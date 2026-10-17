using System.Buffers;
using System.Text;

namespace QuickWord.Internal;

/// <summary>Escaping for hand-built WordprocessingML.</summary>
internal static class XmlUtility
{
    /// <summary>The slow-path trigger set: the three entities plus the control characters XML 1.0
    /// forbids (0x00-0x08, 0x0B, 0x0C, 0x0E-0x1F; \t \n \r are the three legal ones). The fast path
    /// used to probe only &amp; &lt; &gt;, so a value like "\x01abc" returned untouched and reached
    /// &lt;w:t&gt; raw, making Word call the whole package corrupt — the drop-them loop below was
    /// unreachable for exactly those values.</summary>
    private static readonly SearchValues<char> DirtyChars = SearchValues.Create(
        '&', '<', '>',
        '\u0000', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007', '\u0008',
        '\u000B', '\u000C', '\u000E', '\u000F', '\u0010', '\u0011', '\u0012', '\u0013', '\u0014',
        '\u0015', '\u0016', '\u0017', '\u0018', '\u0019', '\u001A', '\u001B', '\u001C', '\u001D',
        '\u001E', '\u001F');

    /// <summary>Escapes text content for an XML element (does not touch quotes since element text needs none).</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!text.AsSpan().ContainsAny(DirtyChars)) return text;

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
}
