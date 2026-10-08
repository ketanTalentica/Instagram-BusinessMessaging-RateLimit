using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Timeout;
using InstagramSenderApi.Instagram.Infrastructure;

namespace InstagramSenderApi.Instagram.Client;

public sealed record InstagramApiResult(
    bool IsSuccess,
    System.Net.HttpStatusCode StatusCode,
    string? ResponseBody = null,
    bool IsRateLimited = false,
    int RetryAfterMinutes = 0);

public interface IInstagramClient
{
    Task<InstagramApiResult> PostAsync(
        string tenantId,
        string relativeUrl,
        object payload,
        CancellationToken ct = default);
}

/// <summary>
/// Typed HttpClient for Instagram Graph API.
/// Executes every call inside a per-tenant Polly pipeline (retry → circuit breaker → timeout)
/// so one tenant's failures never trip another tenant's circuit. A fresh HttpRequestMessage
/// is built per attempt — HttpRequestMessage instances cannot be re-sent.
/// Sets TenantId on every request so InstagramRateLimitHandler can tag the response.
/// </summary>
public sealed class InstagramClient : IInstagramClient
{
    private readonly HttpClient _httpClient;
    private readonly ResiliencePipelineRegistry<string> _pipelineRegistry;

    public InstagramClient(HttpClient httpClient, ResiliencePipelineRegistry<string> pipelineRegistry)
    {
        _httpClient       = httpClient;
        _pipelineRegistry = pipelineRegistry;
    }

    public async Task<InstagramApiResult> PostAsync(
        string tenantId,
        string relativeUrl,
        object payload,
        CancellationToken ct = default)
    {
        var pipeline = _pipelineRegistry.GetOrAddPipeline<HttpResponseMessage>(
            $"instagram:{tenantId}",
            builder => InstagramResiliencePipeline.Configure(builder));

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);

        try
        {
            using var response = await pipeline.ExecuteAsync(async token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
                request.Options.Set(InstagramRateLimitHandler.TenantIdKey, tenantId);
                request.Content = new ByteArrayContent(payloadBytes);
                request.Content.Headers.ContentType = new("application/json");
                return await _httpClient.SendAsync(request, token);
            }, ct);

            var body = await response.Content.ReadAsStringAsync(ct);

            var isRateLimited = response.RequestMessage?.Options
                .TryGetValue(InstagramRateLimitHandler.IsRateLimitedKey, out var limited) == true && limited;
            var retryAfter = 0;
            if (isRateLimited)
                response.RequestMessage!.Options
                    .TryGetValue(InstagramRateLimitHandler.RetryAfterMinutesKey, out retryAfter);

            return new InstagramApiResult(
                response.IsSuccessStatusCode, response.StatusCode, body, isRateLimited, retryAfter);
        }
        catch (BrokenCircuitException)
        {
            // Circuit open for this tenant — signal the worker to re-enqueue and back off
            return new InstagramApiResult(
                false, System.Net.HttpStatusCode.ServiceUnavailable, null, IsRateLimited: true);
        }
        catch (TimeoutRejectedException)
        {
            return new InstagramApiResult(false, System.Net.HttpStatusCode.RequestTimeout);
        }
        catch (HttpRequestException)
        {
            return new InstagramApiResult(false, System.Net.HttpStatusCode.ServiceUnavailable);
        }
    }
}
