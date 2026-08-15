using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiGateway.Tests;

/// <summary>Integration tests through the real ASP.NET Core pipeline (WebApplicationFactory),
/// covering what unit tests can't: that UseRateLimiter is actually wired up, partitions by
/// tenant, and rejects with the status codes/headers the article describes. Runs against the
/// app's default provider wiring — no API keys are configured in this test environment, so
/// Program.cs's own fallback logic registers FakeAiProvider instances automatically.</summary>
public sealed class RateLimitingIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RateLimitingIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiGateway:Tenants:tenant-limit:ApiKey"] = "limit-test-key",
                    ["AiGateway:Tenants:tenant-limit:DailyTokenBudget"] = "1000000",
                    ["AiGateway:Tenants:tenant-limit:RequestsPerMinute"] = "3",
                    ["AiGateway:Tenants:tenant-limit:MaxConcurrentAiRequests"] = "10",
                    ["AiGateway:Tenants:tenant-limit:AllowedModels:0"] = "economy",
                    ["AiGateway:Tenants:tenant-limit:AllowedModels:1"] = "standard",
                    ["AiGateway:Tenants:tenant-limit:AllowedModels:2"] = "local",

                    ["AiGateway:Tenants:tenant-concurrency:ApiKey"] = "concurrency-test-key",
                    ["AiGateway:Tenants:tenant-concurrency:DailyTokenBudget"] = "1000000",
                    ["AiGateway:Tenants:tenant-concurrency:RequestsPerMinute"] = "1000",
                    ["AiGateway:Tenants:tenant-concurrency:MaxConcurrentAiRequests"] = "2",
                    ["AiGateway:Tenants:tenant-concurrency:AllowedModels:0"] = "economy",
                    ["AiGateway:Tenants:tenant-concurrency:AllowedModels:1"] = "standard",
                    ["AiGateway:Tenants:tenant-concurrency:AllowedModels:2"] = "local"
                });
            });
        });
    }

    [Fact]
    public async Task Requests_below_the_limit_succeed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "limit-test-key");

        var response = await client.PostAsJsonAsync("/api/ai/chat", ChatBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Requests_above_the_per_minute_limit_receive_429_with_retry_after()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "limit-test-key"); // configured above: 3/min

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 6; i++)
        {
            responses.Add(await client.PostAsJsonAsync("/api/ai/chat", ChatBody()));
        }

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        var rejected = responses.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Concurrent_requests_beyond_the_tenants_concurrency_limit_are_rejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "concurrency-test-key"); // configured above: concurrency=2, rate=1000/min

        var tasks = Enumerable.Range(0, 6).Select(_ => client.PostAsJsonAsync("/api/ai/chat", ChatBody())).ToArray();
        var responses = await Task.WhenAll(tasks);

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_api_key_is_rejected_before_rate_limiting_even_matters()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ai/chat", ChatBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static object ChatBody() => new
    {
        messages = new[] { new { role = "user", content = "integration test message" } },
        model = "auto",
        capability = "simple"
    };
}
