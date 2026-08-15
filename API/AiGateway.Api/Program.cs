using System.Text.Json.Serialization;
using AiGateway.Api.Composition;
using AiGateway.Api.Endpoints;
using AiGateway.Api.Observability;
using AiGateway.Api.RateLimiting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddAiGatewayCore(builder.Configuration);
builder.Services.AddConfiguredAiProviders(builder.Configuration);
builder.Services.AddAiGatewayRateLimiting();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(AiGatewayTelemetry.ServiceName))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(AiGatewayTelemetry.ServiceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddConsoleExporter();

        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(AiGatewayMetrics.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddConsoleExporter();

        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        }
    });

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRateLimiter();

// Application health ("is this process up and able to serve traffic?") — distinct from AI
// provider health ("are the upstream models currently trustworthy?"), which is exposed at
// GET /api/ai/providers/health instead. See Section 10 of the article.
app.MapHealthChecks("/health");

app.MapAiGatewayEndpoints();

app.Run();

// Exposed so WebApplicationFactory<Program> can be used from the integration tests.
public partial class Program;
