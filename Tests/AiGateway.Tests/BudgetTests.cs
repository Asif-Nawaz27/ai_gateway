using AiGateway.Api.Gateway;
using AiGateway.Tests.TestSupport;
using Xunit;

namespace AiGateway.Tests;

public sealed class BudgetTests
{
    [Fact]
    public void Request_within_budget_succeeds_and_reserves_tokens()
    {
        var budget = new TokenBudgetService();
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 1000);

        var reservation = budget.Reserve("tenant-x", tenant, estimatedInputTokens: 100, maxOutputTokens: 200);

        Assert.Equal(300, reservation.ReservedTokens);
        var status = budget.GetStatus("tenant-x", tenant);
        Assert.Equal(300, status.ConsumedTokens);
        Assert.Equal(700, status.RemainingTokens);
    }

    [Fact]
    public void Request_exceeding_per_request_style_budget_is_rejected()
    {
        var budget = new TokenBudgetService();
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 100);

        Assert.Throws<BudgetExceededException>(() => budget.Reserve("tenant-x", tenant, estimatedInputTokens: 80, maxOutputTokens: 50));
    }

    [Fact]
    public void Tenant_exceeding_daily_budget_across_multiple_requests_is_rejected()
    {
        var budget = new TokenBudgetService();
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 500);

        budget.Reserve("tenant-x", tenant, estimatedInputTokens: 200, maxOutputTokens: 100); // 300 used, 200 left
        budget.Reserve("tenant-x", tenant, estimatedInputTokens: 100, maxOutputTokens: 90); // 190 used, 10 left

        Assert.Throws<BudgetExceededException>(() => budget.Reserve("tenant-x", tenant, estimatedInputTokens: 5, maxOutputTokens: 10)); // needs 15, only 10 left
    }

    [Fact]
    public void An_obviously_oversized_request_is_rejected_before_any_provider_call()
    {
        var budget = new TokenBudgetService();
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 8000);

        // Simulates the estimate produced before invoking a provider: a 20,000-token estimate
        // against an 8,000-token budget must never reach a provider call.
        var ex = Assert.Throws<BudgetExceededException>(() => budget.Reserve("tenant-x", tenant, estimatedInputTokens: 18_000, maxOutputTokens: 2_000));
        Assert.Equal(8000, ex.BudgetLimit);
    }

    [Fact]
    public void Commit_replaces_the_reservations_estimate_with_provider_reported_actual_usage()
    {
        var budget = new TokenBudgetService();
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 1000);

        var reservation = budget.Reserve("tenant-x", tenant, estimatedInputTokens: 400, maxOutputTokens: 400); // reserves 800
        budget.Commit("tenant-x", reservation.ReservationId, actualTotalTokens: 250); // provider used far less than estimated

        var status = budget.GetStatus("tenant-x", tenant);
        Assert.Equal(250, status.ConsumedTokens);
    }

    [Fact]
    public void Release_frees_a_reservation_that_never_produced_billable_usage()
    {
        var budget = new TokenBudgetService();
        var tenant = GatewayTestFactory.Tenant(dailyBudget: 1000);

        var reservation = budget.Reserve("tenant-x", tenant, estimatedInputTokens: 400, maxOutputTokens: 400);
        budget.Release("tenant-x", reservation.ReservationId);

        var status = budget.GetStatus("tenant-x", tenant);
        Assert.Equal(0, status.ConsumedTokens);
        Assert.Equal(1000, status.RemainingTokens);
    }

    [Fact]
    public void Budget_is_tracked_independently_per_tenant()
    {
        var budget = new TokenBudgetService();
        var tenantA = GatewayTestFactory.Tenant(dailyBudget: 100);
        var tenantB = GatewayTestFactory.Tenant(dailyBudget: 100);

        budget.Reserve("tenant-a", tenantA, estimatedInputTokens: 90, maxOutputTokens: 5);

        var statusA = budget.GetStatus("tenant-a", tenantA);
        var statusB = budget.GetStatus("tenant-b", tenantB);

        Assert.Equal(5, statusA.RemainingTokens);
        Assert.Equal(100, statusB.RemainingTokens);
    }
}
