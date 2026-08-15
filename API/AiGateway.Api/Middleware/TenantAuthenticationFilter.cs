using AiGateway.Api.Models;
using AiGateway.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace AiGateway.Api.Middleware;

/// <summary>Resolves the calling tenant from the <c>X-Api-Key</c> header and stashes it on
/// <see cref="HttpContext.Items"/> for the endpoint to use. This is a deliberately simple
/// simplification of authentication — a static per-tenant key, not OAuth/JWT/mTLS — appropriate
/// for a sample gateway but explicitly called out as such in the article's security section.
/// Applied as an endpoint filter (not global middleware) so it only runs for the chat endpoint,
/// after rate limiting has already partitioned the request by the same header.</summary>
public sealed class TenantAuthenticationFilter : IEndpointFilter
{
    public const string TenantIdItemKey = "TenantId";
    public const string TenantOptionsItemKey = "TenantOptions";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        if (!httpContext.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyValues) || StringValues.IsNullOrEmpty(apiKeyValues))
        {
            return Results.Json(
                new GatewayErrorResponse("unauthorized", "MissingApiKey", "The X-Api-Key header is required.", httpContext.TraceIdentifier),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var apiKey = apiKeyValues.ToString();
        var gatewayOptions = httpContext.RequestServices.GetRequiredService<IOptions<AiGatewayOptions>>().Value;
        var tenantEntry = gatewayOptions.Tenants.FirstOrDefault(t => t.Value.ApiKey == apiKey);

        if (tenantEntry.Value is null)
        {
            return Results.Json(
                new GatewayErrorResponse("unauthorized", "InvalidApiKey", "The supplied API key is not recognized.", httpContext.TraceIdentifier),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        httpContext.Items[TenantIdItemKey] = tenantEntry.Key;
        httpContext.Items[TenantOptionsItemKey] = tenantEntry.Value;

        return await next(context);
    }
}
