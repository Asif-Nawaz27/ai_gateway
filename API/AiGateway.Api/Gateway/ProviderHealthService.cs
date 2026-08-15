using System.Collections.Concurrent;
using AiGateway.Api.Models;
using AiGateway.Api.Options;
using Microsoft.Extensions.Options;

namespace AiGateway.Api.Gateway;

public sealed record ProviderHealthSnapshot(string ProviderName, int WindowSize, int Failures, int ConsecutiveFailures, ProviderHealthStatus Status);

public interface IProviderHealthService
{
    void RecordSuccess(string providerName);
    void RecordFailure(string providerName);
    ProviderHealthStatus GetStatus(string providerName);
    ProviderHealthSnapshot GetSnapshot(string providerName);
}

/// <summary>Tracks each provider's recent outcomes and classifies it Healthy / Degraded /
/// Unavailable. This is deliberately not a distributed circuit breaker — a sliding window over
/// the last N outcomes, held in memory, per process. See the article's health-checks section for
/// why that's the right amount of complexity for this sample (and what changes for a
/// multi-instance deployment).</summary>
public sealed class ProviderHealthService : IProviderHealthService
{
    private readonly int _windowSize;
    private readonly double _degradedThreshold;
    private readonly double _unavailableThreshold;
    private readonly int _consecutiveForUnavailable;
    private readonly ConcurrentDictionary<string, ProviderState> _providers = new(StringComparer.OrdinalIgnoreCase);

    public ProviderHealthService(IOptions<AiGatewayOptions> options)
    {
        var o = options.Value;
        _windowSize = o.ProviderHealthWindowSize;
        _degradedThreshold = o.ProviderDegradedThreshold;
        _unavailableThreshold = o.ProviderUnavailableThreshold;
        _consecutiveForUnavailable = o.ProviderConsecutiveFailuresForUnavailable;
    }

    private sealed class ProviderState
    {
        public readonly object Gate = new();
        public readonly Queue<bool> Outcomes = new();
        public int ConsecutiveFailures;
    }

    public void RecordSuccess(string providerName) => Record(providerName, success: true);

    public void RecordFailure(string providerName) => Record(providerName, success: false);

    private void Record(string providerName, bool success)
    {
        var state = _providers.GetOrAdd(providerName, _ => new ProviderState());
        lock (state.Gate)
        {
            state.Outcomes.Enqueue(success);
            while (state.Outcomes.Count > _windowSize)
            {
                state.Outcomes.Dequeue();
            }

            state.ConsecutiveFailures = success ? 0 : state.ConsecutiveFailures + 1;
        }
    }

    public ProviderHealthStatus GetStatus(string providerName) => GetSnapshot(providerName).Status;

    public ProviderHealthSnapshot GetSnapshot(string providerName)
    {
        var state = _providers.GetOrAdd(providerName, _ => new ProviderState());
        lock (state.Gate)
        {
            var total = state.Outcomes.Count;
            var failures = state.Outcomes.Count(o => !o);
            var status = Classify(total, failures, state.ConsecutiveFailures);
            return new ProviderHealthSnapshot(providerName, total, failures, state.ConsecutiveFailures, status);
        }
    }

    private ProviderHealthStatus Classify(int total, int failures, int consecutiveFailures)
    {
        // Fast path: a hard, repeated failure streak means "unavailable" even before the
        // window has enough samples to compute a meaningful ratio.
        if (consecutiveFailures >= _consecutiveForUnavailable)
        {
            return ProviderHealthStatus.Unavailable;
        }

        if (total == 0)
        {
            return ProviderHealthStatus.Healthy;
        }

        var failureRate = (double)failures / total;
        if (failureRate > _unavailableThreshold)
        {
            return ProviderHealthStatus.Unavailable;
        }

        if (failureRate > _degradedThreshold)
        {
            return ProviderHealthStatus.Degraded;
        }

        return ProviderHealthStatus.Healthy;
    }
}
