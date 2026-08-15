using AiGateway.Api.Models;
using AiGateway.Api.Options;

namespace AiGateway.Api.Gateway;

/// <summary>Pure cost arithmetic over provider-reported usage. Aggregation by tenant, provider,
/// and model is not duplicated here as a second ledger — it's done through the tagged
/// OpenTelemetry counters in Observability/AiGatewayMetrics.cs, which already carry those
/// dimensions and can be queried/aggregated by any metrics backend.</summary>
public interface ICostCalculator
{
    decimal Calculate(ModelOptions model, UsageInfo usage);
}

public sealed class CostCalculator : ICostCalculator
{
    public decimal Calculate(ModelOptions model, UsageInfo usage)
    {
        var inputCost = usage.InputTokens / 1_000_000m * model.InputCostPerMillionTokens;
        var outputCost = usage.OutputTokens / 1_000_000m * model.OutputCostPerMillionTokens;
        return Math.Round(inputCost + outputCost, 6);
    }
}
