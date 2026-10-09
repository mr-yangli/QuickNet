using System.Collections.Concurrent;
using System.Text;

namespace QuickExcel.Internal.OpenXml;

/// <summary>Low-level helpers for hand-built OOXML: escaping and column-letter caching.</summary>
internal static class XmlUtility
{
    /// <summary>Escapes text content for an XML element (does not touch quotes since element text needs none).</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (text.AsSpan().IndexOfAny('&', '<', '>') < 0) return text;

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

    private static readonly ConcurrentDictionary<int, string> ColumnCache = new();

    /// <summary>Cached zero-based column index -&gt; A1 letters.</summary>
    public static string ColumnLetter(int index) =>
        ColumnCache.GetOrAdd(index, CellReference.GetColumnName);
}
