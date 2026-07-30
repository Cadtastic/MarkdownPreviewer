using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkdownPreviewer.Rendering.Messaging;

/// <summary>Message sent from the host to the render page.</summary>
internal sealed class HostToPageMessage
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("token")]
    public long Token { get; init; }

    [JsonPropertyName("markdown")]
    public string? Markdown { get; init; }

    [JsonPropertyName("theme")]
    public string? Theme { get; init; }

    [JsonPropertyName("docBase")]
    public string? DocBase { get; init; }

    [JsonPropertyName("settings")]
    public PageSettings? Settings { get; init; }
}

/// <summary>
/// Settings projected into the shape the page expects.
/// </summary>
/// <remarks>
/// A separate DTO rather than serialising <c>RenderSettings</c> directly: the
/// domain record must not be shackled to a wire format, and several fields
/// (theme selection, byte caps) are resolved host-side and are meaningless to the
/// page.
/// </remarks>
internal sealed class PageSettings
{
    [JsonPropertyName("allowRawHtml")]     public bool AllowRawHtml { get; init; }
    [JsonPropertyName("linkify")]          public bool Linkify { get; init; }
    [JsonPropertyName("typographer")]      public bool Typographer { get; init; }
    [JsonPropertyName("highlight")]        public bool Highlight { get; init; }
    [JsonPropertyName("mermaid")]          public bool Mermaid { get; init; }
    [JsonPropertyName("math")]             public bool Math { get; init; }
    [JsonPropertyName("singleDollarMath")] public bool SingleDollarMath { get; init; }
    [JsonPropertyName("taskLists")]        public bool TaskLists { get; init; }
    [JsonPropertyName("showFrontMatter")]  public bool ShowFrontMatter { get; init; }
    [JsonPropertyName("fontScalePercent")] public int FontScalePercent { get; init; }
    [JsonPropertyName("maxAutoDetectBytes")] public int MaxAutoDetectBytes { get; init; } = 10240;
}

/// <summary>Message sent from the render page back to the host.</summary>
internal sealed class PageToHostMessage
{
    [JsonPropertyName("kind")]        public string Kind { get; init; } = string.Empty;
    [JsonPropertyName("token")]       public long Token { get; init; }
    [JsonPropertyName("version")]     public string? Version { get; init; }
    [JsonPropertyName("elapsedMs")]   public double ElapsedMs { get; init; }
    [JsonPropertyName("usedMermaid")] public bool UsedMermaid { get; init; }
    [JsonPropertyName("usedMath")]    public bool UsedMath { get; init; }
    [JsonPropertyName("warnings")]    public string[]? Warnings { get; init; }
    [JsonPropertyName("message")]     public string? Message { get; init; }
    [JsonPropertyName("url")]         public string? Url { get; init; }
    [JsonPropertyName("reason")]      public string? Reason { get; init; }
}

/// <summary>
/// Source-generated serialisation context.
/// </summary>
/// <remarks>
/// Reflection-based <c>System.Text.Json</c> would work, but the generator removes
/// startup reflection cost on a code path that runs on every file selection, and
/// it keeps the door open for a NativeAOT build later.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HostToPageMessage))]
[JsonSerializable(typeof(PageToHostMessage))]
[JsonSerializable(typeof(PageSettings))]
internal sealed partial class PreviewJsonContext : JsonSerializerContext
{
}
