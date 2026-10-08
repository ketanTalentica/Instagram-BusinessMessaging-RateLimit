using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using InstagramSenderApi.Instagram.Workers;

namespace InstagramSenderApi.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class SendController : ControllerBase
{
    private readonly SendQueueWorker _queueWorker;

    public SendController(SendQueueWorker queueWorker) => _queueWorker = queueWorker;

    /// <summary>
    /// Enqueues an outbound send job.
    /// Returns 202 Accepted immediately — the job is processed asynchronously by SendQueueWorker.
    /// </summary>
    [HttpPost]
    public IActionResult Post([FromBody] SendRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
            return BadRequest(new { error = "TenantId is required." });

        if (string.IsNullOrWhiteSpace(request.TargetEndpoint))
            return BadRequest(new { error = "TargetEndpoint is required." });

        var job = new SendJob(request.TenantId, request.TargetEndpoint, request.Payload);

        switch (_queueWorker.TryEnqueue(job))
        {
            case SendQueueWorker.EnqueueResult.Enqueued:
                return Accepted(new { message = "Job enqueued.", tenantId = request.TenantId });
            case SendQueueWorker.EnqueueResult.Full:
                Response.Headers.RetryAfter = "5";
                return StatusCode(429, new { error = "Tenant queue is full. Retry later." });
            default:
                return StatusCode(503, new { error = "Queue unavailable." });
        }
    }
}

public sealed record SendRequest(
    string TenantId,
    string TargetEndpoint,
    JsonElement Payload);
