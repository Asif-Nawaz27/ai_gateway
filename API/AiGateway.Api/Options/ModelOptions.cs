using AiGateway.Api.Models;

namespace AiGateway.Api.Options;

/// <summary>Configuration for one routable model tier (e.g. "economy", "standard", "premium").
/// Costs are illustrative demo values — see appsettings.json — not live provider pricing.</summary>
public sealed class ModelOptions
{
    public required string Provider { get; set; }
    public required string ProviderModelId { get; set; }
    public RequestCapability Capability { get; set; } = RequestCapability.Standard;
    public decimal InputCostPerMillionTokens { get; set; }
    public decimal OutputCostPerMillionTokens { get; set; }

    /// <summary>Lower numbers are preferred when several tiers are otherwise equally eligible.</summary>
    public int Priority { get; set; } = 5;

    public int MaxContextTokens { get; set; } = 16_000;
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Next model key to try when this one is unhealthy, over budget, or the tenant
    /// isn't permitted to use it. Forms the fallback chain walked by <c>ModelRouter</c>.</summary>
    public string? FallbackModel { get; set; }
}
