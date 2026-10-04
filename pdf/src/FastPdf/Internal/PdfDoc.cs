using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FastPdf.Internal;

/// <summary>
/// Builds a PDF 1.4 file from numbered indirect objects. Callers may <see cref="ReserveObject"/> an
/// index before its content is known (forward references, e.g. the catalog referenced by the
/// trailer), then fill it with SetObject. Object bodies exclude the "n 0 obj"/"endobj"
/// framing — <see cref="Build"/> adds it uniformly and writes the classic xref table + trailer.
/// </summary>
internal sealed class PdfDoc
{
    private readonly Dictionary<int, byte[]> _objects = new();
    private int _next = 1;

    /// <summary>Take the next free object number without writing a body yet.</summary>
    public int ReserveObject() => _next++;

    /// <summary>Claim a specific object number (for ids reserved up-front by the caller).</summary>
    public int ReserveExact(int id)
    {
        if (id >= _next) _next = id + 1;
        return id;
    }

    public void SetObject(int id, ReadOnlySpan<byte> body) => _objects[id] = body.ToArray();

    public void SetObject(int id, string body) => SetObject(id, Encoding.ASCII.GetBytes(body));

    /// <summary>
    /// Add a stream object (deflate-compressed when that actually saves bytes) and return its number.
    /// <paramref name="extraDict"/> is appended inside the stream dictionary, e.g. "/Type /XObject".
    /// </summary>
    public int AddStream(ReadOnlySpan<byte> data, string extraDict = "",
                         CompressionLevel level = CompressionLevel.SmallestSize)
    {
        int id = ReserveObject();
        SetStream(id, data, extraDict, level);
        return id;
    }

    /// <summary>Write stream content into an object id that was reserved earlier.</summary>
    public void SetStream(int id, ReadOnlySpan<byte> data, string extraDict = "",
                          CompressionLevel level = CompressionLevel.SmallestSize)
    {
        byte[] raw = data.ToArray();
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, level, leaveOpen: true))
                z.Write(raw);
            compressed = ms.ToArray();
        }
        bool flate = compressed.Length < raw.Length;
        byte[] payload = flate ? compressed : raw;

        var dict = new StringBuilder();
        dict.Append("<< /Length ").Append(payload.Length);
        if (flate) dict.Append(" /Filter /FlateDecode");
        if (extraDict.Length > 0) dict.Append(' ').Append(extraDict);
        dict.Append(" >>\nstream\n");

        var head = Encoding.ASCII.GetBytes(dict.ToString());
        var body = new byte[head.Length + payload.Length + "\nendstream\n".Length];
        head.CopyTo(body, 0);
        payload.CopyTo(body, head.Length);
        "\nendstream\n"u8.CopyTo(body.AsSpan(head.Length + payload.Length));
        _objects[id] = body;
    }

    public byte[] Build(int catalogId)
    {
        int max = _next - 1;
        var outMs = new MemoryStream();
        outMs.Write("%PDF-1.4\n"u8);
        outMs.Write("%\xE2\xE3\xCF\xD3\n"u8); // binary comment: file is not pure ASCII

        long[] offsets = new long[max + 2];
        for (int id = 1; id <= max; id++)
        {
            offsets[id] = outMs.Position;
            outMs.Write(Encoding.ASCII.GetBytes($"{id} 0 obj\n"));
            if (_objects.TryGetValue(id, out var body))
            {
                outMs.Write(body);
                if (body.Length == 0 || body[^1] != (byte)'\n') outMs.WriteByte((byte)'\n');
            }
            else outMs.Write("<< >>\n"u8);
            outMs.Write("endobj\n"u8);
        }

        long xrefOff = outMs.Position;
        var xref = new StringBuilder();
        xref.Append("xref\n0 ").Append(max + 1).Append('\n');
        xref.Append("0000000000 65535 f \n");
        for (int id = 1; id <= max; id++)
            xref.Append(offsets[id].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xref.Append("trailer\n<< /Size ").Append(max + 1)
            .Append(" /Root ").Append(catalogId).Append(" 0 R >>\nstartxref\n")
            .Append(xrefOff.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        outMs.Write(Encoding.ASCII.GetBytes(xref.ToString()));
        return outMs.ToArray();
    }
}
