using System.Text.Json.Serialization;

namespace MarkdownPreviewer.Rendering.Messaging;

/// <summary>
/// One image's extracted text, sent to the page in an <c>imageText</c> message
/// so the search can match what the image draws.
/// </summary>
internal sealed class ImageTextEntry
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}
