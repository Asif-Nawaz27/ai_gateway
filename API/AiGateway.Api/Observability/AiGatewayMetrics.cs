using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AiGateway.Api.Observability;

/// <summary>Custom application metrics for the gateway — these are NOT OpenTelemetry semantic
/// conventions, just this repository's own naming, exported through the standard
/// <see cref="Meter"/> API so any OTel-compatible backend can scrape them. Every instrument name
/// below is deliberately prefixed <c>ai_gateway_</c> to make that "custom, not standard" fact
/// visible in a metrics explorer.</summary>
public sealed class AiGatewayMetrics : IDisposable
{
    public const string MeterName = "AiGateway";

    private readonly Meter _meter;
    private readonly Counter<long> _requestsTotal;
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _providerFailures;
    private readonly Counter<long> _fallbackTotal;
    private readonly Counter<long> _tokensTotal;
    private readonly Counter<double> _estimatedCost;
    private readonly Counter<long> _rateLimitRejections;
    private readonly Counter<long> _budgetRejections;

    public AiGatewayMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        _requestsTotal = _meter.CreateCounter<long>(
            "ai_gateway_requests_total", description: "Custom metric: gateway requests processed, tagged by tenant/provider/model/fallback.");
        _requestDuration = _meter.CreateHistogram<double>(
            "ai_gateway_request_duration", unit: "ms", description: "Custom metric: gateway request duration in milliseconds.");
        _providerFailures = _meter.CreateCounter<long>(
            "ai_gateway_provider_failures", description: "Custom metric: provider call failures, tagged by provider/model/failure kind.");
        _fallbackTotal = _meter.CreateCounter<long>(
            "ai_gateway_fallback_total", description: "Custom metric: requests that used a fallback model.");
        _tokensTotal = _meter.CreateCounter<long>(
            "ai_gateway_tokens_total", description: "Custom metric: input+output tokens consumed, tagged by tenant/provider/model/direction.");
        _estimatedCost = _meter.CreateCounter<double>(
            "ai_gateway_estimated_cost", unit: "usd", description: "Custom metric: estimated cost using configured illustrative pricing.");
        _rateLimitRejections = _meter.CreateCounter<long>(
            "ai_gateway_rate_limit_rejections", description: "Custom metric: requests rejected by ASP.NET Core rate limiting.");
        _budgetRejections = _meter.CreateCounter<long>(
            "ai_gateway_budget_rejections", description: "Custom metric: requests rejected due to token-budget or routing constraints.");
    }

    public void RecordRequest(string tenantId, string provider, string model, bool fallbackUsed, int retryCount, TimeSpan duration)
    {
        var tags = new TagList { { "tenant", tenantId }, { "provider", provider }, { "model", model }, { "fallback", fallbackUsed } };
        _requestsTotal.Add(1, tags);
        _requestDuration.Record(duration.TotalMilliseconds, tags);
    }

    public void RecordProviderFailure(string provider, string model, string kind) =>
        _providerFailures.Add(1, new TagList { { "provider", provider }, { "model", model }, { "kind", kind } });

    public void RecordFallback(string tenantId, string provider, string model) =>
        _fallbackTotal.Add(1, new TagList { { "tenant", tenantId }, { "provider", provider }, { "model", model } });

    public void RecordTokens(string tenantId, string provider, string model, int inputTokens, int outputTokens)
    {
        _tokensTotal.Add(inputTokens, new TagList { { "tenant", tenantId }, { "provider", provider }, { "model", model }, { "direction", "input" } });
        _tokensTotal.Add(outputTokens, new TagList { { "tenant", tenantId }, { "provider", provider }, { "model", model }, { "direction", "output" } });
    }

    public void RecordCost(string tenantId, string provider, string model, double cost) =>
        _estimatedCost.Add(cost, new TagList { { "tenant", tenantId }, { "provider", provider }, { "model", model } });

    public void RecordRateLimitRejection(string tenantId, string policy) =>
        _rateLimitRejections.Add(1, new TagList { { "tenant", tenantId }, { "policy", policy } });

    public void RecordBudgetRejection(string tenantId, string reason) =>
        _budgetRejections.Add(1, new TagList { { "tenant", tenantId }, { "reason", reason } });

    public void Dispose() => _meter.Dispose();
}
