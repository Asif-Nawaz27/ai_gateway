using AiGateway.Api.Gateway;
using AiGateway.Api.Models;
using AiGateway.Api.Options;
using AiGateway.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

/// <summary>Exercises ModelRouter against the decision table described in the article. Uses the
/// real TokenBudgetService and ProviderHealthService (not mocks) since both are simple,
/// deterministic, in-memory classes — using the real thing here is more informative than mocking
/// it, and avoids taking a dependency on a mocking framework for something this simple.</summary>
public sealed class RoutingTests
{
    private static ModelRouter CreateRouter(out ProviderHealthService health, out TokenBudgetService budget, AiGatewayOptions? options = null)
    {
        var opts = Options.Create(options ?? GatewayTestFactory.DefaultOptions());
        health = new ProviderHealthService(opts);
        budget = new TokenBudgetService();
        return new ModelRouter(opts, health, budget);
    }

    [Fact]
    public void Simple_capability_with_ample_budget_selects_economy()
    {
        var router = CreateRouter(out _, out _);
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 100_000);

        var decision = router.Route(new RoutingRequest(
            "tenant-x", tenant, "auto", RequestCapability.Simple, RequestPriority.Normal, EstimatedInputTokens: 50, MaxOutputTokens: 200));

        Assert.True(decision.Success);
        Assert.Equal("economy", decision.SelectedModelKey);
        Assert.False(decision.FallbackUsed);
    }

    [Fact]
    public void Standard_capability_with_ample_budget_selects_standard()
    {
        var router = CreateRouter(out _, out _);
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 100_000);

        var decision = router.Route(new RoutingRequest(
            "tenant-x", tenant, "auto", RequestCapability.Standard, RequestPriority.Normal, EstimatedInputTokens: 50, MaxOutputTokens: 200));

        Assert.True(decision.Success);
        Assert.Equal("standard", decision.SelectedModelKey);
    }

    [Fact]
    public void Complex_capability_with_ample_budget_selects_premium()
    {
        var router = CreateRouter(out _, out _);
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 100_000);

        var decision = router.Route(new RoutingRequest(
            "tenant-x", tenant, "auto", RequestCapability.Complex, RequestPriority.Normal, EstimatedInputTokens: 50, MaxOutputTokens: 200));

        Assert.True(decision.Success);
        Assert.Equal("premium", decision.SelectedModelKey);
        Assert.False(decision.FallbackUsed);
    }

    [Fact]
    public void Complex_capability_under_budget_pressure_at_normal_priority_downgrades_one_tier_to_standard()
    {
        var router = CreateRouter(out _, out var budget);
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 1000);
        // Drain the budget below the 25% pressure threshold (i.e. below 250 of 1000 remaining).
        budget.Reserve("tenant-x", tenant, estimatedInputTokens: 700, maxOutputTokens: 100); // 800 reserved -> 200 remaining (20%)

        var decision = router.Route(new RoutingRequest(
            "tenant-x", tenant, "auto", RequestCapability.Complex, RequestPriority.Normal, EstimatedInputTokens: 10, MaxOutputTokens: 20));

        Assert.True(decision.Success);
        Assert.Equal("standard", decision.SelectedModelKey);
        Assert.Equal("premium", decision.DesiredModelKey);
        Assert.True(decision.FallbackUsed);
    }

    [Fact]
    public void Complex_capability_under_budget_pressure_at_low_priority_downgrades_to_cheapest_tier()
    {
        var router = CreateRouter(out _, out var budget);
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 1000);
        budget.Reserve("tenant-x", tenant, estimatedInputTokens: 700, maxOutputTokens: 100); // 20% remaining

        var decision = router.Route(new RoutingRequest(
            "tenant-x", tenant, "auto", RequestCapability.Complex, RequestPriority.Low, EstimatedInputTokens: 10, MaxOutputTokens: 20));

        Assert.True(decision.Success);
        Assert.Equal("economy", decision.SelectedModelKey);
    }

    [Fact]
    public void Complex_capability_under_budget_pressure_at_high_priority_keeps_premium()
    {
        var router = CreateRouter(out _, out var budget);
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 1000);
        budget.Reserve("tenant-x", tenant, estimatedInputTokens: 700, maxOutputTokens: 100); // 20% remaining

        var decision = router.Route(new RoutingRequest(
            "tenant-x", tenant, "auto", RequestCapability.Complex, RequestPriority.High, EstimatedInputTokens: 10, MaxOutputTokens: 20));

        Assert.True(decision.Success);
        Assert.Equal("premium", decision.SelectedModelKey);
        Assert.False(decision.FallbackUsed);
    }

    [Fact]
    public void Request_exceeding_remaining_budget_outright_is_rejected_regardless_of_tier()
    {
        var router = CreateRouter(out _, out var budget);
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 100);
        budget.Reserve("tenant-x", tenant, estimatedInputTokens: 90, maxOutputTokens: 5); // 5 remaining

        var decision = router.Route(new RoutingRequest(
            "tenant-x", tenant, "auto", RequestCapability.Simple, RequestPriority.Low, EstimatedInputTokens: 10, MaxOutputTokens: 20));

        Assert.False(decision.Success);
        Assert.Equal(GatewayRejectionReason.BudgetExceeded, decision.RejectionReason);
    }

    [Fact]
    public void Premium_unavailable_falls_back_through_the_health_chain()
    {
        var router = CreateRouter(out var health, out _);
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 100_000);

        health.RecordFailure(GatewayTestFactory.ProviderB);
        health.RecordFailure(GatewayTestFactory.ProviderB);
        health.RecordFailure(GatewayTestFactory.ProviderB); // 3 consecutive -> Unavailable

        var decision = router.Route(new RoutingRequest(
            "tenant-x", tenant, "auto", RequestCapability.Complex, RequestPriority.High, EstimatedInputTokens: 50, MaxOutputTokens: 200));

        Assert.True(decision.Success);
        Assert.Equal("standard", decision.SelectedModelKey);
        Assert.Equal("premium", decision.DesiredModelKey);
        Assert.True(decision.FallbackUsed);
    }

    [Fact]
    public void Explicit_model_request_outside_tenant_allowlist_is_rejected_not_downgraded()
    {
        var router = CreateRouter(out _, out _);
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 100_000, allowedModels: new[] { "economy", "standard", "local" });

        var decision = router.Route(new RoutingRequest(
            "tenant-x", tenant, "premium", RequestCapability.Standard, RequestPriority.Normal, EstimatedInputTokens: 50, MaxOutputTokens: 200));

        Assert.False(decision.Success);
        Assert.Equal(GatewayRejectionReason.ModelNotPermitted, decision.RejectionReason);
    }

    [Fact]
    public void Unknown_explicit_model_is_rejected_as_validation_error()
    {
        var router = CreateRouter(out _, out _);
        var tenant = GatewayTestFactory.Tenant();

        var decision = router.Route(new RoutingRequest(
            "tenant-x", tenant, "does-not-exist", RequestCapability.Standard, RequestPriority.Normal, EstimatedInputTokens: 50, MaxOutputTokens: 200));

        Assert.False(decision.Success);
        Assert.Equal(GatewayRejectionReason.Validation, decision.RejectionReason);
    }

    [Fact]
    public void Estimate_exceeding_every_reachable_models_context_window_is_rejected()
    {
        var router = CreateRouter(out _, out _);
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 1_000_000, allowedModels: new[] { "economy", "local" });

        // economy's context is 16,000 and its only fallback (local) is 8,000 — this exceeds both.
        var decision = router.Route(new RoutingRequest(
            "tenant-x", tenant, "auto", RequestCapability.Simple, RequestPriority.Normal, EstimatedInputTokens: 15_990, MaxOutputTokens: 2_000));

        Assert.False(decision.Success);
        Assert.Equal(GatewayRejectionReason.NoHealthyProvider, decision.RejectionReason);
    }
}
