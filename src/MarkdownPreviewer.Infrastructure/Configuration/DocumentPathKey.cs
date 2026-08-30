namespace MarkdownPreviewer.Infrastructure.Configuration;

/// <summary>
/// Turns a document path into the registry value name that per-document
/// settings are stored under.
/// </summary>
/// <remarks>
/// Shared by every per-document store so the rules live in one place. Two of
/// them are load-bearing rather than cosmetic:
///
/// <list type="bullet">
///   <item>The path is canonicalised, so one file cannot end up with two
///   independent settings under two spellings.</item>
///   <item>An unrooted path is rejected <b>before</b> canonicalisation.
///   <c>Path.GetFullPath</c> would happily root a bare name against the
///   current directory, which would let a display name like "notes.md" — all a
///   stream-fed item has — stand for every file that shares that name.</item>
/// </list>
///
/// Registry value names may contain backslashes (only key names may not) and
/// compare case-insensitively, which is how <c>DocumentLocation</c> already
/// compares paths, so the entry written for <c>C:\Docs\Notes.md</c> is found
/// again for <c>c:\docs\notes.md</c>.
/// </remarks>
internal static class DocumentPathKey
{
    public static bool TryNormalise(string? fullPath, out string normalised)
    {
        normalised = string.Empty;

        if (string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathRooted(fullPath))
        {
            return false;
        }

        try
        {
            normalised = Path.GetFullPath(fullPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
