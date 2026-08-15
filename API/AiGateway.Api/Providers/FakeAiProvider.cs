using System.Collections.Concurrent;
using AiGateway.Api.Models;

namespace AiGateway.Api.Providers;

/// <summary>The behaviors a <see cref="FakeAiProvider"/> can be scripted to produce. Mirrors the
/// failure classes the gateway's resilience layer actually has to handle in production.</summary>
public enum FakeBehavior
{
    Success,
    Timeout,
    RateLimited429,
    ServerError500,
    ServiceUnavailable503,
    Slow,
    InvalidResponse,

    /// <summary>A malformed CLIENT request (HTTP 400) — distinct from <see cref="InvalidResponse"/>,
    /// which is a malformed PROVIDER response to an otherwise-valid request. A 400 means the same
    /// request would fail identically on every provider, so (unlike InvalidResponse) it is neither
    /// retried nor used to trigger a fallback — see AiGatewayService's failure classification.</summary>
    InvalidRequest400
}

/// <summary>Controls one <see cref="FakeAiProvider"/> instance: a queue of scripted behaviors
/// (consumed first, in order — used by the failure-simulation transcript) plus an optional
/// random failure rate (used by the benchmark to model a provider with a background error rate).
/// Each registered fake provider gets its own controller so tests can drive them independently.</summary>
public sealed class FakeProviderScript
{
    private readonly ConcurrentQueue<FakeBehavior> _scripted = new();

    public double RandomFailureRate { get; set; }
    public TimeSpan BaseLatency { get; set; } = TimeSpan.FromMilliseconds(150);
    public TimeSpan LatencyJitter { get; set; } = TimeSpan.FromMilliseconds(80);
    public TimeSpan SlowLatency { get; set; } = TimeSpan.FromSeconds(6);

    public void Enqueue(params FakeBehavior[] behaviors)
    {
        foreach (var behavior in behaviors)
        {
            _scripted.Enqueue(behavior);
        }
    }

    public FakeBehavior Next(Random random)
    {
        if (_scripted.TryDequeue(out var scripted))
        {
            return scripted;
        }

        if (RandomFailureRate > 0 && random.NextDouble() < RandomFailureRate)
        {
            return random.Next(3) switch
            {
                0 => FakeBehavior.RateLimited429,
                1 => FakeBehavior.ServerError500,
                _ => FakeBehavior.ServiceUnavailable503
            };
        }

        return FakeBehavior.Success;
    }
}

/// <summary>A fully controllable, in-process provider used for every automated test, the failure
/// simulation, and the benchmark. No test or demo in this repository ever calls a paid provider.</summary>
public sealed class FakeAiProvider : IAiProvider
{
    private readonly FakeProviderScript _script;
    private readonly Random _random = new();

    public FakeAiProvider(string name, FakeProviderScript script)
    {
        Name = name;
        _script = script;
    }

    public string Name { get; }

    public async Task<ProviderCompletionResult> CompleteAsync(
        NormalizedChatRequest request,
        string providerModelId,
        int maxOutputTokens,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var behavior = _script.Next(_random);
        var latency = behavior == FakeBehavior.Slow
            ? _script.SlowLatency
            : _script.BaseLatency + TimeSpan.FromMilliseconds(_random.NextDouble() * _script.LatencyJitter.TotalMilliseconds);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        try
        {
            await Task.Delay(latency, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(Name, ProviderFailureKind.Timeout,
                $"Provider '{Name}' timed out after {timeout.TotalSeconds:0}s.");
        }

        switch (behavior)
        {
            case FakeBehavior.Timeout:
                throw new AiProviderException(Name, ProviderFailureKind.Timeout, $"Provider '{Name}' timed out.");
            case FakeBehavior.RateLimited429:
                throw new AiProviderException(Name, ProviderFailureKind.RateLimited, $"Provider '{Name}' returned 429 Too Many Requests.", 429);
            case FakeBehavior.ServerError500:
                throw new AiProviderException(Name, ProviderFailureKind.ServerError, $"Provider '{Name}' returned 500 Internal Server Error.", 500);
            case FakeBehavior.ServiceUnavailable503:
                throw new AiProviderException(Name, ProviderFailureKind.ServerError, $"Provider '{Name}' returned 503 Service Unavailable.", 503);
            case FakeBehavior.InvalidResponse:
                throw new AiProviderException(Name, ProviderFailureKind.InvalidResponse, $"Provider '{Name}' returned a malformed response body.");
            case FakeBehavior.InvalidRequest400:
                throw new AiProviderException(Name, ProviderFailureKind.InvalidRequest, $"Provider '{Name}' rejected the request as malformed (400).", 400);
        }

        var inputTokens = request.EstimatedInputTokens;
        var outputTokens = Math.Clamp(inputTokens / 3, 24, maxOutputTokens);
        var content = $"[{Name}/{providerModelId}] simulated completion for {request.Messages.Count} message(s).";
        return new ProviderCompletionResult(content, new UsageInfo(inputTokens, outputTokens), "stop");
    }
}
