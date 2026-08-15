namespace AiGateway.Api.Options;

public sealed class AiGatewayOptions
{
    public const string SectionName = "AiGateway";

    public string DefaultModel { get; set; } = "standard";
    public int MaxInputTokens { get; set; } = 8_000;
    public int MaxOutputTokens { get; set; } = 2_000;

    /// <summary>Bounded retry attempts per model, before the fallback chain moves to the next model.</summary>
    public int MaxRetries { get; set; } = 2;

    public int ProviderHealthWindowSize { get; set; } = 10;
    public double ProviderDegradedThreshold { get; set; } = 0.2;
    public double ProviderUnavailableThreshold { get; set; } = 0.5;
    public int ProviderConsecutiveFailuresForUnavailable { get; set; } = 3;

    /// <summary>Below this fraction of a tenant's remaining daily token budget, ModelRouter
    /// starts steering new requests toward cheaper tiers (see CapabilityTierOrder) to stretch
    /// the remaining allowance, rather than waiting for the budget to be fully exhausted.</summary>
    public double BudgetPressureThreshold { get; set; } = 0.25;

    /// <summary>Cost ladder used only for budget-pressure downgrades, from most to least
    /// expensive. Deliberately separate from each ModelOptions.FallbackModel chain (which drives
    /// health/context-fit fallback) — cost ordering and failover wiring are different concerns
    /// that happen to often agree, but don't have to.</summary>
    public List<string> CapabilityTierOrder { get; set; } = new() { "premium", "standard", "economy" };

    public Dictionary<string, ModelOptions> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TenantOptions> Tenants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
