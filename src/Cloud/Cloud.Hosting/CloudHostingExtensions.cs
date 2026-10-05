using Cloud.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Cloud.Hosting;

/// <summary>Plumbing shared by the four server hosts.</summary>
public static class CloudHostingExtensions
{
    /// <summary>
    /// Headers that cost nothing and close easy mistakes: the API's answers are never sniffed into another content type, never framed and
    /// never sent on as a referrer; and nothing under /api is cached by a browser or proxy (responses can carry licenses or other customers' metadata).
    /// </summary>
    public static IApplicationBuilder UseCloudSecurityHeaders(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                if (context.Request.Path.StartsWithSegments("/api") && !headers.ContainsKey("Cache-Control"))
                    headers["Cache-Control"] = "no-store";
                return Task.CompletedTask;
            });

            await next();
        });

    /// <summary>
    /// Transport security for every host outside Development: HSTS, redirection to HTTPS where the host knows its HTTPS port, and - because
    /// a host behind a TLS-terminating proxy cannot always redirect - a hard refusal of any API request that still arrives over plain HTTP.
    /// Credentials and licenses therefore never travel over an insecure channel by accident. <c>/health</c> stays reachable for probes.
    /// Behind a proxy, configure forwarded headers so the original scheme is seen (a deployment concern, Stage 14).
    /// Development stays plain so local testing needs no certificates.
    /// </summary>
    public static WebApplication UseCloudTransportSecurity(this WebApplication app)
    {
        if (app.Environment.IsDevelopment()) return app;

        app.UseHsts();
        app.UseHttpsRedirection();
        app.Use(async (context, next) =>
        {
            if (!context.Request.IsHttps && !context.Request.Path.StartsWithSegments("/health"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new ApiError(CloudErrorCodes.HttpsRequired, "HTTPS is required."));
                return;
            }

            await next();
        });
        return app;
    }

    /// <summary>The key authentication failures are counted under: the caller's network address.</summary>
    public static string CallerKey(this HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>The answer for a caller that is being turned away: 429 with Retry-After, in the standard error shape.</summary>
    public static IResult TooManyRequests(this HttpContext http, ThrottleDecision decision)
    {
        http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString();
        return Results.Json(new ApiError(CloudErrorCodes.TooManyRequests, "Too many failed attempts. Try again later."), statusCode: 429);
    }
}
