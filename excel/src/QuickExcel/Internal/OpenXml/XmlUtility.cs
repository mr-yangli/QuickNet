using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

namespace QuickExcel.Internal.OpenXml;

/// <summary>Low-level helpers for hand-built OOXML: escaping and column-letter caching.</summary>
internal static class XmlUtility
{
    /// <summary>Everything that must take the slow path in element text: the three entities plus
    /// the control characters XML 1.0 forbids (0x00-0x08, 0x0B, 0x0C, 0x0E-0x1F; \t \n \r are the
    /// three legal ones). The fast path used to probe only &amp; &lt; &gt;, so a value like
    /// "\x01abc" returned untouched and produced an invalid part — the loop below that drops
    /// them was unreachable for such values. <see cref="AttributeDirtyChars"/> must keep this list.</summary>
    private static readonly SearchValues<char> TextDirtyChars = SearchValues.Create(
        '&', '<', '>',
        '\u0000', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007', '\u0008',
        '\u000B', '\u000C', '\u000E', '\u000F', '\u0010', '\u0011', '\u0012', '\u0013', '\u0014',
        '\u0015', '\u0016', '\u0017', '\u0018', '\u0019', '\u001A', '\u001B', '\u001C', '\u001D',
        '\u001E', '\u001F');

    /// <summary>Attribute values are double-quoted here (<c name="…"/>) so the fast path must also
    /// stop on <c>"</c> — that character is fine in element text (<see cref="TextDirtyChars"/>)
    /// and would emit a broken attribute if left as-is.</summary>
    private static readonly SearchValues<char> AttributeDirtyChars = SearchValues.Create(
        '&', '<', '>', '"',
        '\u0000', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007', '\u0008',
        '\u000B', '\u000C', '\u000E', '\u000F', '\u0010', '\u0011', '\u0012', '\u0013', '\u0014',
        '\u0015', '\u0016', '\u0017', '\u0018', '\u0019', '\u001A', '\u001B', '\u001C', '\u001D',
        '\u001E', '\u001F');

    /// <summary>Escapes text content for an XML element (does not touch quotes since element text
    /// needs none). Attribute values must go through <see cref="EscapeAttribute"/> instead.</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!text.AsSpan().ContainsAny(TextDirtyChars)) return text;

        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default:
                    // Control chars are illegal in XML 1.0; drop them to keep the package valid.
                    if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') break;
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Escapes text for a double-quoted XML attribute value: element escaping plus
    /// &amp;quot; on <c>"</c>. <see cref="Escape"/> leaves quotes alone and therefore breaks
    /// <c>&lt;sheet name="…"/&gt;</c> when a sheet name carries one.</summary>
    public static string EscapeAttribute(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!text.AsSpan().ContainsAny(AttributeDirtyChars)) return text;

        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') break;
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static readonly ConcurrentDictionary<int, string> ColumnCache = new();

    /// <summary>Cached zero-based column index -&gt; A1 letters.</summary>
    public static string ColumnLetter(int index) =>
        ColumnCache.GetOrAdd(index, CellReference.GetColumnName);
}
