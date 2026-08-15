using AiGateway.Api.Models;
using AiGateway.Api.Options;

namespace AiGateway.Tests.TestSupport;

/// <summary>Shared configuration for tests: a 4-tier model ladder (premium/standard/economy/local)
/// across two "real" provider names plus a local fallback, mirroring appsettings.json but with
/// small, deterministic numbers so tests don't need to reason about production-sized budgets.</summary>
internal static class GatewayTestFactory
{
    public const string ProviderA = "ProviderA";
    public const string ProviderB = "ProviderB";
    public const string ProviderLocal = "Local";

    public static AiGatewayOptions DefaultOptions() => new()
    {
        DefaultModel = "standard",
        MaxInputTokens = 8000,
        MaxOutputTokens = 2000,
        MaxRetries = 2,
        ProviderHealthWindowSize = 5,
        ProviderDegradedThreshold = 0.2,
        ProviderUnavailableThreshold = 0.5,
        ProviderConsecutiveFailuresForUnavailable = 3,
        BudgetPressureThreshold = 0.25,
        CapabilityTierOrder = new List<string> { "premium", "standard", "economy" },
        Models = new Dictionary<string, ModelOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["economy"] = new ModelOptions
            {
                Provider = ProviderA,
                ProviderModelId = "economy-model",
                Capability = RequestCapability.Simple,
                InputCostPerMillionTokens = 0.15m,
                OutputCostPerMillionTokens = 0.60m,
                Priority = 3,
                MaxContextTokens = 16_000,
                TimeoutSeconds = 5,
                FallbackModel = "local"
            },
            ["standard"] = new ModelOptions
            {
                Provider = ProviderA,
                ProviderModelId = "standard-model",
                Capability = RequestCapability.Standard,
                InputCostPerMillionTokens = 2.50m,
                OutputCostPerMillionTokens = 10.00m,
                Priority = 2,
                MaxContextTokens = 32_000,
                TimeoutSeconds = 5,
                FallbackModel = "economy"
            },
            ["premium"] = new ModelOptions
            {
                Provider = ProviderB,
                ProviderModelId = "premium-model",
                Capability = RequestCapability.Complex,
                InputCostPerMillionTokens = 15.00m,
                OutputCostPerMillionTokens = 75.00m,
                Priority = 1,
                MaxContextTokens = 64_000,
                TimeoutSeconds = 5,
                FallbackModel = "standard"
            },
            ["local"] = new ModelOptions
            {
                Provider = ProviderLocal,
                ProviderModelId = "local-model",
                Capability = RequestCapability.Simple,
                InputCostPerMillionTokens = 0,
                OutputCostPerMillionTokens = 0,
                Priority = 4,
                MaxContextTokens = 8_000,
                TimeoutSeconds = 5,
                FallbackModel = null
            }
        }
    };

    public static TenantOptions Tenant(int dailyBudget = 100_000, IEnumerable<string>? allowedModels = null) => new()
    {
        ApiKey = "test-key",
        DailyTokenBudget = dailyBudget,
        RequestsPerMinute = 100,
        MaxConcurrentAiRequests = 10,
        AllowedModels = (allowedModels ?? new[] { "economy", "standard", "premium", "local" }).ToList()
    };

    public static ChatTurn UserTurn(string content = "test message") => new(GatewayChatRole.User, content);
}
