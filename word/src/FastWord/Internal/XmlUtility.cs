using System.Text;

namespace FastWord.Internal;

/// <summary>Escaping for hand-built WordprocessingML.</summary>
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
}
