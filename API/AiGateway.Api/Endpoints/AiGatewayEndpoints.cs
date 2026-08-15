using AiGateway.Api.Gateway;
using AiGateway.Api.Middleware;
using AiGateway.Api.Models;
using AiGateway.Api.Options;
using Microsoft.Extensions.Options;

namespace AiGateway.Api.Endpoints;

public static class AiGatewayEndpoints
{
    public static IEndpointRouteBuilder MapAiGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ai/chat", HandleChatAsync)
            .AddEndpointFilter<TenantAuthenticationFilter>()
            .WithName("PostChatCompletion");

        // AI-provider health is intentionally separate from ASP.NET Core's own /health endpoint
        // (mapped in Program.cs): this reports whether the *upstream models* are trustworthy
        // right now, not whether this process is alive. See Section 10 of the article.
        app.MapGet("/api/ai/providers/health", GetProviderHealth)
            .WithName("GetProviderHealth");

        return app;
    }

    private static async Task<IResult> HandleChatAsync(
        ChatCompletionApiRequest request,
        HttpContext httpContext,
        IAiGateway gateway,
        CancellationToken cancellationToken)
    {
        var tenantId = (string)httpContext.Items[TenantAuthenticationFilter.TenantIdItemKey]!;
        var tenant = (TenantOptions)httpContext.Items[TenantAuthenticationFilter.TenantOptionsItemKey]!;

        var result = await gateway.ProcessAsync(request, tenantId, tenant, cancellationToken);

        return result switch
        {
            GatewaySuccess success => Results.Ok(success.Response),
            GatewayRejected rejected => MapRejection(rejected),
            GatewayFailed failed => Results.Json(
                new GatewayErrorResponse("provider_failure", "AllProvidersFailed", failed.Message, failed.RequestId),
                statusCode: StatusCodes.Status502BadGateway),
            _ => Results.Problem()
        };
    }

    private static IResult MapRejection(GatewayRejected rejected)
    {
        var status = rejected.Reason switch
        {
            GatewayRejectionReason.Validation => StatusCodes.Status400BadRequest,
            GatewayRejectionReason.ContextTooLarge => StatusCodes.Status400BadRequest,
            GatewayRejectionReason.ModelNotPermitted => StatusCodes.Status403Forbidden,
            GatewayRejectionReason.BudgetExceeded => StatusCodes.Status402PaymentRequired,
            GatewayRejectionReason.NoHealthyProvider => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };

        return Results.Json(
            new GatewayErrorResponse(rejected.Reason.ToString(), rejected.Reason.ToString(), rejected.Message, rejected.RequestId),
            statusCode: status);
    }

    private static IResult GetProviderHealth(IProviderHealthService health, IOptions<AiGatewayOptions> options)
    {
        var providerNames = options.Value.Models.Values
            .Select(m => m.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var snapshots = providerNames.Select(health.GetSnapshot).ToList();
        return Results.Ok(snapshots);
    }
}
