using System.Runtime.CompilerServices;

// The render surface itself needs COM and a live browser to exercise, but the
// pure pieces (SVG text extraction) are unit-testable and internal.
[assembly: InternalsVisibleTo("MarkdownPreviewer.Tests")]
