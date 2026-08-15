using AiGateway.Api.Gateway;
using AiGateway.Api.Observability;
using AiGateway.Api.Options;
using AiGateway.Api.Providers;

namespace AiGateway.Api.Composition;

/// <summary>Composition root for the gateway's own services (router, budget, health, cost,
/// estimator, orchestrator) — shared by the web app (Program.cs), the failure-simulation
/// console app, and the benchmark console app, so all three exercise identical gateway logic.
/// Provider *registration* is deliberately a separate method: the web app decides between real
/// and fake providers based on configured API keys, while the console apps always build their
/// own explicit <see cref="FakeAiProvider"/> instances so they can script specific scenarios.</summary>
public static class AiGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddAiGatewayCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiGatewayOptions>(configuration.GetSection(AiGatewayOptions.SectionName));

        services.AddMetrics();
        services.AddSingleton<AiGatewayMetrics>();
        services.AddSingleton<ITokenBudgetService, TokenBudgetService>();
        services.AddSingleton<IProviderHealthService, ProviderHealthService>();
        services.AddSingleton<ITokenEstimator, TokenEstimator>();
        services.AddSingleton<ICostCalculator, CostCalculator>();
        services.AddSingleton<IModelRouter, ModelRouter>();
        services.AddSingleton<IAiGateway, AiGatewayService>();

        return services;
    }

    /// <summary>Registers a real provider when an API key is configured (via user-secrets or an
    /// environment variable — never appsettings.json), and falls back to a <see cref="FakeAiProvider"/>
    /// otherwise, so <c>dotnet run</c> works out of the box with no keys at all. The "Local"
    /// provider is always a fake — it stands in for a small, cheap, always-available local model
    /// (e.g. one served through Ollama) that this sample doesn't need real weights to illustrate.</summary>
    public static IServiceCollection AddConfiguredAiProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var openAiKey = configuration["AiGateway:Providers:OpenAI:ApiKey"];
        var anthropicKey = configuration["AiGateway:Providers:Anthropic:ApiKey"];

        if (!string.IsNullOrWhiteSpace(openAiKey))
        {
            services.AddSingleton<IAiProvider>(_ => new OpenAiProvider(openAiKey));
        }
        else
        {
            var script = new FakeProviderScript { BaseLatency = TimeSpan.FromMilliseconds(900), LatencyJitter = TimeSpan.FromMilliseconds(300) };
            services.AddSingleton<IAiProvider>(_ => new FakeAiProvider("OpenAI", script));
        }

        if (!string.IsNullOrWhiteSpace(anthropicKey))
        {
            services.AddHttpClient("Anthropic", client => client.Timeout = TimeSpan.FromSeconds(35))
                .AddStandardResilienceHandler();
            services.AddSingleton<IAiProvider>(sp =>
                new AnthropicProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient("Anthropic"), anthropicKey));
        }
        else
        {
            var script = new FakeProviderScript { BaseLatency = TimeSpan.FromMilliseconds(1400), LatencyJitter = TimeSpan.FromMilliseconds(400) };
            services.AddSingleton<IAiProvider>(_ => new FakeAiProvider("Anthropic", script));
        }

        var localScript = new FakeProviderScript { BaseLatency = TimeSpan.FromMilliseconds(120), LatencyJitter = TimeSpan.FromMilliseconds(40) };
        services.AddSingleton<IAiProvider>(_ => new FakeAiProvider("Local", localScript));

        return services;
    }
}
