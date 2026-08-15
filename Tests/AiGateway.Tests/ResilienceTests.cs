using AiGateway.Api.Gateway;
using AiGateway.Api.Models;
using AiGateway.Api.Observability;
using AiGateway.Api.Providers;
using AiGateway.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

/// <summary>Exercises AiGatewayService's retry/fallback policy against every FakeAiProvider
/// behavior, using zero simulated latency so the tests run fast and deterministically. This is
/// the same failure classification the article's failure-simulation transcript exercises live —
/// these tests are the machine-checked version of that narrative.</summary>
public sealed class ResilienceTests
{
    private sealed record Harness(AiGatewayService Gateway, FakeProviderScript Premium, FakeProviderScript Standard, IProviderHealthService Health);

    private static Harness CreateHarness()
    {
        var options = GatewayTestFactory.DefaultOptions();
        options.MaxRetries = 2;
        var ioptions = Options.Create(options);

        var health = new ProviderHealthService(ioptions);
        var budget = new TokenBudgetService();
        var estimator = new TokenEstimator();
        var cost = new CostCalculator();
        var router = new ModelRouter(ioptions, health, budget);

        var premiumScript = new FakeProviderScript();
        var standardScript = new FakeProviderScript(); // shared by "standard" and "economy" (same Provider name)
        var localScript = new FakeProviderScript();

        IAiProvider[] providers =
        [
            new FakeAiProvider(GatewayTestFactory.ProviderB, premiumScript),
            new FakeAiProvider(GatewayTestFactory.ProviderA, standardScript),
            new FakeAiProvider(GatewayTestFactory.ProviderLocal, localScript)
        ];

        var metrics = new AiGatewayMetrics(new TestMeterFactory());
        var gateway = new AiGatewayService(router, budget, estimator, cost, health, providers, ioptions, metrics, NullLogger<AiGatewayService>.Instance);

        return new Harness(gateway, premiumScript, standardScript, health);
    }

    private static ChatCompletionApiRequest ComplexRequest() => new(
        Messages: new List<ChatMessageDto> { new("user", "Explain the CAP theorem.") },
        Model: "auto",
        Capability: "complex",
        Priority: "normal",
        MaxTokens: 300);

    [Fact]
    public async Task Provider_timeout_exhausts_retries_then_falls_back_to_the_next_model()
    {
        var harness = CreateHarness();
        // MaxRetries=2 means 3 total attempts on the primary model — all must fail to force fallback.
        harness.Premium.Enqueue(FakeBehavior.Timeout, FakeBehavior.Timeout, FakeBehavior.Timeout);

        var tenant = GatewayTestFactory.Tenant();
        var result = await harness.Gateway.ProcessAsync(ComplexRequest(), "tenant-x", tenant, CancellationToken.None);

        var success = Assert.IsType<GatewaySuccess>(result);
        Assert.Equal("standard", success.Response.Model);
        Assert.Equal(GatewayTestFactory.ProviderA, success.Response.Provider);
        Assert.True(success.Response.FallbackUsed);
        Assert.Equal(2, success.Response.RetryCount);
    }

    [Fact]
    public async Task Provider_503_exhausts_retries_then_falls_back()
    {
        var harness = CreateHarness();
        harness.Premium.Enqueue(FakeBehavior.ServiceUnavailable503, FakeBehavior.ServiceUnavailable503, FakeBehavior.ServiceUnavailable503);

        var result = await harness.Gateway.ProcessAsync(ComplexRequest(), "tenant-x", GatewayTestFactory.Tenant(), CancellationToken.None);

        var success = Assert.IsType<GatewaySuccess>(result);
        Assert.Equal("standard", success.Response.Model);
        Assert.True(success.Response.FallbackUsed);
    }

    [Fact]
    public async Task Provider_429_is_retried_on_the_same_model_without_falling_back()
    {
        var harness = CreateHarness();
        // Only one 429 scripted: the second attempt (a retry) finds an empty queue and defaults
        // to Success, so this exercises "retry succeeds" rather than "retries exhausted".
        harness.Premium.Enqueue(FakeBehavior.RateLimited429);

        var result = await harness.Gateway.ProcessAsync(ComplexRequest(), "tenant-x", GatewayTestFactory.Tenant(), CancellationToken.None);

        var success = Assert.IsType<GatewaySuccess>(result);
        Assert.Equal("premium", success.Response.Model);
        Assert.False(success.Response.FallbackUsed);
        Assert.Equal(1, success.Response.RetryCount);
    }

    [Fact]
    public async Task Invalid_response_is_not_retried_but_does_fall_back()
    {
        var harness = CreateHarness();
        harness.Premium.Enqueue(FakeBehavior.InvalidResponse);

        var result = await harness.Gateway.ProcessAsync(ComplexRequest(), "tenant-x", GatewayTestFactory.Tenant(), CancellationToken.None);

        var success = Assert.IsType<GatewaySuccess>(result);
        Assert.Equal("standard", success.Response.Model);
        Assert.True(success.Response.FallbackUsed);
        Assert.Equal(0, success.Response.RetryCount); // no retry attempted on premium itself
    }

    [Fact]
    public async Task Invalid_request_is_neither_retried_nor_used_to_trigger_fallback()
    {
        var harness = CreateHarness();
        harness.Premium.Enqueue(FakeBehavior.InvalidRequest400);
        // Standard is left healthy/available on purpose: if the gateway incorrectly fell back,
        // this would silently succeed instead of failing the assertion below.

        var result = await harness.Gateway.ProcessAsync(ComplexRequest(), "tenant-x", GatewayTestFactory.Tenant(), CancellationToken.None);

        var failed = Assert.IsType<GatewayFailed>(result);
        Assert.Equal(1, failed.AttemptsMade);
    }

    [Fact]
    public async Task A_client_cancellation_is_not_retried_or_translated_into_a_gateway_failure_response()
    {
        var harness = CreateHarness();
        harness.Premium.Enqueue(FakeBehavior.Slow);
        harness.Premium.SlowLatency = TimeSpan.FromSeconds(5);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Gateway.ProcessAsync(ComplexRequest(), "tenant-x", GatewayTestFactory.Tenant(), cts.Token));
    }

    [Fact]
    public async Task When_every_model_in_the_fallback_chain_fails_the_gateway_returns_a_failure_result()
    {
        var harness = CreateHarness();
        harness.Premium.RandomFailureRate = 1.0; // always fails
        harness.Standard.RandomFailureRate = 1.0; // backs both "standard" and "economy"

        var tenant = GatewayTestFactory.Tenant(allowedModels: new[] { "economy", "standard", "premium" }); // exclude "local" so the chain is fully exhaustible

        var result = await harness.Gateway.ProcessAsync(ComplexRequest(), "tenant-x", tenant, CancellationToken.None);

        Assert.IsType<GatewayFailed>(result);
    }
}
