using System.Diagnostics.Metrics;
using AiGateway.Api.Gateway;
using AiGateway.Api.Models;
using AiGateway.Api.Observability;
using AiGateway.Api.Options;
using AiGateway.Api.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Deliberately scripts provider failures against the SAME AiGatewayService code the web app
// uses — through FakeAiProvider, never a real network call — and prints exactly what the gateway
// decided at each step. This is the transcript quoted in the article's failure-simulation
// section; nothing below is hand-written output, it's the real console output of `dotnet run` in
// this project.
//
// Split into two phases on purpose. An early version of this script reused one gateway instance
// for every scenario and got confusing results: by the fourth or fifth scripted failure, Provider
// B's rolling health window had already crossed the "Unavailable" threshold from EARLIER
// scenarios, so the router started skipping it before later scenarios ever got to exercise their
// OWN scripted behavior. Phase 1 now gets a fresh gateway (and therefore fresh provider health)
// per scenario, to isolate each failure type. Phase 2 deliberately reuses one gateway instance to
// show that same health-accumulation effect on purpose, instead of by accident.

var tenant = new TenantOptions
{
    ApiKey = "sim-key",
    DailyTokenBudget = 1_000_000,
    RequestsPerMinute = 1000,
    MaxConcurrentAiRequests = 100,
    AllowedModels = ["economy", "standard", "premium", "local"]
};

Console.WriteLine("AI Gateway — Failure Simulation");
Console.WriteLine("================================");
Console.WriteLine("Provider A backs 'standard' and 'economy'. Provider B backs 'premium'. Local backs 'local'.");
Console.WriteLine();
Console.WriteLine("Phase 1 — one failure type per request, each against a freshly-healthy Provider B");
Console.WriteLine("---------------------------------------------------------------------------------");

var scenarioNumber = 0;

await RunIsolatedScenario("Baseline: Provider B healthy", []);
await RunIsolatedScenario("Provider B returns 503 three times (retries exhausted) -> fallback to Standard",
    [FakeBehavior.ServiceUnavailable503, FakeBehavior.ServiceUnavailable503, FakeBehavior.ServiceUnavailable503]);
await RunIsolatedScenario("Provider B returns 429 once -> bounded retry succeeds on the SAME model",
    [FakeBehavior.RateLimited429]);
await RunIsolatedScenario("Provider B times out three times -> fallback to Standard",
    [FakeBehavior.Timeout, FakeBehavior.Timeout, FakeBehavior.Timeout]);
await RunIsolatedScenario("Provider B returns a malformed response -> no retry, but DOES fall back (provider-specific glitch)",
    [FakeBehavior.InvalidResponse]);
await RunIsolatedScenario("Provider B rejects the request outright (400) -> no retry, NO fallback (same request fails everywhere)",
    [FakeBehavior.InvalidRequest400]);

Console.WriteLine();
Console.WriteLine("Phase 2 — one continuous gateway instance: watch Provider B's health degrade and recover");
Console.WriteLine("-------------------------------------------------------------------------------------------");

// Unavailable-by-ratio is set out of reach here (1.1 — impossible to exceed) so this phase
// isolates the CONSECUTIVE-failure fast path cleanly. With the default 0.5 ratio threshold from
// Phase 1's config, 2 failures out of a 2-sample window is already 100% — which would flip the
// provider to Unavailable via the ratio check ONE request earlier than the "3 consecutive
// failures" story below intends. Both mechanisms are real and both ship in ProviderHealthService;
// this phase just isolates one of them for a clean narrative. See the article for the ratio path.
var (gateway, _, scriptB, _, health) = BuildGateway(unavailableThreshold: 1.1);

// Each call below enqueues 3 failures — MaxRetries=2 means 3 total attempts per model — so the
// WHOLE request exhausts its retries on Provider B and records exactly one health failure, then
// falls back. (Enqueuing just one failure would let the automatic retry consume the empty queue
// as a default Success on the 2nd attempt, masking the fallback this scenario is meant to show —
// an easy mistake to make when scripting these, worth calling out.)
await RunOnGateway(gateway, "Request A: Provider B healthy", () => scriptB.Enqueue(FakeBehavior.Success));
await RunOnGateway(gateway, "Request B: Provider B fails (1st consecutive failure) -> falls back to Standard",
    () => scriptB.Enqueue(FakeBehavior.ServerError500, FakeBehavior.ServerError500, FakeBehavior.ServerError500));
await RunOnGateway(gateway, "Request C: Provider B fails again (2nd consecutive failure) -> falls back to Standard",
    () => scriptB.Enqueue(FakeBehavior.ServerError500, FakeBehavior.ServerError500, FakeBehavior.ServerError500));
await RunOnGateway(gateway, "Request D: Provider B fails a 3rd consecutive time -> crosses the Unavailable threshold",
    () => scriptB.Enqueue(FakeBehavior.ServerError500, FakeBehavior.ServerError500, FakeBehavior.ServerError500));
PrintHealth(health, "After Request D");
await RunOnGateway(gateway, "Request E: Provider B is Unavailable -> router skips it, routes straight to Standard (no wasted call)", () => scriptB.Enqueue(FakeBehavior.Success));
await RunOnGateway(gateway, "Request F: same again -> still skipped, even though a success was scripted and never consumed", () => { });
PrintHealth(health, "After Request F (Provider B still Unavailable — no successes have been recorded since the failures)");

Console.WriteLine();
Console.WriteLine("Note: this gateway only records Provider B health when it actually CALLS Provider B. Once");
Console.WriteLine("marked Unavailable, the router stops calling it — so recovery in this design needs a");
Console.WriteLine("separate out-of-band health probe, not just \"try it again on the next real request\".");
Console.WriteLine("That's a deliberate scope boundary of this sample; see the article's production-considerations");
Console.WriteLine("section for what a background health-probe loop would add.");

return;

(AiGatewayService Gateway, FakeProviderScript ScriptA, FakeProviderScript ScriptB, FakeProviderScript ScriptLocal, IProviderHealthService Health) BuildGateway(double unavailableThreshold = 0.5)
{
    var options = new AiGatewayOptions
    {
        MaxInputTokens = 8_000,
        MaxOutputTokens = 2_000,
        MaxRetries = 2,
        ProviderHealthWindowSize = 5,
        ProviderDegradedThreshold = 0.2,
        ProviderUnavailableThreshold = unavailableThreshold,
        ProviderConsecutiveFailuresForUnavailable = 3,
        BudgetPressureThreshold = 0.25,
        CapabilityTierOrder = ["premium", "standard", "economy"],
        Models = new Dictionary<string, ModelOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["economy"] = new() { Provider = "ProviderA", ProviderModelId = "economy-model", Capability = RequestCapability.Simple, Priority = 3, MaxContextTokens = 16_000, TimeoutSeconds = 2, FallbackModel = "local", InputCostPerMillionTokens = 0.15m, OutputCostPerMillionTokens = 0.60m },
            ["standard"] = new() { Provider = "ProviderA", ProviderModelId = "standard-model", Capability = RequestCapability.Standard, Priority = 2, MaxContextTokens = 32_000, TimeoutSeconds = 2, FallbackModel = "economy", InputCostPerMillionTokens = 2.50m, OutputCostPerMillionTokens = 10.00m },
            ["premium"] = new() { Provider = "ProviderB", ProviderModelId = "premium-model", Capability = RequestCapability.Complex, Priority = 1, MaxContextTokens = 64_000, TimeoutSeconds = 2, FallbackModel = "standard", InputCostPerMillionTokens = 15.00m, OutputCostPerMillionTokens = 75.00m },
            ["local"] = new() { Provider = "Local", ProviderModelId = "local-model", Capability = RequestCapability.Simple, Priority = 4, MaxContextTokens = 8_000, TimeoutSeconds = 2, InputCostPerMillionTokens = 0, OutputCostPerMillionTokens = 0 }
        }
    };

    var ioptions = Options.Create(options);
    var health = new ProviderHealthService(ioptions);
    var budget = new TokenBudgetService();
    var estimator = new TokenEstimator();
    var cost = new CostCalculator();
    var router = new ModelRouter(ioptions, health, budget);

    var scriptA = new FakeProviderScript { BaseLatency = TimeSpan.FromMilliseconds(30), LatencyJitter = TimeSpan.Zero };
    var scriptB = new FakeProviderScript { BaseLatency = TimeSpan.FromMilliseconds(30), LatencyJitter = TimeSpan.Zero };
    var scriptLocal = new FakeProviderScript { BaseLatency = TimeSpan.FromMilliseconds(30), LatencyJitter = TimeSpan.Zero };

    IAiProvider[] providers =
    [
        new FakeAiProvider("ProviderA", scriptA),
        new FakeAiProvider("ProviderB", scriptB),
        new FakeAiProvider("Local", scriptLocal)
    ];

    var metrics = new AiGatewayMetrics(new ConsoleMeterFactory());
    var gw = new AiGatewayService(router, budget, estimator, cost, health, providers, ioptions, metrics, NullLogger<AiGatewayService>.Instance);
    return (gw, scriptA, scriptB, scriptLocal, health);
}

async Task<GatewayResult> Send(AiGatewayService gw)
{
    var request = new ChatCompletionApiRequest(
        Messages: [new ChatMessageDto("user", "Explain optimistic concurrency control.")],
        Model: "auto",
        Capability: RequestCapability.Complex.ToString(),
        Priority: "normal",
        MaxTokens: 300);

    return await gw.ProcessAsync(request, "tenant-sim", tenant, CancellationToken.None);
}

async Task RunIsolatedScenario(string title, FakeBehavior[] scriptedBehaviors)
{
    scenarioNumber++;
    var (gw, _, scriptB, _, _) = BuildGateway();
    if (scriptedBehaviors.Length > 0)
    {
        scriptB.Enqueue(scriptedBehaviors);
    }

    Console.WriteLine($"Request {scenarioNumber}: {title}");
    var result = await Send(gw);
    PrintOutcome(result);
}

async Task RunOnGateway(AiGatewayService gw, string title, Action scriptSetup)
{
    scenarioNumber++;
    scriptSetup();
    Console.WriteLine($"Request {scenarioNumber}: {title}");
    var result = await Send(gw);
    PrintOutcome(result);
}

void PrintOutcome(GatewayResult result)
{
    switch (result)
    {
        case GatewaySuccess success:
            Console.WriteLine($"  -> SUCCESS  model={success.Response.Model,-8} provider={success.Response.Provider,-9} fallbackUsed={success.Response.FallbackUsed,-5} retryCount={success.Response.RetryCount}");
            break;
        case GatewayRejected rejected:
            Console.WriteLine($"  -> REJECTED reason={rejected.Reason} — {rejected.Message}");
            break;
        case GatewayFailed failed:
            Console.WriteLine($"  -> FAILED   attempts={failed.AttemptsMade} — {failed.Message}");
            break;
    }

    Console.WriteLine();
}

void PrintHealth(IProviderHealthService health, string label)
{
    var snapshot = health.GetSnapshot("ProviderB");
    Console.WriteLine($"  [{label}] ProviderB status={snapshot.Status} window={snapshot.WindowSize} failures={snapshot.Failures} consecutiveFailures={snapshot.ConsecutiveFailures}");
    Console.WriteLine();
}

internal sealed class ConsoleMeterFactory : IMeterFactory
{
    public Meter Create(MeterOptions options) => new(options.Name, options.Version);
    public void Dispose() { }
}
