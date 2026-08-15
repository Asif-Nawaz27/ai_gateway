using AiGateway.Api.Gateway;
using AiGateway.Api.Models;
using AiGateway.Api.Options;
using AiGateway.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

public sealed class ProviderHealthTests
{
    private static ProviderHealthService CreateService(int windowSize = 5, double degraded = 0.2, double unavailable = 0.5, int consecutive = 3)
    {
        var options = Options.Create(new AiGatewayOptions
        {
            ProviderHealthWindowSize = windowSize,
            ProviderDegradedThreshold = degraded,
            ProviderUnavailableThreshold = unavailable,
            ProviderConsecutiveFailuresForUnavailable = consecutive
        });
        return new ProviderHealthService(options);
    }

    [Fact]
    public void A_provider_with_no_recorded_outcomes_is_optimistically_healthy()
    {
        var health = CreateService();
        Assert.Equal(ProviderHealthStatus.Healthy, health.GetStatus("provider-x"));
    }

    [Fact]
    public void A_provider_with_a_low_failure_rate_stays_healthy()
    {
        var health = CreateService(windowSize: 5, degraded: 0.2, unavailable: 0.5);

        health.RecordSuccess("provider-x");
        health.RecordSuccess("provider-x");
        health.RecordSuccess("provider-x");
        health.RecordSuccess("provider-x");
        health.RecordFailure("provider-x"); // 1/5 = 20%, at (not above) the degraded threshold

        Assert.Equal(ProviderHealthStatus.Healthy, health.GetStatus("provider-x"));
    }

    [Fact]
    public void A_provider_with_a_moderate_failure_rate_is_degraded()
    {
        var health = CreateService(windowSize: 5, degraded: 0.2, unavailable: 0.8, consecutive: 10);

        health.RecordSuccess("provider-x");
        health.RecordFailure("provider-x");
        health.RecordSuccess("provider-x");
        health.RecordFailure("provider-x");
        health.RecordSuccess("provider-x"); // 2/5 = 40%: above degraded (20%), at/below unavailable (80%)

        var snapshot = health.GetSnapshot("provider-x");
        Assert.Equal(ProviderHealthStatus.Degraded, snapshot.Status);
        Assert.Equal(2, snapshot.Failures);
    }

    [Fact]
    public void A_provider_with_a_high_failure_rate_is_unavailable()
    {
        var health = CreateService(windowSize: 5, degraded: 0.2, unavailable: 0.5, consecutive: 10);

        health.RecordFailure("provider-x");
        health.RecordFailure("provider-x");
        health.RecordFailure("provider-x");
        health.RecordSuccess("provider-x");
        health.RecordSuccess("provider-x"); // 3/5 = 60% > 50% unavailable threshold

        Assert.Equal(ProviderHealthStatus.Unavailable, health.GetStatus("provider-x"));
    }

    [Fact]
    public void Consecutive_failures_trigger_unavailable_before_the_window_is_full()
    {
        // Unavailable threshold set above 100% so only the consecutive-failure fast path can classify Unavailable here.
        var health = CreateService(windowSize: 10, degraded: 0.2, unavailable: 1.1, consecutive: 3);

        health.RecordFailure("provider-x");
        health.RecordFailure("provider-x");
        Assert.Equal(ProviderHealthStatus.Degraded, health.GetStatus("provider-x")); // 2/2 = 100% > degraded threshold, but consecutive fast-path hasn't tripped yet

        health.RecordFailure("provider-x");
        Assert.Equal(ProviderHealthStatus.Unavailable, health.GetStatus("provider-x")); // 3 consecutive failures
    }

    [Fact]
    public void A_success_after_failures_resets_the_consecutive_failure_counter_and_recovers_eligibility()
    {
        var health = CreateService(windowSize: 5, degraded: 0.2, unavailable: 0.5, consecutive: 3);

        health.RecordFailure("provider-x");
        health.RecordFailure("provider-x");
        health.RecordFailure("provider-x");
        Assert.Equal(ProviderHealthStatus.Unavailable, health.GetStatus("provider-x"));

        health.RecordSuccess("provider-x");
        health.RecordSuccess("provider-x");
        health.RecordSuccess("provider-x");
        health.RecordSuccess("provider-x"); // window now [F,F,F,S,S,S,S] trimmed to last 5: [F,S,S,S,S] = 1/5 = 20%, not > 20%

        Assert.Equal(ProviderHealthStatus.Healthy, health.GetStatus("provider-x"));
    }

    [Fact]
    public void Providers_are_tracked_independently()
    {
        var health = CreateService(consecutive: 2);

        health.RecordFailure("provider-x");
        health.RecordFailure("provider-x");
        health.RecordSuccess("provider-y");

        Assert.Equal(ProviderHealthStatus.Unavailable, health.GetStatus("provider-x"));
        Assert.Equal(ProviderHealthStatus.Healthy, health.GetStatus("provider-y"));
    }
}
