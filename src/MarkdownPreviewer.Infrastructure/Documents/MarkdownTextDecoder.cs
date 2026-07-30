using System.Text;

namespace MarkdownPreviewer.Infrastructure.Documents;

/// <summary>
/// Decodes Markdown bytes to text, detecting the encoding from a byte-order mark
/// and falling back to UTF-8.
/// </summary>
/// <remarks>
/// Markdown files in the wild are overwhelmingly UTF-8, but Windows tooling still
/// emits UTF-16 LE with a BOM (PowerShell's <c>&gt;</c> redirection historically
/// did exactly that), and files exported from older editors can be ANSI. Getting
/// this wrong shows up as mojibake or as a document that renders as one line of
/// NUL-separated characters, so the detection is explicit rather than implicit.
/// </remarks>
internal static class MarkdownTextDecoder
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Longest BOM we look for.</summary>
    public const int MaximumPreambleLength = 4;

    /// <summary>
    /// Decodes <paramref name="bytes"/>, honouring any BOM.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding = DetectEncoding(bytes, out int preambleLength);
        ReadOnlySpan<byte> payload = bytes[preambleLength..];

        string text = encoding.GetString(payload);

        // Trim a stray BOM that survived decoding (happens with UTF-8 content
        // written by tools that emit the BOM twice), and normalise line endings
        // so the JS side never sees a lone CR.
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace('\r', '\n');
    }

    private static Encoding DetectEncoding(ReadOnlySpan<byte> bytes, out int preambleLength)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            preambleLength = 3;
            return Utf8NoBom;
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            preambleLength = 4;
            return new UTF32Encoding(bigEndian: false, byteOrderMark: false);
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            preambleLength = 4;
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            preambleLength = 2;
            return Encoding.Unicode;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            preambleLength = 2;
            return Encoding.BigEndianUnicode;
        }

        preambleLength = 0;
        return Utf8NoBom;
    }
}
