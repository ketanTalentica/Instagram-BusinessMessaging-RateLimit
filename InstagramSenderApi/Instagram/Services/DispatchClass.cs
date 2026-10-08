using System.Text.Json;

namespace InstagramSenderApi.Instagram.Services;

/// <summary>
/// Meta's per-second caps differ per call class, not per account alone
/// (developers.facebook.com/docs/instagram-platform → messaging, read 2026-08-04):
/// text/links/reactions/stickers 100/s, audio/video 10/s, Conversations API 2/s.
/// One cap for all of them overruns the tighter classes by 10–50×, so every call is
/// classified before it reaches <see cref="PerSecondDispatchGate"/>.
/// </summary>
public enum DispatchClass
{
    /// <summary>Text, links, reactions, stickers — 100/s per account.</summary>
    TextSend,

    /// <summary>Audio or video attachment — 10/s per account.</summary>
    MediaSend,

    /// <summary>Conversations API reads — 2/s per account.</summary>
    Conversations,

    /// <summary>Content publishing (/media, /media_publish) — no documented per-second cap.</summary>
    ContentPublish,

    /// <summary>Endpoint not recognised — treated as the tightest class.</summary>
    Unclassified
}

/// <summary>
/// Maps an outbound Graph API call to its per-second limit class. The endpoint alone is
/// not sufficient: text and audio/video sends share the <c>/messages</c> endpoint and are
/// told apart only by the attachment type in the payload.
/// </summary>
public static class DispatchClassifier
{
    private static readonly string[] MediaAttachmentTypes = ["audio", "video"];

    public static DispatchClass Classify(string? relativeUrl, JsonElement payload)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl))
            return DispatchClass.Unclassified;

        var url = relativeUrl.AsSpan();

        if (url.Contains("/conversations", StringComparison.OrdinalIgnoreCase))
            return DispatchClass.Conversations;

        if (url.Contains("/media_publish", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/media",         StringComparison.OrdinalIgnoreCase))
            return DispatchClass.ContentPublish;

        if (url.Contains("/messages", StringComparison.OrdinalIgnoreCase))
            return HasAudioOrVideoAttachment(payload)
                ? DispatchClass.MediaSend
                : DispatchClass.TextSend;

        return DispatchClass.Unclassified;
    }

    /// <summary>
    /// Looks for <c>message.attachment.type</c> = audio|video, the shape Meta's Send API
    /// uses. A malformed or unexpected payload falls back to the text class rather than
    /// throwing — misclassifying one call must never fail the send.
    /// </summary>
    private static bool HasAudioOrVideoAttachment(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        if (!payload.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object)
            return false;

        if (!message.TryGetProperty("attachment", out var attachment) ||
            attachment.ValueKind != JsonValueKind.Object)
            return false;

        if (!attachment.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
            return false;

        var value = type.GetString();
        return value is not null &&
               MediaAttachmentTypes.Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}
