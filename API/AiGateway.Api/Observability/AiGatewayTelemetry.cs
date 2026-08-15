using System.Diagnostics;

namespace AiGateway.Api.Observability;

/// <summary>Distributed-tracing source for the gateway. Registered with OpenTelemetry tracing in
/// Program.cs so spans show up alongside the built-in ASP.NET Core and HttpClient instrumentation.</summary>
public static class AiGatewayTelemetry
{
    public const string ServiceName = "AiGateway";

    public static readonly ActivitySource ActivitySource = new(ServiceName);
}
