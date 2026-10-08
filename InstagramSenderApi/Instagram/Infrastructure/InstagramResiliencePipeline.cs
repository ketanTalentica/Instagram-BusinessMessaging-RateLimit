using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace InstagramSenderApi.Instagram.Infrastructure;

/// <summary>
/// Polly v8 resilience pipeline for outbound Instagram API calls.
/// Strategy order (first added = outermost): Retry → CircuitBreaker → Timeout.
/// The timeout is therefore per attempt (15 s), not per whole call chain — a long
/// retry delay taken from estimated_time_to_regain_access is never cut short.
///
/// Retry delay uses estimated_time_to_regain_access from the Instagram error response
/// (set by InstagramRateLimitHandler on the request options). Falls back to exponential
/// backoff with jitter: 2 s, 4 s, 8 s. The jitter is load-bearing, not cosmetic — instances that
/// backed off together would otherwise retry together and rebuild the burst they just avoided.
///
/// One pipeline instance is created per tenant by InstagramClient via
/// ResiliencePipelineRegistry, so one tenant's circuit breaker tripping
/// does not affect other tenants.
/// </summary>
public static class InstagramResiliencePipeline
{
    /// <summary>
    /// Rate-limit blocks longer than this are not retried inline; the block is already
    /// persisted to SQL by InstagramRateLimitHandler, so SendQueueWorker re-enqueues the
    /// job and InstagramThrottleGuard holds the tenant queue until the block expires.
    /// Meta guidance: when the limit is hit, stop calling — do not keep probing.
    /// </summary>
    private static readonly TimeSpan MaxInlineRetryDelay = TimeSpan.FromMinutes(2);

    public static void Configure(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        builder
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                BackoffType      = DelayBackoffType.Exponential,
                Delay            = TimeSpan.FromSeconds(2),
                UseJitter        = true,

                ShouldHandle = args =>
                {
                    if (args.Outcome.Exception is HttpRequestException
                                               or TimeoutRejectedException
                                               or BrokenCircuitException)
                        return new ValueTask<bool>(true);

                    if (!IsRateLimited(args.Outcome.Result))
                        return new ValueTask<bool>(false);

                    // Long hard blocks are handled by the queue, not by sleeping here
                    return new ValueTask<bool>(RetryAfter(args.Outcome.Result) <= MaxInlineRetryDelay);
                },

                DelayGenerator = args =>
                {
                    // Honour Instagram's own estimated recovery window when present
                    if (IsRateLimited(args.Outcome.Result))
                    {
                        var retryAfter = RetryAfter(args.Outcome.Result);
                        if (retryAfter > TimeSpan.Zero)
                            return new ValueTask<TimeSpan?>(retryAfter);
                    }

                    // Exponential backoff: 2 s → 4 s → 8 s
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber + 1));
                    return new ValueTask<TimeSpan?>(delay);
                },

                // Each attempt sends a fresh HttpRequestMessage (see InstagramClient),
                // so the previous attempt's response is no longer needed.
                OnRetry = args =>
                {
                    args.Outcome.Result?.Dispose();
                    return default;
                }
            })

            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio       = 0.8,
                MinimumThroughput  = 3,
                SamplingDuration   = TimeSpan.FromMinutes(2),
                BreakDuration      = TimeSpan.FromMinutes(5),

                ShouldHandle = args =>
                {
                    if (args.Outcome.Exception is HttpRequestException or TimeoutRejectedException)
                        return new ValueTask<bool>(true);

                    return new ValueTask<bool>(IsRateLimited(args.Outcome.Result));
                }
            })

            .AddTimeout(TimeSpan.FromSeconds(15)); // innermost: 15 s per individual attempt
    }

    private static bool IsRateLimited(HttpResponseMessage? response) =>
        response?.RequestMessage?.Options
            .TryGetValue(InstagramRateLimitHandler.IsRateLimitedKey, out var limited) == true && limited;

    private static TimeSpan RetryAfter(HttpResponseMessage? response)
    {
        if (response?.RequestMessage?.Options
                .TryGetValue(InstagramRateLimitHandler.RetryAfterMinutesKey, out var minutes) == true
            && minutes > 0)
            return TimeSpan.FromMinutes(minutes + 1); // 1-minute safety buffer

        return TimeSpan.Zero;
    }
}
