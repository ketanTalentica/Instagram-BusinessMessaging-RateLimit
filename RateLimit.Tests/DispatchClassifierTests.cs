using System.Text.Json;
using InstagramSenderApi.Instagram.Services;
using Xunit;

namespace RateLimit.Tests;

/// <summary>
/// Classification is what selects the per-second cap, and text and audio/video sends share the
/// same endpoint — so the payload, not the URL, is what separates a 100/s call from a 10/s one.
/// A wrong answer here is a 10x overrun of a real Meta limit.
/// </summary>
public class DispatchClassifierTests
{
    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    [InlineData("VIDEO")]
    public void An_audio_or_video_attachment_makes_a_messages_call_media_class(string type)
    {
        var payload = Payload("{\"message\":{\"attachment\":{\"type\":\"" + type + "\"}}}");

        Assert.Equal(DispatchClass.MediaSend,
            DispatchClassifier.Classify("/v25.0/t/messages", payload));
    }

    [Theory]
    [InlineData("""{"message":{"text":"hi"}}""")]
    [InlineData("""{"message":{"attachment":{"type":"image"}}}""")]
    [InlineData("{}")]
    public void Everything_else_on_messages_is_text_class(string json)
    {
        Assert.Equal(DispatchClass.TextSend,
            DispatchClassifier.Classify("/v25.0/t/messages", Payload(json)));
    }

    [Fact]
    public void Conversations_is_recognised_from_the_endpoint()
    {
        Assert.Equal(DispatchClass.Conversations,
            DispatchClassifier.Classify("/v25.0/t/conversations", Payload("{}")));
    }

    [Theory]
    [InlineData("/v25.0/t/media")]
    [InlineData("/v25.0/t/media_publish")]
    public void Publishing_endpoints_are_their_own_class(string url)
    {
        Assert.Equal(DispatchClass.ContentPublish, DispatchClassifier.Classify(url, Payload("{}")));
    }

    [Theory]
    [InlineData("/v25.0/t/something_new")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unrecognised_endpoint_is_unclassified_and_therefore_capped_tightest(string? url)
    {
        Assert.Equal(DispatchClass.Unclassified, DispatchClassifier.Classify(url, Payload("{}")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"just-a-string\"")]
    [InlineData("""{"message":"not-an-object"}""")]
    [InlineData("""{"message":{"attachment":{"type":123}}}""")]
    public void A_malformed_payload_falls_back_to_text_rather_than_failing_the_send(string json)
    {
        // Misclassifying one call is recoverable; throwing here would drop it entirely.
        Assert.Equal(DispatchClass.TextSend,
            DispatchClassifier.Classify("/v25.0/t/messages", Payload(json)));
    }
}
