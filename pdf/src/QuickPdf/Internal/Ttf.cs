using System.Buffers.Binary;
using System.Globalization;

namespace QuickPdf.Internal;

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
        // 畸形字体防御（v0.2 第二笔）：下面每一步的读数都以"文件装载进来的字节"为边界，
        // 越界一律 InvalidDataException（调用方里 TryLoad 据此换下一个候选），而不是
        // ArgumentOutOfRangeException / OverflowException 那种"责怪调用语句"的误导形状。
        if (file is not { Length: > 12 })
            throw new InvalidDataException("Font file is empty or too short to be a sfnt.");

        // msyh.ttc / simsun.ttc are collections: extract font #0 as a standalone sfnt first.
        if (BinaryPrimitives.ReadUInt32BigEndian(file) == 0x74746366 /* ttcf */)
            file = ExtractFontFromTtc(file, 0);

        var span = file.AsSpan();
        var tables = ReadDirectory(span);
        if (!tables.TryGetValue("head", out var head) || !tables.TryGetValue("hhea", out var hhea)
            || !tables.TryGetValue("maxp", out var maxp) || !tables.ContainsKey("hmtx")
            || !tables.ContainsKey("cmap") || !tables.ContainsKey("glyf"))
            throw new InvalidDataException("Not a usable TrueType (glyf) font — CFF/OTF cannot go into /FontFile2.");

        // 固定字段读之前先把每张要用的表声明出最小长度：偏移有效不代表字段在场。
        RequireLength(span, head, 46, "head");
        RequireLength(span, hhea, 36, "hhea");
        RequireLength(span, maxp, 6, "maxp");

        int upm = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(head.Offset + 18));
        if (upm <= 0)
            throw new InvalidDataException($"Malformed font: unitsPerEm is {upm}; every advance divides by it.");
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

    /// <summary>定点读的总闸（v0.2 第二笔）：表偏移/长度是文件直读的，"字段在场"这件事用长度明证一次，
    /// 之后那句 ReadXxx 才配说它拿的是表里的字段而不是文件外的垃圾。</summary>
    private static void RequireLength(ReadOnlySpan<byte> s, (int Offset, int Length) table, int min, string what)
    {
        if (table.Length < min)
            throw new InvalidDataException($"Malformed font: {what} table is {table.Length} bytes, needs >= {min}.");
    }

    /// <summary>带防御的定点读（v0.2 第二笔）：越界交 InvalidDataException 而不是越界异常的误导形状。
    /// 只在装载路径上走（每字形零次），替身换不来什么也不亏什么。</summary>
    private static ushort ReadU16(ReadOnlySpan<byte> s, int pos, string what)
    {
        if ((uint)pos > (uint)(s.Length - 2))
            throw new InvalidDataException($"Malformed font: {what} at byte {pos} reads past the end of {s.Length} bytes.");
        return BinaryPrimitives.ReadUInt16BigEndian(s.Slice(pos));
    }

    private static uint ReadU32(ReadOnlySpan<byte> s, int pos, string what)
    {
        if ((uint)pos > (uint)(s.Length - 4))
            throw new InvalidDataException($"Malformed font: {what} at byte {pos} reads past the end of {s.Length} bytes.");
        return BinaryPrimitives.ReadUInt32BigEndian(s.Slice(pos));
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
        // 畸形防御（v0.2 第二笔）：目录长度、字体偏移、逐表 (offset, length) 全部在文件边界内明证过才动手。
        // 长算写死：numFonts * 4 按 int 算在 numFonts 接近 2^30 时回卷成小数，那道判就放行伪造目录了。
        if ((long)file.Length < 12L + (long)numFonts * 4)
            throw new InvalidDataException($"Malformed font: ttcf directory claims {numFonts} fonts, file ends before all offsets.");
        int fontOff = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(12 + fontIndex * 4));
        if (fontOff < 0 || (long)fontOff + 12 > file.Length)
            throw new InvalidDataException($"Malformed font: font offset {fontOff} lies outside the file.");

        var src = file.AsSpan(fontOff);
        var whole = file.AsSpan(); // TTC table offsets are absolute from the file start
        ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(4));
        if ((long)fontOff + 12 + numTables * 16 > file.Length)
            throw new InvalidDataException($"Malformed font: table directory of {numTables} tables runs past the end of the file.");

        // size = 12-byte header + 16-byte records + padded table data
        int total = 12 + numTables * 16;
        var sizes = new int[numTables];
        for (int i = 0; i < numTables; i++)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(src.Slice(12 + i * 16 + 12));
            int toff = BinaryPrimitives.ReadInt32BigEndian(src.Slice(12 + i * 16 + 8));
            if (len < 0 || (long)toff + len > file.Length)
                throw new InvalidDataException($"Malformed font: table #{i} (offset {toff}, length {len}) lies outside the collection.");
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
        // 畸形防御（v0.2 第二笔）：目录先得放得下，逐表 (offset, length) 得在文件边界内——
        // 这里验一次，之后所有消费点（head/hhea/maxp/hmtx/cmap/name/glyf）的定点读都成立在它上面。
        if (12 + numTables * 16 > s.Length)
            throw new InvalidDataException($"Malformed font: table directory of {numTables} tables runs past the {s.Length}-byte file.");
        var map = new Dictionary<string, (int Offset, int Length)>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            string name = System.Text.Encoding.ASCII.GetString(s.Slice(rec, 4));
            int off = BinaryPrimitives.ReadInt32BigEndian(s.Slice(rec + 8));
            int len = BinaryPrimitives.ReadInt32BigEndian(s.Slice(rec + 12));
            if (off < 0 || len < 0 || (long)off + len > s.Length)
                throw new InvalidDataException($"Malformed font: table '{name}' (offset {off}, length {len}) lies outside the file.");
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
            last = ReadU16(s, hmtx.Offset + g * 4, $"hmtx advance #{g}"); // 畸形防御（v0.2 第二笔）：逐项过界判
            adv[g] = last;
        }
        for (int g = numHMetrics; g < adv.Length; g++) adv[g] = last; // trailing glyphs reuse the last advance
        return adv;
    }

    private static string ReadPostScriptName(ReadOnlySpan<byte> s, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue("name", out var name)) return "QuickPdfFont";
        int count = ReadU16(s, name.Offset + 2, "name.namerecordCount");
        int strStart = name.Offset + ReadU16(s, name.Offset + 4, "name.stringOffset");
        for (int i = 0; i < count; i++)
        {
            int rec = name.Offset + 6 + i * 12;
            int nameId = ReadU16(s, rec + 6, "name.nameId");
            int len = ReadU16(s, rec + 8, "name.length");
            int off = strStart + ReadU16(s, rec + 10, "name.offset");
            if (nameId == 29 || nameId == 6) // 29 = PostScript name; 6 = fallback
            {
                if (off < 0 || len < 0 || (long)off + len > s.Length)
                    throw new InvalidDataException($"Malformed font: name record #{i} (offset {off}, length {len}) lies outside the file.");
                ushort plat = ReadU16(s, rec, "name.platform");
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
        return "QuickPdfFont";
    }

    private void BuildCmap(ReadOnlySpan<byte> s)
    {
        var cmap = _tables["cmap"];
        int num = ReadU16(s, cmap.Offset + 2, "cmap.numTables");
        int chosenFmt = -1, chosenOff = 0; int win = 99;
        for (int i = 0; i < num; i++)
        {
            int rec = cmap.Offset + 4 + i * 8; // version(2) + numTables(2), then records
            int plat = ReadU16(s, rec, $"cmap record #{i} platform");
            int enc = ReadU16(s, rec + 2, $"cmap record #{i} encoding");
            int off = cmap.Offset + (int)ReadU32(s, rec + 4, $"cmap record #{i} offset");
            int fmt = ReadU16(s, off, $"cmap subtable #{i} format");
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
        int segCountX2 = ReadU16(s, off + 6, "cmap4.segCountX2");
        int seg = segCountX2 / 2;
        // 畸形防御（v0.2 第二笔）：一段记录名义上要 8 字节（end/start/delta/rangeOffset 四颗 u16），
        // 段数以"文件里还剩多少字节"收口——伪造的 segCount 让内层循环空转到赔不起。
        int room = (s.Length - off) / 8;
        if (seg > room) seg = room;
        int endCode = off + 14, startCode = endCode + segCountX2 + 2, delta = startCode + segCountX2, range = delta + segCountX2;
        for (int i = 0; i < seg; i++)
        {
            int end = ReadU16(s, endCode + i * 2, $"cmap4 endCode #{i}");
            int start = ReadU16(s, startCode + i * 2, $"cmap4 startCode #{i}");
            if (start == 0xFFFF || end < start) continue;
            short d = (short)ReadU16(s, delta + i * 2, $"cmap4 idDelta #{i}");
            ushort ro = ReadU16(s, range + i * 2, $"cmap4 idRangeOffset #{i}");
            for (int c = start; c <= end; c++)
            {
                int gid;
                if (ro == 0) gid = (c + d) & 0xFFFF;
                else
                {
                    int gi = range + i * 2 + ro + (c - start) * 2;
                    if ((uint)gi > (uint)(s.Length - 2)) break; // 指针飞出文件的段照到为止，别把越界当成功
                    ushort raw = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(gi));
                    gid = raw == 0 ? 0 : (raw + d) & 0xFFFF;
                }
                if (gid != 0 && !_cmap.ContainsKey(c)) _cmap[c] = gid;
            }
        }
    }

    private void ReadCmap12(ReadOnlySpan<byte> s, int off)
    {
        // 畸形防御（v0.2 第二笔）三半：① groups 以"文件在表头之后还剩多少字节"收口（每组建义上占 12 字节），
        // 伪造大数转不出物料经济；② start/end 以 Unicode 全宇宙收口（0x10FFFF），start 出界的组与
        // end < start 的组直接跳过——单组数据造不出几十亿次循环；③ gidStart + 步长按 16 位字形号口径裁断，
        // 溢出（负数或 > 0xFFFF）的那一步不写表，写进 PDF 的 gid 永远站得住。
        int groups = (int)ReadU32(s, off + 12, "cmap12.numGroups");
        int room = (int)Math.Min((long)(s.Length - off - 16) / 12, groups);
        for (int i = 0; i < room; i++)
        {
            int g = off + 16 + i * 12;
            uint start = ReadU32(s, g, $"cmap12 group #{i} start");
            uint end = ReadU32(s, g + 4, $"cmap12 group #{i} end");
            uint gidStart = ReadU32(s, g + 8, $"cmap12 group #{i} gidStart");
            if (start > EndOfUnicode || end > EndOfUnicode || end < start) continue;
            for (uint c = start; c <= end; c++)
            {
                uint gid = gidStart + (c - start);
                if (gid > 0xFFFF) break; // 字形号溢出：组的尾巴已经在字形宇宙之外
                if (!_cmap.ContainsKey((int)c)) _cmap[(int)c] = (int)gid;
            }
        }
    }

    private const uint EndOfUnicode = 0x10FFFF;

    /// <summary>Advance width in PDF units (thousandths of an em), matching the /DW=1000 coordinate scale.</summary>
    public int WidthThousand(int glyphId)
    {
        if ((uint)glyphId >= (uint)_advance.Length) return 0;
        return (int)Math.Round(_advance[glyphId] * 1000.0 / UnitsPerEm);
    }

    public int GlyphId(int codePoint) => _cmap.TryGetValue(codePoint, out int g) ? g : 0;

    public bool TryGlyphId(int codePoint, out int glyphId) => _cmap.TryGetValue(codePoint, out glyphId);
}
