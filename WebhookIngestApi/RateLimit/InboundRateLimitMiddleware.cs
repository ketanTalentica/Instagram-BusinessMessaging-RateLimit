using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WebhookIngestApi.RateLimit;

/// <summary>
/// ASP.NET Core middleware that intercepts all requests before routing.
/// On deny: short-circuits with the correct HTTP status + Retry-After + X-RateLimit-* headers.
/// On allow: calls next(ctx). The concurrency slot taken during evaluation is released by
/// ConcurrencyRule on Response.OnCompleted, not here — it must outlive this method.
/// </summary>
public sealed class InboundRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly InboundRateLimitPipeline _service;
    private readonly InboundRateLimitOptions _options;
    private readonly ILogger<InboundRateLimitMiddleware> _logger;

    public InboundRateLimitMiddleware(
        RequestDelegate next,
        InboundRateLimitPipeline service,
        IOptions<InboundRateLimitOptions> options,
        ILogger<InboundRateLimitMiddleware> logger)
    {
        _next    = next;
        _service = service;
        _options = options.Value;
        _logger  = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!_options.Enabled || IsBypassed(ctx))
        {
            await _next(ctx);
            return;
        }

        var eval = await _service.EvaluateAsync(ctx, ctx.RequestAborted);

        if (!eval.IsAllowed)
        {
            if (_options.ObserveOnly)
            {
                var (wouldBeStatus, _) = MapRejection(eval.Reason);
                _logger.LogWarning(
                    "OBSERVE ONLY: would deny {DenialReason} → {StatusCode} for {Method} {Path} (retryAfter={RetryAfter}s)",
                    eval.Reason, wouldBeStatus, ctx.Request.Method, ctx.Request.Path, eval.RetryAfterSeconds);
                await _next(ctx);
                return;
            }

            await WriteRejectionAsync(ctx, eval);
            return;
        }

        await _next(ctx);
    }

    /// <summary>
    /// GETs always bypass — Meta's hub.challenge webhook verification handshake is a GET
    /// and must never be throttled. ExcludedPaths covers health probes, swagger, etc.
    /// </summary>
    private bool IsBypassed(HttpContext ctx)
    {
        if (HttpMethods.IsGet(ctx.Request.Method))
            return true;

        foreach (var excluded in _options.ExcludedPaths)
        {
            if (string.IsNullOrWhiteSpace(excluded)) continue;
            var prefix = excluded.StartsWith('/') ? excluded : "/" + excluded;
            if (ctx.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static (int StatusCode, string ErrorCode) MapRejection(DenialReason reason) => reason switch
    {
        DenialReason.Blocked        => (StatusCodes.Status403Forbidden,             "access_denied"),
        DenialReason.Signature      => (StatusCodes.Status401Unauthorized,          "invalid_signature"),
        DenialReason.PayloadSize    => (StatusCodes.Status413RequestEntityTooLarge, "payload_too_large"),
        DenialReason.LengthRequired => (StatusCodes.Status411LengthRequired,        "length_required"),
        _                        => (StatusCodes.Status429TooManyRequests,       "rate_limit_exceeded")
    };

    private async Task WriteRejectionAsync(HttpContext ctx, LimitDecision eval)
    {
        var (statusCode, errorCode) = MapRejection(eval.Reason);

        ctx.Response.StatusCode  = statusCode;
        ctx.Response.ContentType = "application/json";

        if (statusCode == StatusCodes.Status429TooManyRequests)
        {
            ctx.Response.Headers["Retry-After"]          = eval.RetryAfterSeconds.ToString();
            ctx.Response.Headers["X-RateLimit-Limit"]    = GetLimit(eval.Reason).ToString();
            ctx.Response.Headers["X-RateLimit-Remaining"] = eval.Remaining.ToString();
            ctx.Response.Headers["X-RateLimit-Reset"]    =
                DateTimeOffset.UtcNow.AddSeconds(eval.RetryAfterSeconds).ToUnixTimeSeconds().ToString();
        }

        _logger.LogWarning(
            "Request denied: {DenialReason} → {StatusCode} for path {Path}",
            eval.Reason, statusCode, ctx.Request.Path);

        await ctx.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                error             = errorCode,
                retryAfterSeconds = eval.RetryAfterSeconds
            }));
    }

    private int GetLimit(DenialReason reason) => reason switch
    {
        DenialReason.Global      => _options.GlobalLimitPerMinute,
        DenialReason.Ip          => _options.PerIpLimitPerMinute,
        DenialReason.Client      => _options.PerClientLimitPerMinute,
        DenialReason.Concurrency => _options.MaxConcurrencyPerClient,
        _                     => 0
    };
}
