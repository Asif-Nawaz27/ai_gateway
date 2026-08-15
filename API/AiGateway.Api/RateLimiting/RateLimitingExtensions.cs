using System.Globalization;
using System.Threading.RateLimiting;
using AiGateway.Api.Models;
using AiGateway.Api.Observability;
using AiGateway.Api.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace AiGateway.Api.RateLimiting;

/// <summary>Wires ASP.NET Core's built-in rate limiter with two limiters chained per tenant: a
/// Fixed Window limiter bounding HTTP requests/minute, and a Concurrency limiter bounding how
/// many AI calls a tenant can have in flight at once. These protect different things — the
/// request-rate limiter protects the gateway itself from being hammered; the concurrency limiter
/// protects the (expensive, slow) upstream model calls specifically, which is why an AI gateway
/// needs both even though a typical CRUD API usually only needs the first. Neither of these is
/// the same thing as a provider's own rate limit (see the 429 handling in AiGatewayService) or
/// the token budget (see TokenBudgetService) — three genuinely different controls that are easy
/// to conflate.
///
/// Fixed Window over Sliding Window was a deliberate, empirically-checked choice, not the
/// obvious "modern" default: testing both against .NET 10's System.Threading.RateLimiting
/// directly showed SlidingWindowRateLimiter's rejected leases advertise a RetryAfter metadata
/// *name* but a value RetryAfter.TryGetMetadata resolves as unavailable, while
/// FixedWindowRateLimiter reliably returns it. Since a usable Retry-After header on 429s is a
/// real requirement here (not a nice-to-have), Fixed Window wins despite Sliding Window's better
/// boundary-burst behavior — see the article's rate-limiting section for the measurement.</summary>
public static class RateLimitingExtensions
{
    private const string UnknownTenantPartition = "unknown-tenant";

    public static IServiceCollection AddAiGatewayRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejectedAsync;

            var requestRateLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                if (!IsAiEndpoint(httpContext))
                {
                    return RateLimitPartition.GetNoLimiter("no-limit");
                }

                var tenantKey = ResolveTenantKey(httpContext);
                var tenant = ResolveTenantOptions(httpContext, tenantKey);
                var permitLimit = tenant?.RequestsPerMinute ?? 10;

                return RateLimitPartition.GetFixedWindowLimiter($"rate:{tenantKey}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            var concurrencyLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                if (!IsAiEndpoint(httpContext))
                {
                    return RateLimitPartition.GetNoLimiter("no-limit");
                }

                var tenantKey = ResolveTenantKey(httpContext);
                var tenant = ResolveTenantOptions(httpContext, tenantKey);
                var permitLimit = tenant?.MaxConcurrentAiRequests ?? 2;

                return RateLimitPartition.GetConcurrencyLimiter($"concurrency:{tenantKey}", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = permitLimit,
                    QueueLimit = 0
                });
            });

            // Combines both limiters into one evaluation per request: a request must acquire a
            // lease from BOTH before it's allowed through. This is the documented pattern for
            // applying more than one limiter to the same requests.
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(requestRateLimiter, concurrencyLimiter);
        });

        return services;
    }

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        // Fixed Window can tell the caller when a permit will free up; Concurrency cannot (it has
        // no notion of "when a slot will open"), so no Retry-After header is set for a
        // concurrency rejection — that's a real limitation of the concurrency limiter, not an
        // oversight here.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        var tenantKey = ResolveTenantKey(context.HttpContext);
        context.HttpContext.RequestServices.GetService<AiGatewayMetrics>()?.RecordRateLimitRejection(tenantKey, "tenant-rate-or-concurrency");

        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new GatewayErrorResponse(
                "rate_limited",
                "TooManyRequests",
                "Request rate or concurrent-AI-request limit exceeded for this tenant.",
                context.HttpContext.TraceIdentifier),
            cancellationToken: cancellationToken);
    }

    private static bool IsAiEndpoint(HttpContext httpContext) =>
        httpContext.Request.Path.StartsWithSegments("/api/ai");

    private static string ResolveTenantKey(HttpContext httpContext) =>
        httpContext.Request.Headers.TryGetValue("X-Api-Key", out var key) && !StringValues.IsNullOrEmpty(key)
            ? key.ToString()
            : UnknownTenantPartition;

    private static TenantOptions? ResolveTenantOptions(HttpContext httpContext, string apiKey)
    {
        if (apiKey == UnknownTenantPartition)
        {
            return null;
        }

        var options = httpContext.RequestServices.GetRequiredService<IOptions<AiGatewayOptions>>().Value;
        return options.Tenants.Values.FirstOrDefault(t => t.ApiKey == apiKey);
    }
}
