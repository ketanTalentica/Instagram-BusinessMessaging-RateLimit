using System.Text;

namespace WebhookTrafficSimulator.Infrastructure;

public static class PayloadFactory
{
    private static readonly byte[] SmallPayload = Encoding.UTF8.GetBytes(
        """{"event":"webhook.test","timestamp":"2026-06-19T00:00:00Z","data":{"id":"test-123","value":"hello"}}""");

    public static byte[] CreateSmall() => SmallPayload;

    public static byte[] CreateOversized(int megabytes)
    {
        // Build a JSON body that exceeds the size limit
        var padLength = megabytes * 1024 * 1024;
        var sb = new StringBuilder(padLength + 32);
        sb.Append("{\"event\":\"webhook.test\",\"pad\":\"");
        sb.Append('x', padLength);
        sb.Append("\"}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
