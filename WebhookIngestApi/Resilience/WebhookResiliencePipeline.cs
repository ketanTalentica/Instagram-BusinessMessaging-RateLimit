using Polly;
using Polly.CircuitBreaker;

namespace WebhookIngestApi.Resilience;

/// <summary>
/// Polly v8 resilience pipeline that wraps downstream webhook job processing
/// (not the rate-limiting itself). Registered as a named pipeline in DI.
/// </summary>
public static class WebhookResiliencePipeline
{
    public const string PipelineName = "webhook-processing";

    public static void Register(IServiceCollection services)
    {
        services.AddResiliencePipeline(PipelineName, static builder =>
        {
            builder
                .AddTimeout(TimeSpan.FromSeconds(5))
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio      = 0.5,
                    MinimumThroughput = 10,
                    SamplingDuration  = TimeSpan.FromSeconds(30),
                    BreakDuration     = TimeSpan.FromSeconds(15)
                });
        });
    }
}
