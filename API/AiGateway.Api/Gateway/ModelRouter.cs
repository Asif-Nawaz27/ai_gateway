using AiGateway.Api.Models;
using AiGateway.Api.Options;
using Microsoft.Extensions.Options;

namespace AiGateway.Api.Gateway;

public sealed record RoutingRequest(
    string TenantId,
    TenantOptions Tenant,
    string? RequestedModelKey,
    RequestCapability Capability,
    RequestPriority Priority,
    int EstimatedInputTokens,
    int MaxOutputTokens);

public sealed record RoutingDecision(
    bool Success,
    string? SelectedModelKey,
    ModelOptions? SelectedModel,
    string? DesiredModelKey,
    bool FallbackUsed,
    GatewayRejectionReason RejectionReason,
    string Reason,
    IReadOnlyList<string> Trace);

public interface IModelRouter
{
    RoutingDecision Route(RoutingRequest request);
}

/// <summary>Deterministic routing policy. No machine-learning classification, no hidden
/// heuristics on request text — every decision is explainable from configuration plus three
/// pieces of runtime state (tenant remaining budget, provider health, tenant allowlist). See the
/// article's routing-policy section for the decision table this class is built to satisfy.
///
/// A design note worth stating explicitly, because it's easy to get wrong: since
/// <c>TokenBudgetService</c> tracks raw token *volume*, switching a request from Premium to
/// Economy does NOT reduce how many tokens that specific request consumes — a fixed
/// max-output-tokens costs the same tokens regardless of which model answers it. So budget can
/// drive two genuinely different decisions here, and this class keeps them separate:
///  - If the request's estimated tokens don't fit the tenant's remaining budget AT ALL, no tier
///    change can help — that's a hard rejection (GatewayRejectionReason.BudgetExceeded),
///    independent of capability or priority.
///  - If the tenant is simply running low (remaining budget below a configured percentage of
///    their total), the router pre-emptively steers *future* tier selection toward cheaper
///    models — via <see cref="AiGatewayOptions.CapabilityTierOrder"/> — to stretch the
///    remaining allowance across more requests, before the hard limit is ever hit. How far
///    depends on RequestPriority (Low: all the way to the cheapest tier; Normal: one tier; High:
///    not at all — a high-priority request keeps its tier under budget pressure and is only
///    ever rejected outright once the hard limit above is reached).
///
/// Decision order:
///  1. Resolve a *desired* tier: an explicit client request (validated against the tenant
///     allowlist), or the client's declared capability mapped to a baseline tier
///     (Simple→economy, Standard→standard, Complex→premium).
///  2. Hard budget check (see above).
///  3. Budget-pressure tier downgrade (see above).
///  4. Walk the fallback chain starting at that tier, skipping any model whose provider is
///     Unavailable, whose context window can't fit the estimate, or that the tenant isn't
///     allowed to use, until one fits or the chain is exhausted.</summary>
public sealed class ModelRouter : IModelRouter
{
    private readonly AiGatewayOptions _options;
    private readonly IProviderHealthService _health;
    private readonly ITokenBudgetService _budget;

    public ModelRouter(IOptions<AiGatewayOptions> options, IProviderHealthService health, ITokenBudgetService budget)
    {
        _options = options.Value;
        _health = health;
        _budget = budget;
    }

    public RoutingDecision Route(RoutingRequest request)
    {
        var trace = new List<string>();

        var desiredKey = ResolveDesiredTier(request, trace, out var rejection, out var rejectionMessage);
        if (desiredKey is null)
        {
            return Reject(rejection, rejectionMessage!, trace);
        }

        var status = _budget.GetStatus(request.TenantId, request.Tenant);
        var required = request.EstimatedInputTokens + request.MaxOutputTokens;

        if (required > status.RemainingTokens)
        {
            trace.Add(
                $"Estimated usage ({required} tokens) exceeds remaining tenant budget ({status.RemainingTokens} tokens); " +
                "no tier change can help, since every tier consumes the same token volume for a fixed max-output.");
            return Reject(GatewayRejectionReason.BudgetExceeded,
                $"Tenant '{request.TenantId}' has insufficient remaining token budget ({status.RemainingTokens} remaining, {required} required).",
                trace);
        }

        var startKey = desiredKey;
        var remainingFraction = request.Tenant.DailyTokenBudget == 0
            ? 0
            : status.RemainingTokens / (double)request.Tenant.DailyTokenBudget;

        if (remainingFraction < _options.BudgetPressureThreshold)
        {
            trace.Add($"Tenant has {remainingFraction:P0} of its token budget remaining (below the {_options.BudgetPressureThreshold:P0} pressure threshold).");
            startKey = DowngradeForBudgetPressure(request, desiredKey, trace);
        }

        return WalkFallbackChain(request, desiredKey, startKey, trace);
    }

    private string? ResolveDesiredTier(RoutingRequest request, List<string> trace, out GatewayRejectionReason rejection, out string? rejectionMessage)
    {
        rejection = GatewayRejectionReason.None;
        rejectionMessage = null;

        if (!string.IsNullOrWhiteSpace(request.RequestedModelKey) &&
            !string.Equals(request.RequestedModelKey, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!_options.Models.ContainsKey(request.RequestedModelKey))
            {
                rejection = GatewayRejectionReason.Validation;
                rejectionMessage = $"Unknown model '{request.RequestedModelKey}'.";
                return null;
            }

            if (!request.Tenant.AllowedModels.Contains(request.RequestedModelKey, StringComparer.OrdinalIgnoreCase))
            {
                rejection = GatewayRejectionReason.ModelNotPermitted;
                rejectionMessage = $"Tenant '{request.TenantId}' is not permitted to use model '{request.RequestedModelKey}'.";
                return null;
            }

            trace.Add($"Client explicitly requested model '{request.RequestedModelKey}'.");
            return request.RequestedModelKey;
        }

        var baseline = request.Capability switch
        {
            RequestCapability.Simple => "economy",
            RequestCapability.Complex => "premium",
            _ => "standard"
        };

        if (_options.Models.ContainsKey(baseline) && request.Tenant.AllowedModels.Contains(baseline, StringComparer.OrdinalIgnoreCase))
        {
            trace.Add($"Capability '{request.Capability}' mapped to baseline tier '{baseline}'.");
            return baseline;
        }

        var clamped = request.Tenant.AllowedModels
            .Where(m => _options.Models.ContainsKey(m))
            .OrderBy(m => _options.Models[m].Priority)
            .FirstOrDefault();

        if (clamped is null)
        {
            rejection = GatewayRejectionReason.ModelNotPermitted;
            rejectionMessage = $"Tenant '{request.TenantId}' has no permitted models configured.";
            return null;
        }

        trace.Add($"Capability '{request.Capability}' baseline tier '{baseline}' not permitted for tenant; clamped to '{clamped}'.");
        return clamped;
    }

    /// <summary>Steers tier selection down <c>AiGatewayOptions.CapabilityTierOrder</c> (an
    /// explicit cost ladder — premium/standard/economy by default — independent of the
    /// health-fallback chain) by a priority-scaled number of hops. If the desired tier isn't on
    /// the ladder at all (e.g. it was an explicit non-standard model request), budget pressure
    /// has nothing to act on and the desired tier passes through unchanged.</summary>
    private string DowngradeForBudgetPressure(RoutingRequest request, string desiredKey, List<string> trace)
    {
        var tierOrder = _options.CapabilityTierOrder;
        var desiredIndex = tierOrder.FindIndex(k => string.Equals(k, desiredKey, StringComparison.OrdinalIgnoreCase));
        if (desiredIndex < 0)
        {
            return desiredKey;
        }

        var maxHops = request.Priority switch
        {
            RequestPriority.High => 0,
            RequestPriority.Normal => Math.Min(1, tierOrder.Count - 1 - desiredIndex),
            _ => tierOrder.Count - 1 - desiredIndex
        };

        var targetIndex = Math.Min(desiredIndex + maxHops, tierOrder.Count - 1);
        var target = tierOrder[targetIndex];

        if (targetIndex != desiredIndex)
        {
            trace.Add($"Budget-pressure downgrade ({request.Priority} priority, {targetIndex - desiredIndex} tier(s) down from '{desiredKey}'): starting at '{target}'.");
        }

        return target;
    }

    private RoutingDecision WalkFallbackChain(RoutingRequest request, string desiredKey, string startKey, List<string> trace)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chain = BuildChain(startKey);

        foreach (var key in chain)
        {
            if (!visited.Add(key))
            {
                continue;
            }

            var model = _options.Models[key];
            var healthStatus = _health.GetStatus(model.Provider);
            var fitsContext = request.EstimatedInputTokens + request.MaxOutputTokens <= model.MaxContextTokens;
            var allowed = request.Tenant.AllowedModels.Contains(key, StringComparer.OrdinalIgnoreCase);

            if (allowed && fitsContext && healthStatus != ProviderHealthStatus.Unavailable)
            {
                var fallbackUsed = !string.Equals(key, desiredKey, StringComparison.OrdinalIgnoreCase);
                trace.Add(fallbackUsed
                    ? $"Selected fallback model '{key}' (provider '{model.Provider}', health {healthStatus})."
                    : $"Selected desired model '{key}' (provider '{model.Provider}', health {healthStatus}).");

                return new RoutingDecision(true, key, model, desiredKey, fallbackUsed, GatewayRejectionReason.None,
                    string.Join(" ", trace), trace);
            }

            trace.Add($"Skipped '{key}': allowed={allowed}, fits-context={fitsContext}, health={healthStatus}.");
        }

        return Reject(GatewayRejectionReason.NoHealthyProvider,
            "No healthy, permitted, context-fitting model was found in the fallback chain.", trace);
    }

    private List<string> BuildChain(string startKey)
    {
        var chain = new List<string>();
        var cursor = startKey;
        var guard = 0;

        while (cursor is not null && _options.Models.TryGetValue(cursor, out var model) && guard++ < _options.Models.Count + 1)
        {
            chain.Add(cursor);
            cursor = model.FallbackModel;
        }

        return chain;
    }

    private static RoutingDecision Reject(GatewayRejectionReason reason, string message, List<string> trace)
    {
        trace.Add(message);
        return new RoutingDecision(false, null, null, null, false, reason, message, trace);
    }
}
