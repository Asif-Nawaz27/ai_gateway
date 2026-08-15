using System.Diagnostics;
using System.Diagnostics.Metrics;
using AiGateway.Api.Gateway;
using AiGateway.Api.Models;
using AiGateway.Api.Observability;
using AiGateway.Api.Options;
using AiGateway.Api.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Compares two architectures under an IDENTICAL simulated workload:
//   "Direct"  — every request calls the premium model directly. No routing, no budget check, no
//               retry, no fallback. This is "one LLM provider" from the article's introduction.
//   "Gateway" — every request flows through the real AiGatewayService: capability-based routing
//               (so simple requests don't pay for premium), bounded retry, and fallback on
//               failure.
//
// SCOPE, stated plainly because it materially affects how to read the numbers below:
//   - Both architectures call the SAME FakeAiProvider implementation with the SAME simulated
//     per-tier latency distribution and the SAME injected random failure rate. No real network
//     call is made anywhere in this benchmark, and no real LLM provider latency is included.
//   - Both architectures are exercised IN-PROCESS, calling AiGatewayService/IAiProvider directly —
//     NOT over HTTP. ASP.NET Core's own request pipeline (Kestrel, JSON (de)serialization, the
//     rate-limiting middleware) is intentionally excluded, since that overhead is identical for
//     both architectures and would only add noise to a comparison that's specifically about
//     ROUTING and RESILIENCE behavior, not transport overhead.
//   - Numbers below are real measurements from the run that produced this output, on whatever
//     machine `dotnet run` was executed on — see the printed environment block. They are NOT
//     representative of real provider latency or real-world throughput.

const int requestCount = 300;
const int concurrency = 10;

var machineInfo = $".NET {Environment.Version}, {Environment.ProcessorCount} logical CPUs, OS {Environment.OSVersion}";
Console.WriteLine("AI Gateway — Benchmark: Direct-to-Premium vs Gateway Routing");
Console.WriteLine("==============================================================");
Console.WriteLine($"Requests: {requestCount}   Concurrency: {concurrency}   Streaming: no   Environment: {machineInfo}");
Console.WriteLine("Provider latency is SIMULATED (FakeAiProvider), not a real network call. See source comments for full scope.");
Console.WriteLine();

var workload = BuildWorkload(requestCount, seed: 42);

var directResult = await RunDirectAsync(workload, concurrency);
PrintResult("Direct-to-Premium", directResult);

var gatewayResult = await RunGatewayAsync(workload, concurrency);
PrintResult("Gateway (routed)", gatewayResult);

PrintComparisonTable(directResult, gatewayResult);

return;

static List<(RequestCapability Capability, string Content)> BuildWorkload(int count, int seed)
{
    // 60% simple / 30% standard / 10% complex — skewed toward cheap requests, roughly matching
    // the kind of traffic mix a support/chat product sees in practice (many short lookups, fewer
    // genuinely hard questions). Deterministic seed so re-runs are comparable.
    var random = new Random(seed);
    var workload = new List<(RequestCapability, string)>(count);
    for (var i = 0; i < count; i++)
    {
        var roll = random.NextDouble();
        var capability = roll switch
        {
            < 0.60 => RequestCapability.Simple,
            < 0.90 => RequestCapability.Standard,
            _ => RequestCapability.Complex
        };
        workload.Add((capability, $"Benchmark request #{i} ({capability})"));
    }

    return workload;
}

static AiGatewayOptions BuildOptions() => new()
{
    MaxInputTokens = 8_000,
    MaxOutputTokens = 2_000,
    MaxRetries = 2,
    ProviderHealthWindowSize = 20,
    ProviderDegradedThreshold = 0.2,
    ProviderUnavailableThreshold = 0.5,
    ProviderConsecutiveFailuresForUnavailable = 5,
    BudgetPressureThreshold = 0.1,
    CapabilityTierOrder = ["premium", "standard", "economy"],
    Models = new Dictionary<string, ModelOptions>(StringComparer.OrdinalIgnoreCase)
    {
        ["economy"] = new() { Provider = "ProviderA", ProviderModelId = "economy-model", Capability = RequestCapability.Simple, Priority = 3, MaxContextTokens = 16_000, TimeoutSeconds = 5, FallbackModel = "local", InputCostPerMillionTokens = 0.15m, OutputCostPerMillionTokens = 0.60m },
        ["standard"] = new() { Provider = "ProviderA", ProviderModelId = "standard-model", Capability = RequestCapability.Standard, Priority = 2, MaxContextTokens = 32_000, TimeoutSeconds = 5, FallbackModel = "economy", InputCostPerMillionTokens = 2.50m, OutputCostPerMillionTokens = 10.00m },
        ["premium"] = new() { Provider = "ProviderB", ProviderModelId = "premium-model", Capability = RequestCapability.Complex, Priority = 1, MaxContextTokens = 64_000, TimeoutSeconds = 5, FallbackModel = "standard", InputCostPerMillionTokens = 15.00m, OutputCostPerMillionTokens = 75.00m },
        ["local"] = new() { Provider = "Local", ProviderModelId = "local-model", Capability = RequestCapability.Simple, Priority = 4, MaxContextTokens = 8_000, TimeoutSeconds = 5, InputCostPerMillionTokens = 0, OutputCostPerMillionTokens = 0 }
    }
};

// Same simulated latency profile and failure rate used by both architectures: premium is
// slower AND more failure-prone in this simulation than economy/standard, which is a common
// real-world pattern (larger models, more loaded endpoints) and is what makes "everything goes
// to premium" a worse bet than routing, beyond just cost.
static (FakeProviderScript A, FakeProviderScript B, FakeProviderScript Local) BuildScripts() => (
    new FakeProviderScript { BaseLatency = TimeSpan.FromMilliseconds(220), LatencyJitter = TimeSpan.FromMilliseconds(120), RandomFailureRate = 0.04 },
    new FakeProviderScript { BaseLatency = TimeSpan.FromMilliseconds(900), LatencyJitter = TimeSpan.FromMilliseconds(400), RandomFailureRate = 0.10 },
    new FakeProviderScript { BaseLatency = TimeSpan.FromMilliseconds(90), LatencyJitter = TimeSpan.FromMilliseconds(40), RandomFailureRate = 0.01 });

static async Task<BenchmarkResult> RunDirectAsync(List<(RequestCapability Capability, string Content)> workload, int concurrency)
{
    var options = BuildOptions();
    var (_, scriptB, _) = BuildScripts();
    var provider = new FakeAiProvider("ProviderB", scriptB);
    var costCalculator = new CostCalculator();
    var premium = options.Models["premium"];

    var samples = new RequestSample[workload.Count];
    using var gate = new SemaphoreSlim(concurrency);
    var overall = Stopwatch.StartNew();

    var tasks = workload.Select(async (item, index) =>
    {
        await gate.WaitAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            var request = new NormalizedChatRequest("bench-tenant", $"req-{index}", [new ChatTurn(GatewayChatRole.User, item.Content)], 300, EstimateTokens(item.Content));
            try
            {
                var result = await provider.CompleteAsync(request, premium.ProviderModelId, 300, TimeSpan.FromSeconds(premium.TimeoutSeconds), CancellationToken.None);
                sw.Stop();
                var cost = costCalculator.Calculate(premium, result.Usage);
                samples[index] = new RequestSample(true, sw.Elapsed, "premium", false, 0, result.Usage.TotalTokens, cost);
            }
            catch (AiProviderException)
            {
                sw.Stop();
                samples[index] = new RequestSample(false, sw.Elapsed, null, false, 0, 0, 0m);
            }
        }
        finally
        {
            gate.Release();
        }
    });

    await Task.WhenAll(tasks);
    overall.Stop();
    return Summarize(samples, overall.Elapsed);
}

static async Task<BenchmarkResult> RunGatewayAsync(List<(RequestCapability Capability, string Content)> workload, int concurrency)
{
    var options = BuildOptions();
    var ioptions = Options.Create(options);
    var health = new ProviderHealthService(ioptions);
    var budget = new TokenBudgetService();
    var estimator = new TokenEstimator();
    var costCalculator = new CostCalculator();
    var router = new ModelRouter(ioptions, health, budget);

    var (scriptA, scriptB, scriptLocal) = BuildScripts();
    IAiProvider[] providers = [new FakeAiProvider("ProviderA", scriptA), new FakeAiProvider("ProviderB", scriptB), new FakeAiProvider("Local", scriptLocal)];
    var metrics = new AiGatewayMetrics(new BenchmarkMeterFactory());
    var gateway = new AiGatewayService(router, budget, estimator, costCalculator, health, providers, ioptions, metrics, NullLogger<AiGatewayService>.Instance);

    var tenant = new TenantOptions
    {
        ApiKey = "bench-key",
        DailyTokenBudget = 50_000_000, // ample, so budget behavior doesn't confound this comparison
        RequestsPerMinute = int.MaxValue,
        MaxConcurrentAiRequests = concurrency,
        AllowedModels = ["economy", "standard", "premium", "local"]
    };

    var samples = new RequestSample[workload.Count];
    using var gate = new SemaphoreSlim(concurrency);
    var overall = Stopwatch.StartNew();

    var tasks = workload.Select(async (item, index) =>
    {
        await gate.WaitAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            var apiRequest = new ChatCompletionApiRequest([new ChatMessageDto("user", item.Content)], "auto", item.Capability.ToString(), "normal", 300);
            var result = await gateway.ProcessAsync(apiRequest, "bench-tenant", tenant, CancellationToken.None);
            sw.Stop();

            samples[index] = result switch
            {
                GatewaySuccess success => new RequestSample(true, sw.Elapsed, success.Response.Model, success.Response.FallbackUsed, success.Response.RetryCount, success.Response.Usage.TotalTokens, success.Response.EstimatedCost),
                _ => new RequestSample(false, sw.Elapsed, null, false, 0, 0, 0m)
            };
        }
        finally
        {
            gate.Release();
        }
    });

    await Task.WhenAll(tasks);
    overall.Stop();
    return Summarize(samples, overall.Elapsed);
}

static int EstimateTokens(string content) => Math.Max(8, content.Length / 4);

static BenchmarkResult Summarize(RequestSample[] samples, TimeSpan wallClock)
{
    var successes = samples.Where(s => s.Success).ToList();
    var latenciesMs = successes.Select(s => s.Latency.TotalMilliseconds).OrderBy(x => x).ToList();

    double Percentile(double p)
    {
        if (latenciesMs.Count == 0) return 0;
        var index = (int)Math.Ceiling(p * latenciesMs.Count) - 1;
        return latenciesMs[Math.Clamp(index, 0, latenciesMs.Count - 1)];
    }

    var modelCounts = successes
        .Where(s => s.Model is not null)
        .GroupBy(s => s.Model!)
        .ToDictionary(g => g.Key, g => g.Count());

    return new BenchmarkResult(
        TotalRequests: samples.Length,
        Succeeded: successes.Count,
        WallClock: wallClock,
        AverageLatencyMs: latenciesMs.Count > 0 ? latenciesMs.Average() : 0,
        P95LatencyMs: Percentile(0.95),
        FallbackCount: successes.Count(s => s.FallbackUsed),
        TotalRetries: successes.Sum(s => s.Retries),
        TotalTokens: successes.Sum(s => s.Tokens),
        TotalCost: successes.Sum(s => s.Cost),
        ModelCounts: modelCounts);
}

static void PrintResult(string label, BenchmarkResult r)
{
    Console.WriteLine($"--- {label} ---");
    Console.WriteLine($"  Requests:        {r.TotalRequests}");
    Console.WriteLine($"  Success rate:    {100.0 * r.Succeeded / r.TotalRequests:F1}% ({r.Succeeded}/{r.TotalRequests})");
    Console.WriteLine($"  Wall clock:      {r.WallClock.TotalSeconds:F2}s ({r.TotalRequests / r.WallClock.TotalSeconds:F1} req/s)");
    Console.WriteLine($"  Avg latency:     {r.AverageLatencyMs:F0} ms");
    Console.WriteLine($"  p95 latency:     {r.P95LatencyMs:F0} ms");
    Console.WriteLine($"  Fallback used:   {r.FallbackCount} ({(r.Succeeded > 0 ? 100.0 * r.FallbackCount / r.Succeeded : 0):F1}% of successes)");
    Console.WriteLine($"  Total retries:   {r.TotalRetries}");
    Console.WriteLine($"  Total tokens:    {r.TotalTokens:N0}");
    Console.WriteLine($"  Estimated cost:  ${r.TotalCost:F4}");
    if (r.ModelCounts.Count > 0)
    {
        Console.WriteLine($"  Requests/model:  {string.Join(", ", r.ModelCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"))}");
    }

    Console.WriteLine();
}

static void PrintComparisonTable(BenchmarkResult direct, BenchmarkResult gateway)
{
    Console.WriteLine("--- Comparison ---");
    Console.WriteLine($"{"Metric",-20}{"Direct",15}{"Gateway",15}");
    Console.WriteLine($"{"Success rate",-20}{100.0 * direct.Succeeded / direct.TotalRequests,14:F1}%{100.0 * gateway.Succeeded / gateway.TotalRequests,14:F1}%");
    Console.WriteLine($"{"Avg latency (ms)",-20}{direct.AverageLatencyMs,15:F0}{gateway.AverageLatencyMs,15:F0}");
    Console.WriteLine($"{"p95 latency (ms)",-20}{direct.P95LatencyMs,15:F0}{gateway.P95LatencyMs,15:F0}");
    Console.WriteLine($"{"Total cost ($)",-20}{direct.TotalCost,15:F4}{gateway.TotalCost,15:F4}");
    Console.WriteLine($"{"Total tokens",-20}{direct.TotalTokens,15:N0}{gateway.TotalTokens,15:N0}");
}

internal sealed record RequestSample(bool Success, TimeSpan Latency, string? Model, bool FallbackUsed, int Retries, int Tokens, decimal Cost);

internal sealed record BenchmarkResult(
    int TotalRequests,
    int Succeeded,
    TimeSpan WallClock,
    double AverageLatencyMs,
    double P95LatencyMs,
    int FallbackCount,
    int TotalRetries,
    int TotalTokens,
    decimal TotalCost,
    Dictionary<string, int> ModelCounts);

internal sealed class BenchmarkMeterFactory : IMeterFactory
{
    public Meter Create(MeterOptions options) => new(options.Name, options.Version);
    public void Dispose() { }
}
