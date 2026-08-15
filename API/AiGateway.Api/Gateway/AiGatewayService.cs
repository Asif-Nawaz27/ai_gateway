using System.Diagnostics;
using AiGateway.Api.Models;
using AiGateway.Api.Observability;
using AiGateway.Api.Options;
using AiGateway.Api.Providers;
using Microsoft.Extensions.Options;
using Polly;

namespace AiGateway.Api.Gateway;

public abstract record GatewayResult;

public sealed record GatewaySuccess(ChatCompletionApiResponse Response) : GatewayResult;

public sealed record GatewayRejected(GatewayRejectionReason Reason, string Message, string RequestId) : GatewayResult;

public sealed record GatewayFailed(string Message, string RequestId, int AttemptsMade) : GatewayResult;

/// <summary>Orchestrates a single chat request end to end: validate → estimate tokens → route →
/// reserve budget → call the selected model with bounded retry → fall back to the next model in
/// the chain on a retry-exhausted-or-non-retryable-but-fallback-eligible failure → record
/// usage/cost/health/telemetry. This is the "gateway" in "AI Gateway" — everything upstream of
/// this class (middleware, rate limiting) has already run; everything below it is provider I/O.</summary>
public sealed class AiGatewayService : IAiGateway
{
    private readonly IModelRouter _router;
    private readonly ITokenBudgetService _budget;
    private readonly ITokenEstimator _estimator;
    private readonly ICostCalculator _cost;
    private readonly IProviderHealthService _health;
    private readonly IReadOnlyDictionary<string, IAiProvider> _providers;
    private readonly AiGatewayOptions _options;
    private readonly AiGatewayMetrics _metrics;
    private readonly ILogger<AiGatewayService> _logger;

    public AiGatewayService(
        IModelRouter router,
        ITokenBudgetService budget,
        ITokenEstimator estimator,
        ICostCalculator cost,
        IProviderHealthService health,
        IEnumerable<IAiProvider> providers,
        IOptions<AiGatewayOptions> options,
        AiGatewayMetrics metrics,
        ILogger<AiGatewayService> logger)
    {
        _router = router;
        _budget = budget;
        _estimator = estimator;
        _cost = cost;
        _health = health;
        _providers = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<GatewayResult> ProcessAsync(
        ChatCompletionApiRequest apiRequest,
        string tenantId,
        TenantOptions tenant,
        CancellationToken cancellationToken)
    {
        var requestId = $"req_{Guid.NewGuid():N}";
        using var activity = AiGatewayTelemetry.ActivitySource.StartActivity("ai_gateway.process_request");
        var stopwatch = Stopwatch.StartNew();
        activity?.SetTag("gateway.request_id", requestId);
        activity?.SetTag("gateway.tenant_id", tenantId);

        if (apiRequest.Messages is null || apiRequest.Messages.Count == 0)
        {
            _metrics.RecordBudgetRejection(tenantId, "Validation");
            return new GatewayRejected(GatewayRejectionReason.Validation, "At least one message is required.", requestId);
        }

        var turns = apiRequest.Messages.Select(m => new ChatTurn(ParseRole(m.Role), m.Content)).ToList();
        var estimatedInputTokens = _estimator.EstimateInputTokens(turns);
        var maxOutputTokens = Math.Clamp(apiRequest.MaxTokens ?? _options.MaxOutputTokens, 1, _options.MaxOutputTokens);

        if (estimatedInputTokens > _options.MaxInputTokens)
        {
            _metrics.RecordBudgetRejection(tenantId, "ContextTooLarge");
            return new GatewayRejected(
                GatewayRejectionReason.ContextTooLarge,
                $"Estimated input ({estimatedInputTokens} tokens) exceeds the gateway maximum of {_options.MaxInputTokens} tokens.",
                requestId);
        }

        var capability = ParseCapability(apiRequest.Capability);
        var priority = ParsePriority(apiRequest.Priority);

        var decision = _router.Route(new RoutingRequest(
            tenantId, tenant, apiRequest.Model, capability, priority, estimatedInputTokens, maxOutputTokens));

        activity?.SetTag("gateway.routing_reason", decision.Reason);
        _logger.LogInformation(
            "Routing decision for {RequestId}: success={Success} model={Model} fallback={Fallback} reason={Reason}",
            requestId, decision.Success, decision.SelectedModelKey, decision.FallbackUsed, decision.Reason);

        if (!decision.Success || decision.SelectedModel is null || decision.SelectedModelKey is null)
        {
            _metrics.RecordBudgetRejection(tenantId, decision.RejectionReason.ToString());
            return new GatewayRejected(decision.RejectionReason, decision.Reason, requestId);
        }

        BudgetReservation reservation;
        try
        {
            reservation = _budget.Reserve(tenantId, tenant, estimatedInputTokens, maxOutputTokens);
        }
        catch (BudgetExceededException ex)
        {
            // The router's budget check was advisory (read remaining, then chose a tier); this
            // is the hard enforcement point. A concurrent request can race between the two, in
            // which case the reservation loses and the request is rejected here instead of
            // silently re-routed — documented trade-off, see the token-budgets article section.
            _metrics.RecordBudgetRejection(tenantId, "BudgetExceeded");
            return new GatewayRejected(GatewayRejectionReason.BudgetExceeded, ex.Message, requestId);
        }

        var normalized = new NormalizedChatRequest(tenantId, requestId, turns, maxOutputTokens, estimatedInputTokens);

        var retryCount = 0;
        var fallbackUsed = decision.FallbackUsed;
        var attempts = 0;
        var attemptLog = new List<string>();

        var currentKey = decision.SelectedModelKey;
        var currentModel = decision.SelectedModel;

        while (currentKey is not null && currentModel is not null)
        {
            if (!_providers.TryGetValue(currentModel.Provider, out var provider))
            {
                attemptLog.Add($"{currentKey}: provider '{currentModel.Provider}' not registered.");
                (currentKey, currentModel) = Advance(currentModel, tenant, normalized);
                fallbackUsed = true;
                continue;
            }

            attempts++;
            var (succeeded, result, error, retriesUsed) =
                await TryModelWithRetriesAsync(provider, currentKey, currentModel, normalized, requestId, cancellationToken);
            retryCount += retriesUsed;

            if (succeeded && result is not null)
            {
                _health.RecordSuccess(provider.Name);
                _budget.Commit(tenantId, reservation.ReservationId, result.Usage.TotalTokens);
                var costAmount = _cost.Calculate(currentModel, result.Usage);

                stopwatch.Stop();
                _metrics.RecordRequest(tenantId, provider.Name, currentKey, fallbackUsed, retryCount, stopwatch.Elapsed);
                _metrics.RecordTokens(tenantId, provider.Name, currentKey, result.Usage.InputTokens, result.Usage.OutputTokens);
                _metrics.RecordCost(tenantId, provider.Name, currentKey, (double)costAmount);
                if (fallbackUsed)
                {
                    _metrics.RecordFallback(tenantId, provider.Name, currentKey);
                }

                activity?.SetTag("gateway.model", currentKey);
                activity?.SetTag("gateway.provider", provider.Name);
                activity?.SetTag("gateway.fallback_used", fallbackUsed);
                activity?.SetTag("gateway.retry_count", retryCount);

                var response = new ChatCompletionApiResponse(
                    requestId,
                    currentKey,
                    provider.Name,
                    new UsageDto(result.Usage.InputTokens, result.Usage.OutputTokens, result.Usage.TotalTokens, Estimated: false),
                    costAmount,
                    fallbackUsed,
                    retryCount,
                    decision.Reason,
                    result.Content);

                return new GatewaySuccess(response);
            }

            _health.RecordFailure(provider.Name);
            _metrics.RecordProviderFailure(provider.Name, currentKey, error?.Kind.ToString() ?? "Unknown");
            attemptLog.Add($"{currentKey}/{provider.Name}: {error?.Kind} - {error?.Message}");

            if (error is not null && !IsFallbackEligible(error.Kind))
            {
                _budget.Release(tenantId, reservation.ReservationId);
                return new GatewayFailed(
                    $"Request failed on '{currentKey}' with a non-recoverable error ({error.Kind}): {error.Message}", requestId, attempts);
            }

            (currentKey, currentModel) = Advance(currentModel, tenant, normalized);
            fallbackUsed = true;
        }

        _budget.Release(tenantId, reservation.ReservationId);
        _metrics.RecordBudgetRejection(tenantId, "FallbackChainExhausted");
        return new GatewayFailed(
            $"All providers in the fallback chain failed. Attempts: {string.Join(" | ", attemptLog)}", requestId, attempts);
    }

    /// <summary>Walks to the next model in the fallback chain after a live call failure, skipping
    /// (not stopping at) any model the tenant isn't allowed to use or that can't fit this
    /// request's context — the same constraints ModelRouter applies when choosing the *starting*
    /// model. Without this, a live failure could silently hand a tenant a model outside their
    /// allowlist just because it happened to be next in some other model's fallback chain.</summary>
    private (string? Key, ModelOptions? Model) Advance(ModelOptions current, TenantOptions tenant, NormalizedChatRequest request)
    {
        var cursor = current;
        var guard = 0;

        while (!string.IsNullOrEmpty(cursor.FallbackModel) && guard++ < _options.Models.Count + 1)
        {
            if (!_options.Models.TryGetValue(cursor.FallbackModel, out var next))
            {
                return (null, null);
            }

            var fitsContext = request.EstimatedInputTokens + request.MaxOutputTokens <= next.MaxContextTokens;
            if (tenant.AllowedModels.Contains(cursor.FallbackModel, StringComparer.OrdinalIgnoreCase) && fitsContext)
            {
                return (cursor.FallbackModel, next);
            }

            cursor = next;
        }

        return (null, null);
    }

    private async Task<(bool Succeeded, ProviderCompletionResult? Result, AiProviderException? Error, int RetriesUsed)> TryModelWithRetriesAsync(
        IAiProvider provider,
        string modelKey,
        ModelOptions model,
        NormalizedChatRequest request,
        string requestId,
        CancellationToken cancellationToken)
    {
        var retries = 0;

        var pipeline = new ResiliencePipelineBuilder<ProviderCompletionResult>()
            .AddRetry(new Polly.Retry.RetryStrategyOptions<ProviderCompletionResult>
            {
                MaxRetryAttempts = _options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(200),
                ShouldHandle = new PredicateBuilder<ProviderCompletionResult>()
                    .Handle<AiProviderException>(ex => IsRetryable(ex.Kind)),
                OnRetry = args =>
                {
                    retries++;
                    _logger.LogWarning(
                        "Retrying {RequestId} on {Provider}/{Model} after {Kind} (attempt {Attempt}, delay {DelayMs:F0}ms).",
                        requestId, provider.Name, modelKey,
                        (args.Outcome.Exception as AiProviderException)?.Kind, args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

        try
        {
            var result = await pipeline.ExecuteAsync(
                async ct => await provider.CompleteAsync(request, model.ProviderModelId, request.MaxOutputTokens, TimeSpan.FromSeconds(model.TimeoutSeconds), ct),
                cancellationToken);
            return (true, result, null, retries);
        }
        catch (AiProviderException ex)
        {
            return (false, null, ex, retries);
        }
    }

    // Retryable: transient, provider-side conditions where trying again (or the next provider)
    // stands a real chance of succeeding.
    private static bool IsRetryable(ProviderFailureKind kind) => kind is
        ProviderFailureKind.RateLimited or ProviderFailureKind.ServerError or ProviderFailureKind.Timeout;

    // Fallback-eligible: even if we won't retry the SAME model, it's still worth trying the NEXT
    // model in the chain. Excluded: authentication failures and cancellations (won't be fixed by
    // calling a different model), and malformed CLIENT requests (HTTP 400 — the same malformed
    // request would fail identically on every provider). InvalidResponse (a malformed reply to an
    // otherwise-valid request) stays fallback-eligible, since that's plausibly provider-specific.
    private static bool IsFallbackEligible(ProviderFailureKind kind) => kind is not (
        ProviderFailureKind.AuthenticationFailed or ProviderFailureKind.Canceled or ProviderFailureKind.InvalidRequest);

    private static GatewayChatRole ParseRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => GatewayChatRole.System,
        "assistant" => GatewayChatRole.Assistant,
        _ => GatewayChatRole.User
    };

    private static RequestCapability ParseCapability(string? capability) => capability?.ToLowerInvariant() switch
    {
        "simple" => RequestCapability.Simple,
        "complex" => RequestCapability.Complex,
        _ => RequestCapability.Standard
    };

    private static RequestPriority ParsePriority(string? priority) => priority?.ToLowerInvariant() switch
    {
        "low" => RequestPriority.Low,
        "high" => RequestPriority.High,
        _ => RequestPriority.Normal
    };
}

public interface IAiGateway
{
    Task<GatewayResult> ProcessAsync(ChatCompletionApiRequest apiRequest, string tenantId, TenantOptions tenant, CancellationToken cancellationToken);
}
