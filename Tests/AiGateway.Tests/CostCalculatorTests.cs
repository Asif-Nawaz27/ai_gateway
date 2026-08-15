using AiGateway.Api.Gateway;
using AiGateway.Api.Models;
using AiGateway.Tests.TestSupport;
using Xunit;

namespace AiGateway.Tests;

public sealed class CostCalculatorTests
{
    [Fact]
    public void Input_and_output_cost_are_calculated_independently()
    {
        var calculator = new CostCalculator();
        var model = GatewayTestFactory.DefaultOptions().Models["standard"]; // 2.50 / 10.00 per million

        var cost = calculator.Calculate(model, new UsageInfo(InputTokens: 1_000_000, OutputTokens: 0));
        Assert.Equal(2.50m, cost);

        var outputOnly = calculator.Calculate(model, new UsageInfo(InputTokens: 0, OutputTokens: 1_000_000));
        Assert.Equal(10.00m, outputOnly);
    }

    [Fact]
    public void Total_cost_is_the_sum_of_input_and_output_cost()
    {
        var calculator = new CostCalculator();
        var model = GatewayTestFactory.DefaultOptions().Models["premium"]; // 15.00 / 75.00 per million

        var cost = calculator.Calculate(model, new UsageInfo(InputTokens: 2_000, OutputTokens: 500));

        // (2000/1_000_000 * 15.00) + (500/1_000_000 * 75.00) = 0.03 + 0.0375
        Assert.Equal(0.0675m, cost);
    }

    [Fact]
    public void Zero_usage_costs_nothing()
    {
        var calculator = new CostCalculator();
        var model = GatewayTestFactory.DefaultOptions().Models["premium"];

        Assert.Equal(0m, calculator.Calculate(model, new UsageInfo(0, 0)));
    }

    [Fact]
    public void Free_local_model_always_costs_zero()
    {
        var calculator = new CostCalculator();
        var model = GatewayTestFactory.DefaultOptions().Models["local"];

        Assert.Equal(0m, calculator.Calculate(model, new UsageInfo(50_000, 50_000)));
    }
}
