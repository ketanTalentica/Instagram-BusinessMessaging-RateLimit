using System.Collections.Concurrent;
using System.Threading.Channels;
using InstagramSenderApi.Instagram.Client;
using InstagramSenderApi.Instagram.Services;

namespace InstagramSenderApi.Instagram.Workers;

/// <summary>
/// Background worker that maintains one bounded Channel per tenant and processes
/// jobs in strict per-tenant order. Applies InstagramThrottleGuard before every
/// outbound call; re-enqueues on TenantBlockedException after the required delay.
/// </summary>
public sealed class SendQueueWorker : BackgroundService
{
    private const int ChannelCapacity = 500;
    private const int MaxAttempts     = 5;

    private readonly ConcurrentDictionary<string, Channel<SendJob>> _channels =
        new(StringComparer.OrdinalIgnoreCase);

    // Tracks the running processor Task per tenant so we don't start duplicates
    private readonly ConcurrentDictionary<string, Task> _processors =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly InstagramThrottleGuard _throttleGuard;
    private readonly IServiceScopeFactory   _scopeFactory;
    private readonly ILogger<SendQueueWorker> _logger;

    private CancellationToken _stoppingToken;

    public SendQueueWorker(
        InstagramThrottleGuard throttleGuard,
        IServiceScopeFactory   scopeFactory,
        ILogger<SendQueueWorker> logger)
    {
        _throttleGuard = throttleGuard;
        _scopeFactory  = scopeFactory;
        _logger        = logger;
    }

    public enum EnqueueResult { Enqueued, Full, Closed }

    /// <summary>
    /// Enqueue a job without blocking. Never awaits channel space: the per-tenant processor
    /// re-enqueues into its own channel, and awaiting a full channel there would deadlock
    /// (the processor is the channel's only reader). A Full result maps to HTTP 429 upstream.
    /// </summary>
    public EnqueueResult TryEnqueue(SendJob job)
    {
        var channel = _channels.GetOrAdd(
            job.TenantId,
            _ => Channel.CreateBounded<SendJob>(new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            }));

        if (channel.Writer.TryWrite(job))
        {
            EnsureProcessorRunning(job.TenantId);
            return EnqueueResult.Enqueued;
        }

        return channel.Reader.Completion.IsCompleted ? EnqueueResult.Closed : EnqueueResult.Full;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        return Task.CompletedTask; // processors start lazily on first enqueue
    }

    private void EnsureProcessorRunning(string tenantId)
    {
        _processors.AddOrUpdate(
            tenantId,
            _  => ProcessTenantChannelAsync(tenantId, _stoppingToken),
            (_, existing) =>
            {
                if (existing.IsCompleted)
                    return ProcessTenantChannelAsync(tenantId, _stoppingToken);
                return existing;
            });
    }

    private async Task ProcessTenantChannelAsync(string tenantId, CancellationToken ct)
    {
        if (!_channels.TryGetValue(tenantId, out var channel)) return;

        await foreach (var job in channel.Reader.ReadAllAsync(ct))
            await ProcessJobAsync(job, ct);
    }

    private async Task ProcessJobAsync(SendJob job, CancellationToken ct)
    {
        if (job.AttemptCount >= MaxAttempts)
        {
            _logger.LogError(
                "Job for tenant {TenantId} → {Endpoint} dropped after {Max} attempts",
                job.TenantId, job.TargetEndpoint, MaxAttempts);
            return;
        }

        try
        {
            // Classify first: Meta's per-second cap for this call may be 100/s or 2/s
            var dispatchClass = DispatchClassifier.Classify(job.TargetEndpoint, job.Payload);
            await _throttleGuard.EnforceAsync(job.TenantId, dispatchClass, ct);

            // IInstagramClient is transient — resolve per-job via a scope
            using var scope  = _scopeFactory.CreateScope();
            var client       = scope.ServiceProvider.GetRequiredService<IInstagramClient>();
            var result       = await client.PostAsync(job.TenantId, job.TargetEndpoint, job.Payload, ct);

            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "Sent job for tenant {TenantId} → {Endpoint}", job.TenantId, job.TargetEndpoint);
            }
            else if (result.IsRateLimited)
            {
                // Rate-limited after Polly retries were exhausted (or circuit is open).
                // The block window is already persisted; pause this tenant's queue, then re-queue
                // the job so it is not lost. Pausing here is safe — this loop is per-tenant.
                var delay = result.RetryAfterMinutes > 0
                    ? TimeSpan.FromMinutes(result.RetryAfterMinutes + 1)
                    : TimeSpan.FromSeconds(30);
                _logger.LogWarning(
                    "Tenant {TenantId} rate-limited ({Status}) — pausing {Delay:g}, then re-queuing (attempt {Attempt})",
                    job.TenantId, result.StatusCode, delay, job.AttemptCount + 1);
                await Task.Delay(delay, ct);
                Requeue(job);
            }
            else
            {
                _logger.LogWarning(
                    "Non-success {Status} for tenant {TenantId} → {Endpoint} — job dropped (non-retryable)",
                    result.StatusCode, job.TenantId, job.TargetEndpoint);
            }
        }
        catch (TenantBlockedException ex)
        {
            _logger.LogWarning(
                "Tenant {TenantId} blocked — re-queuing after {Delay:g}", job.TenantId, ex.RetryAfter);
            await Task.Delay(ex.RetryAfter, ct);
            Requeue(job);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown — drop the job (add outbox pattern for durability)
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing job for tenant {TenantId}", job.TenantId);
        }
    }

    private void Requeue(SendJob job)
    {
        if (TryEnqueue(job.WithNextAttempt()) != EnqueueResult.Enqueued)
            _logger.LogError(
                "Could not re-queue job for tenant {TenantId} → {Endpoint} (channel full/closed) — job dropped",
                job.TenantId, job.TargetEndpoint);
    }
}
