using System.IO.Compression;
using System.Text;

namespace QuickExcel.Internal.OpenXml;

/// <summary>A merged block; rows are 1-based sheet numbers, columns zero-based (as in <see cref="WorksheetReader"/>).</summary>
internal readonly record struct MergeRange(int Row0, int Col0, int Row1, int Col1);

/// <summary>
/// Collects the &lt;mergeCell ref="..."/&gt; ranges of a worksheet part. OOXML stores a merged block's value
/// only in its top-left cell, so readers need the geometry to rehydrate ("fill") that value into the rest
/// of the block. The scan inflates the entry in fixed chunks and byte-searches for the tag: a literal
/// "&lt;mergeCell" cannot occur in cell text or attributes because XML escaping turns the leading '&lt;'
/// into &amp;lt;. Cost is one inflate pass (~ms per MB) with no per-import large-object allocation.
/// </summary>
internal static class MergeCellScanner
{
    private const int ChunkSize = 1 << 16;
    private const int Overlap = 512; // a real <mergeCell .../> element is far shorter than this
    private const int MaxRanges = 1_000_000;

    /// <summary>Merge ranges sorted by top-left row, or null when the sheet has none.</summary>
    public static List<MergeRange>? Scan(ZipArchiveEntry entry)
    {
        List<MergeRange> ranges;
        using (var stream = entry.Open())
            ranges = ScanStream(stream);

        if (ranges.Count == 0) return null;
        ranges.Sort((a, b) => a.Row0 != b.Row0 ? a.Row0.CompareTo(b.Row0) : a.Col0.CompareTo(b.Col0));
        return ranges;
    }

    private static List<MergeRange> ScanStream(Stream stream)
    {
        var list = new List<MergeRange>();
        byte[] buf = new byte[ChunkSize + Overlap];
        int carry = 0;

        while (true)
        {
            if (carry >= buf.Length) carry = 0; // pathological unterminated tag: drop and resync
            int got = stream.Read(buf, carry, buf.Length - carry);
            bool eof = got == 0;
            int total = carry + got;
            int from = ProcessRegion(buf, total, final: eof, list);
            if (eof) break;

            if (from >= total) { carry = 0; continue; }
            Array.Copy(buf, from, buf, 0, total - from);
            carry = total - from;
            if (list.Count > MaxRanges) break;
        }
        return list;
    }

    /// <summary>
    /// Consumes every complete mergeCell element starting before the tail window (or anywhere, on the
    /// final pass) and returns the index the next round must start from: the tail window can always hold
    /// the beginning of an element split across chunks.
    /// </summary>
    private static int ProcessRegion(byte[] buf, int total, bool final, List<MergeRange> list)
    {
        ReadOnlySpan<byte> tag = "<mergeCell"u8;
        int tail = final ? total : Math.Max(0, total - Overlap);
        int from = tail;
        int scan = 0;
        while (scan < tail)
        {
            int found = buf.AsSpan(scan, total - scan).IndexOf(tag);
            if (found < 0) break;
            int start = scan + found;
            if (start >= tail) break; // defer: re-seen next round inside the carried window
            int at = start + tag.Length;
            if (at >= total) break;
            byte after = buf[at];
            if (after is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                scan = at; // <mergeCells> container or degenerate form: not an element with a ref
                continue;
            }
            int end = Array.IndexOf(buf, (byte)'>', at, total - at);
            if (end < 0)
            {
                if (final) break;
                from = start; // unusually long element crossed the window: carry from its start
                break;
            }
            string? reference = ExtractRef(buf.AsSpan(at, end - at));
            if (TryParseRef(reference, out var range)) list.Add(range);
            scan = end + 1;
        }
        return from;
    }

    private static string? ExtractRef(ReadOnlySpan<byte> attributes)
    {
        int i = attributes.IndexOf("ref=\""u8);
        if (i < 0) return null;
        int start = i + 5;
        int quote = attributes.Slice(start).IndexOf((byte)'"');
        if (quote < 0) return null;
        return Encoding.ASCII.GetString(attributes.Slice(start, quote));
    }

    internal static bool TryParseRef(string? reference, out MergeRange range)
    {
        range = default;
        if (reference is null or { Length: 0 }) return false;
        int colon = reference.IndexOf(':');
        string left = colon < 0 ? reference : reference[..colon];
        string? right = colon < 0 ? null : reference[(colon + 1)..];

        // Full-column refs ("A:A") carry no row digits: treat them as spanning every sheet row.
        if (!TryParseCorner(left, out int r0, out int c0, rowIfMissing: 1, colIfMissing: 0)) return false;
        int r1 = r0, c1 = c0;
        if (right is not null &&
            !TryParseCorner(right, out r1, out c1, rowIfMissing: 1_048_576, colIfMissing: 16_383))
            return false;
        if (r1 < r0) (r0, r1) = (r1, r0);
        if (c1 < c0) (c0, c1) = (c1, c0);
        if (r0 == r1 && c0 == c1) return false; // single cell: nothing to rehydrate
        if (c1 - c0 > 1024) return false;       // refuse horizontal mega-merges (fill blow-up guard)
        range = new MergeRange(r0, c0, r1, c1);
        return true;
    }

    private static bool TryParseCorner(string s, out int row, out int col, int rowIfMissing, int colIfMissing)
    {
        row = 0; col = 0;
        bool hasRow = false, hasCol = false;
        foreach (char ch in s)
        {
            if (ch == '$') continue;
            if (ch is >= '0' and <= '9') { row = row * 10 + (ch - '0'); hasRow = true; }
            else if (ch is >= 'A' and <= 'Z') { col = col * 26 + (ch - 'A' + 1); hasCol = true; }
            else if (ch is >= 'a' and <= 'z') { col = col * 26 + (ch - 'a' + 1); hasCol = true; }
            else { row = col = 0; return false; }
        }
        if (hasCol) col -= 1; else col = colIfMissing;
        if (!hasRow) row = rowIfMissing;
        return row >= 1 && col >= 0;
    }
}
