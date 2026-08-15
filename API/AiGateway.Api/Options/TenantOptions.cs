namespace AiGateway.Api.Options;

/// <summary>Per-tenant configuration: budget, rate limits, and the model allowlist. In this
/// sample, the API key doubles as tenant identification — see the security section of the
/// article for why that's a simplification, not a production auth scheme.</summary>
public sealed class TenantOptions
{
    public required string ApiKey { get; set; }

    /// <summary>Rolling 24-hour token budget (see TokenBudgetService), not a calendar-day reset.</summary>
    public int DailyTokenBudget { get; set; } = 100_000;

    public int RequestsPerMinute { get; set; } = 30;
    public int MaxConcurrentAiRequests { get; set; } = 5;

    /// <summary>Explicit allowlist of model keys this tenant may use. Requesting a model outside
    /// this list is rejected, not silently downgraded — see Section 20 (model-selection abuse).</summary>
    public List<string> AllowedModels { get; set; } = new();
}
