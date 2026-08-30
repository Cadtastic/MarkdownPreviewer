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

    /// <summary>For kind "imageText": the extracted text per image.</summary>
    [JsonPropertyName("images")]
    public ImageTextEntry[]? Images { get; init; }

    /// <summary>
    /// For kind "render": increments only when the previewed document changes
    /// identity. A redraw of the same document (theme flip, trust change)
    /// repeats the previous value, which is how the page tells "the reader
    /// moved on" from "we drew this again".
    /// </summary>
    [JsonPropertyName("documentGeneration")]
    public long DocumentGeneration { get; init; }

    /// <summary>
    /// For kind "render": the document's file name, shown in the page's trust
    /// dialog. A security prompt that cannot name what it is about is not worth
    /// showing, and the page only knows the document by a virtual host URL.
    /// </summary>
    [JsonPropertyName("documentName")]
    public string? DocumentName { get; init; }

    /// <summary>
    /// For kind "render": whether the user has trusted this document to load
    /// resources from the internet.
    /// </summary>
    [JsonPropertyName("trusted")]
    public bool Trusted { get; init; }

    /// <summary>
    /// For kind "render": whether the document's task-list checkboxes may be
    /// edited from the preview. False for stream-fed items (no file to write)
    /// and for truncated documents (the page's line numbers would describe a
    /// file the editor is not looking at).
    /// </summary>
    [JsonPropertyName("taskEditable")]
    public bool TaskEditable { get; init; }

    /// <summary>
    /// For kind "render": whether this document already had checkbox editing
    /// turned on. Per document, so the answer travels with the file rather than
    /// arming every document the reader opens next.
    /// </summary>
    [JsonPropertyName("taskEditOn")]
    public bool TaskEditOn { get; init; }

    /// <summary>
    /// For kind "render": whether trust can be granted at all. False for
    /// stream-fed items, which have no path to record a grant against — the page
    /// disables its Trust control and says why rather than offering a switch
    /// that would silently forget.
    /// </summary>
    [JsonPropertyName("trustable")]
    public bool Trustable { get; init; }
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

    /// <summary>
    /// The standing preference, which the page needs in order to decide whether
    /// to emit a remote image URL at all. The host still refuses the request
    /// independently — that is the security boundary — but a page that knows
    /// the answer up front never issues a request it expects to be refused, and
    /// stops showing images the moment permission goes away.
    /// </summary>
    [JsonPropertyName("allowRemoteImages")] public bool AllowRemoteImages { get; init; }

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

    /// <summary>For kind "imageTextRequest": the images the page wants text for.</summary>
    [JsonPropertyName("urls")]        public string[]? Urls { get; init; }

    /// <summary>
    /// For kind "toggleTask": the 0-based source line of the task marker, and
    /// the state the reader just put the checkbox into. The host verifies the
    /// line against the file before flipping anything.
    /// </summary>
    [JsonPropertyName("line")]        public long Line { get; init; } = -1;

    /// <summary>For kind "toggleTask": the checkbox's new state.</summary>
    [JsonPropertyName("checked")]     public bool Checked { get; init; }

    /// <summary>For kind "setTaskEdit": whether editing is being turned on.</summary>
    [JsonPropertyName("enabled")]     public bool Enabled { get; init; }

    /// <summary>
    /// For kind "openDocument": what the reader wants done with the link —
    /// "navigate" reveals the file in Explorer, anything else (or nothing, from
    /// an older page) launches it in its default application.
    /// </summary>
    [JsonPropertyName("mode")]        public string? Mode { get; init; }

    /// <summary>
    /// For kind "trustDocument": the state the user just chose. The page has
    /// already shown its confirmation dialog by the time this arrives; the host
    /// records the grant and re-renders so the newly permitted images load.
    /// </summary>
    [JsonPropertyName("trusted")]     public bool Trusted { get; init; }
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
[JsonSerializable(typeof(ImageTextEntry))]
internal sealed partial class PreviewJsonContext : JsonSerializerContext
{
}
