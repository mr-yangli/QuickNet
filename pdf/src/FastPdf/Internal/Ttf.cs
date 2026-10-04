using System.Buffers.Binary;
using System.Globalization;

namespace FastPdf.Internal;

/// <summary>
/// Minimal TrueType/OT-SFNT reader that extracts what an embedded CID (Identity-H) PDF font needs:
/// unitsPerEm, layout bbox, hmtx advance widths, a code point -> glyph id map (cmap format 4 and 12),
/// and the PostScript name. The whole font file is handed to the PDF as FontFile2 (no subsetting) — the
/// simplest thing that renders Chinese identically in every viewer; size is traded for guaranteed fidelity.
/// </summary>
internal sealed class Ttf
{
    private readonly byte[] _file;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new();

    public int UnitsPerEm { get; }
    public int Ascender { get; }
    public int Descender { get; }
    public int BboxXMin { get; }
    public int BboxYMin { get; }
    public int BboxXMax { get; }
    public int BboxYMax { get; }
    public bool FixedPitch { get; }
    public string PostScriptName { get; }

    // advance width (in font units) per glyph id, from hmtx
    private readonly int[] _advance;
    // code point -> glyph id, from the preferred cmap subtable
    private readonly Dictionary<int, int> _cmap = new();

    public byte[] FontData => _file;

    private Ttf(byte[] file, Dictionary<string, (int Offset, int Length)> tables,
               int upm, int asc, int desc, int xMin, int yMin, int xMax, int yMax, bool fixedPitch,
               string psName, int[] advance)
    {
        _file = file; _tables = tables;
        UnitsPerEm = upm; Ascender = asc; Descender = desc;
        BboxXMin = xMin; BboxYMin = yMin; BboxXMax = xMax; BboxYMax = yMax;
        FixedPitch = fixedPitch; PostScriptName = psName; _advance = advance;
    }

    public static Ttf Load(byte[] file)
    {
        // msyh.ttc / simsun.ttc are collections: extract font #0 as a standalone sfnt first.
        if (BinaryPrimitives.ReadUInt32BigEndian(file) == 0x74746366 /* ttcf */)
            file = ExtractFontFromTtc(file, 0);

        var span = file.AsSpan();
        var tables = ReadDirectory(span);
        if (!tables.TryGetValue("head", out var head) || !tables.TryGetValue("hhea", out var hhea)
            || !tables.TryGetValue("maxp", out var maxp) || !tables.ContainsKey("hmtx")
            || !tables.ContainsKey("cmap") || !tables.ContainsKey("glyf"))
            throw new InvalidDataException("Not a usable TrueType (glyf) font — CFF/OTF cannot go into /FontFile2.");

        int upm = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(head.Offset + 18));
        int xMin = BinaryPrimitives.ReadInt16BigEndian(span.Slice(head.Offset + 36));
        int yMin = BinaryPrimitives.ReadInt16BigEndian(span.Slice(head.Offset + 38));
        int xMax = BinaryPrimitives.ReadInt16BigEndian(span.Slice(head.Offset + 40));
        int yMax = BinaryPrimitives.ReadInt16BigEndian(span.Slice(head.Offset + 42));
        int macStyle = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(head.Offset + 44));

        // Classic hhea v1.0 layout (verified against fontTools on msyh/simhei):
        // version@0(4) ascender@4 descender@6 lineGap@8 advanceWidthMax@10 ... numberOfHMetrics@34
        int asc = BinaryPrimitives.ReadInt16BigEndian(span.Slice(hhea.Offset + 4));
        int desc = BinaryPrimitives.ReadInt16BigEndian(span.Slice(hhea.Offset + 6));
        int numHMetrics = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(hhea.Offset + 34));
        int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(maxp.Offset + 4));

        int[] advance = ReadHmtx(span, tables["hmtx"], numHMetrics, numGlyphs);
        string psName = ReadPostScriptName(span, tables);
        var ttf = new Ttf(file, tables, upm, asc, desc, xMin, yMin, xMax, yMax, (macStyle & 1) != 0, psName, advance);
        ttf.BuildCmap(span);
        return ttf;
    }

    /// <summary>
    /// Rebuilds one font from a 'ttcf' collection as a standalone sfnt: copy the chosen font's table
    /// directory, then each table's bytes 4-byte aligned. The result is a valid .ttf stream that PDF
    /// viewers (and our own directory parser) can consume unchanged.
    /// </summary>
    private static byte[] ExtractFontFromTtc(byte[] file, int fontIndex)
    {
        int numFonts = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(8));
        if ((uint)fontIndex >= (uint)numFonts)
            throw new InvalidDataException($"Font index {fontIndex} out of range (collection has {numFonts} fonts).");
        int fontOff = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(12 + fontIndex * 4));

        var src = file.AsSpan(fontOff);
        var whole = file.AsSpan(); // TTC table offsets are absolute from the file start
        ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(4));

        // size = 12-byte header + 16-byte records + padded table data
        int total = 12 + numTables * 16;
        var sizes = new int[numTables];
        for (int i = 0; i < numTables; i++)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(src.Slice(12 + i * 16 + 12));
            sizes[i] = (len + 3) & ~3;
            total += sizes[i];
        }

        var outBuf = new byte[total];
        var dst = outBuf.AsSpan();
        src.Slice(0, 4).CopyTo(dst); // sfnt version tag
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(4), numTables);
        int entrySelector = (int)Math.Floor(Math.Log2(numTables));
        int searchRange = 1 << (entrySelector + 4);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(10), (ushort)(numTables * 16 - searchRange));

        int dataOff = 12 + numTables * 16;
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            // copy tag + length; rewrite offset, leave checksum 0 (viewers don't verify FontFile2)
            src.Slice(rec, 4).CopyTo(dst.Slice(rec));
            // checksum field (rec+4..+7): copy as-is
            src.Slice(rec + 4, 4).CopyTo(dst.Slice(rec + 4));
            int toff = BinaryPrimitives.ReadInt32BigEndian(src.Slice(rec + 8));
            int len = BinaryPrimitives.ReadInt32BigEndian(src.Slice(rec + 12));
            BinaryPrimitives.WriteInt32BigEndian(dst.Slice(rec + 8), dataOff);
            BinaryPrimitives.WriteInt32BigEndian(dst.Slice(rec + 12), len);
            whole.Slice(toff, len).CopyTo(dst.Slice(dataOff));
            dataOff += sizes[i];
        }
        return outBuf;
    }

    private static Dictionary<string, (int Offset, int Length)> ReadDirectory(ReadOnlySpan<byte> s)
    {
        uint tag = BinaryPrimitives.ReadUInt32BigEndian(s);
        if (tag == 0x74746366 /* ttcf */) throw new InvalidDataException("TrueType collections are not supported; provide a single .ttf.");
        if (tag != 0x00010000 && tag != 0x4F505446 /* OTTO */ && tag != 0x74727565)
            throw new InvalidDataException("Unrecognised sfnt version.");

        int numTables = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(4));
        var map = new Dictionary<string, (int Offset, int Length)>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            string name = System.Text.Encoding.ASCII.GetString(s.Slice(rec, 4));
            int off = BinaryPrimitives.ReadInt32BigEndian(s.Slice(rec + 8));
            int len = BinaryPrimitives.ReadInt32BigEndian(s.Slice(rec + 12));
            map[name] = (off, len);
        }
        return map;
    }

    private static int[] ReadHmtx(ReadOnlySpan<byte> s, (int Offset, int Length) hmtx, int numHMetrics, int numGlyphs)
    {
        int[] adv = new int[Math.Max(numGlyphs, numHMetrics)];
        int last = 0;
        for (int g = 0; g < numHMetrics; g++)
        {
            last = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(hmtx.Offset + g * 4));
            adv[g] = last;
        }
        for (int g = numHMetrics; g < adv.Length; g++) adv[g] = last; // trailing glyphs reuse the last advance
        return adv;
    }

    private static string ReadPostScriptName(ReadOnlySpan<byte> s, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue("name", out var name)) return "FastPdfFont";
        int count = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(name.Offset + 2));
        int strStart = name.Offset + BinaryPrimitives.ReadUInt16BigEndian(s.Slice(name.Offset + 4));
        for (int i = 0; i < count; i++)
        {
            int rec = name.Offset + 6 + i * 12;
            int nameId = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(rec + 6));
            int len = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(rec + 8));
            int off = strStart + BinaryPrimitives.ReadUInt16BigEndian(s.Slice(rec + 10));
            if (nameId == 29 || nameId == 6) // 29 = PostScript name; 6 = fallback
            {
                ushort plat = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(rec));
                var raw = s.Slice(off, len);
                if (plat == 3) // UTF-16BE
                {
                    var chars = new char[raw.Length / 2];
                    for (int c = 0; c < chars.Length; c++) chars[c] = (char)BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(c * 2));
                    return new string(chars);
                }
                return System.Text.Encoding.ASCII.GetString(raw);
            }
        }
        return "FastPdfFont";
    }

    private void BuildCmap(ReadOnlySpan<byte> s)
    {
        var cmap = _tables["cmap"];
        int num = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(cmap.Offset + 2));
        int chosenFmt = -1, chosenOff = 0; int win = 99;
        for (int i = 0; i < num; i++)
        {
            int rec = cmap.Offset + 4 + i * 8; // version(2) + numTables(2), then records
            int plat = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(rec));
            int enc = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(rec + 2));
            int off = cmap.Offset + BinaryPrimitives.ReadInt32BigEndian(s.Slice(rec + 4));
            int fmt = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(off));
            int rank = (plat, enc, fmt) switch
            {
                (3, 10, 12) => 0, // Windows Unicode Full
                (0, 4 or 6, 12) => 0, // Unicode full
                (3, 1, 4) => 1,    // Windows BMP
                (0, _, 4) => 2,    // Unicode BMP
                _ => 9
            };
            if (rank < win) { win = rank; chosenFmt = fmt; chosenOff = off; }
        }
        if (win == 9) throw new InvalidDataException("Font has no usable cmap (need format 4 or 12).");
        if (chosenFmt == 4) ReadCmap4(s, chosenOff); else ReadCmap12(s, chosenOff);
    }

    private void ReadCmap4(ReadOnlySpan<byte> s, int off)
    {
        int segCountX2 = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(off + 6));
        int seg = segCountX2 / 2;
        int endCode = off + 14, startCode = endCode + segCountX2 + 2, delta = startCode + segCountX2, range = delta + segCountX2;
        for (int i = 0; i < seg; i++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(endCode + i * 2));
            int start = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(startCode + i * 2));
            if (start == 0xFFFF) continue;
            short d = BinaryPrimitives.ReadInt16BigEndian(s.Slice(delta + i * 2));
            ushort ro = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(range + i * 2));
            for (int c = start; c <= end; c++)
            {
                int gid;
                if (ro == 0) gid = (c + d) & 0xFFFF;
                else
                {
                    int gi = range + i * 2 + ro + (c - start) * 2;
                    ushort raw = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(gi));
                    gid = raw == 0 ? 0 : (raw + d) & 0xFFFF;
                }
                if (gid != 0 && !_cmap.ContainsKey(c)) _cmap[c] = gid;
            }
        }
    }

    private void ReadCmap12(ReadOnlySpan<byte> s, int off)
    {
        int groups = BinaryPrimitives.ReadInt32BigEndian(s.Slice(off + 12));
        for (int i = 0; i < groups; i++)
        {
            int g = off + 16 + i * 12;
            int start = BinaryPrimitives.ReadInt32BigEndian(s.Slice(g));
            int end = BinaryPrimitives.ReadInt32BigEndian(s.Slice(g + 4));
            int gidStart = BinaryPrimitives.ReadInt32BigEndian(s.Slice(g + 8));
            for (int c = start; c <= end; c++)
                if (!_cmap.ContainsKey(c)) _cmap[c] = gidStart + (c - start);
        }
    }

    /// <summary>Advance width in PDF units (thousandths of an em), matching the /DW=1000 coordinate scale.</summary>
    public int WidthThousand(int glyphId)
    {
        if ((uint)glyphId >= (uint)_advance.Length) return 0;
        return (int)Math.Round(_advance[glyphId] * 1000.0 / UnitsPerEm);
    }

    public int GlyphId(int codePoint) => _cmap.TryGetValue(codePoint, out int g) ? g : 0;

    public bool TryGlyphId(int codePoint, out int glyphId) => _cmap.TryGetValue(codePoint, out glyphId);
}
