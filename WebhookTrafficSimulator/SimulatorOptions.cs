namespace WebhookTrafficSimulator;

public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    public string TargetBaseUrl          { get; set; } = "http://localhost:5002";
    public string WebhookPath            { get; set; } = "/webhook";
    public string SlowWebhookPath        { get; set; } = "/webhook/slow";
    public string HmacSecret             { get; set; } = string.Empty;
    public int    DefaultDurationSeconds { get; set; } = 30;
    public int    VirtualIpCount         { get; set; } = 50;
    public int    OversizedPayloadMb     { get; set; } = 2;
}
