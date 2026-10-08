using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WebhookTrafficSimulator.Infrastructure;

/// <summary>
/// HttpClient wrapper that builds valid or invalid HMAC signatures and sets the
/// X-Forwarded-For / X-Webhook-Source headers required by WebhookIngestApi.
/// </summary>
public sealed class WebhookHttpSender
{
    private readonly HttpClient      _client;
    private readonly SimulatorOptions _options;

    public WebhookHttpSender(HttpClient client, SimulatorOptions options)
    {
        _client  = client;
        _options = options;
    }

    /// <param name="path">Override the default webhook path (e.g. /webhook/slow).</param>
    /// <param name="clientId">Sets X-Webhook-Source header (per-client rate limit key).</param>
    /// <param name="virtualIp">Sets X-Forwarded-For header (per-IP rate limit key).</param>
    /// <param name="payload">Raw body bytes. Defaults to a small valid JSON payload.</param>
    /// <param name="validHmac">True = compute correct HMAC; false = send garbage signature.</param>
    public async Task<(int StatusCode, Dictionary<string, string> Headers, long LatencyMs)> SendAsync(
        string?  path      = null,
        string?  clientId  = null,
        string?  virtualIp = null,
        byte[]?  payload   = null,
        bool     validHmac = true,
        CancellationToken ct = default)
    {
        var body = payload ?? PayloadFactory.CreateSmall();
        var sw   = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Post, path ?? _options.WebhookPath);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        if (clientId  is not null) request.Headers.TryAddWithoutValidation("X-Webhook-Source", clientId);
        if (virtualIp is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-For",  virtualIp);

        request.Headers.TryAddWithoutValidation(
            "X-Hub-Signature-256",
            validHmac ? ComputeHmac(body) : "sha256=invalidsignaturedeadbeefdeadbeef");

        try
        {
            using var response = await _client.SendAsync(request, ct);
            sw.Stop();

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in new[] { "Retry-After", "X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Reset" })
                if (response.Headers.TryGetValues(h, out var vals))
                    headers[h] = vals.First();

            return ((int)response.StatusCode, headers, sw.ElapsedMilliseconds);
        }
        catch (Exception)
        {
            sw.Stop();
            return (0, [], sw.ElapsedMilliseconds);
        }
    }

    private string ComputeHmac(byte[] body)
    {
        if (string.IsNullOrEmpty(_options.HmacSecret)) return "sha256=nosecret";
        var key  = Encoding.UTF8.GetBytes(_options.HmacSecret);
        var hash = HMACSHA256.HashData(key, body);
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
