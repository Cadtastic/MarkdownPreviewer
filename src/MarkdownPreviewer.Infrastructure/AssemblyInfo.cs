using System.Runtime.CompilerServices;

// The shell-automation revealer needs a live Explorer to exercise, but its
// pure pieces (URL-to-path translation, path equality) are unit-testable and
// internal.
[assembly: InternalsVisibleTo("MarkdownPreviewer.Tests")]
