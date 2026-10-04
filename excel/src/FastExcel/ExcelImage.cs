using System.Buffers;
using System.Buffers.Binary;

namespace FastExcel;

/// <summary>
/// An image to embed in (or read back from) a worksheet cell. Wraps raw bytes plus the detected
/// format and pixel dimensions so the writer can build a correctly sized drawing anchor.
/// </summary>
public sealed class ExcelImage : IEquatable<ExcelImage>
{
    public ReadOnlyMemory<byte> Data { get; }
    public ExcelImageFormat Format { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }

    public ExcelImage(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0)
            throw new ArgumentException("Image data is empty.", nameof(data));
        Data = data;
        Format = DetectFormat(data.Span);
        (PixelWidth, PixelHeight) = Measure(data.Span, Format);
    }

    public ExcelImage(byte[] data) : this(new ReadOnlyMemory<byte>(data)) { }

    public static ExcelImage FromFile(string path) =>
        new(new ReadOnlyMemory<byte>(File.ReadAllBytes(path)));

    public static async Task<ExcelImage> FromFileAsync(string path, CancellationToken ct = default) =>
        new(new ReadOnlyMemory<byte>(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)));

    public static ExcelImage FromStream(Stream stream)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> seg) && seg.Offset == 0)
            return new ExcelImage(new ReadOnlyMemory<byte>(seg.Array!, 0, (int)ms.Length));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return new ExcelImage(new ReadOnlyMemory<byte>(copy.ToArray()));
    }

    public static async Task<ExcelImage> FromStreamAsync(Stream stream, CancellationToken ct = default)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct).ConfigureAwait(false);
        return new ExcelImage(new ReadOnlyMemory<byte>(copy.ToArray()));
    }

    public static implicit operator ExcelImage(byte[] data) => new(data);

    /// <summary>File extension (without dot) Excel expects for this image's media part.</summary>
    public string Extension => Format switch
    {
        ExcelImageFormat.Png => "png",
        ExcelImageFormat.Jpeg => "jpeg",
        ExcelImageFormat.Gif => "gif",
        ExcelImageFormat.Bmp => "bmp",
        ExcelImageFormat.Emf => "emf",
        ExcelImageFormat.Wmf => "wmf",
        ExcelImageFormat.Tiff => "tiff",
        _ => "bin"
    };

    /// <summary>Content type written into [Content_Types].xml.</summary>
    public string ContentType => Format switch
    {
        ExcelImageFormat.Png => "image/png",
        ExcelImageFormat.Jpeg => "image/jpeg",
        ExcelImageFormat.Gif => "image/gif",
        ExcelImageFormat.Bmp => "image/bmp",
        ExcelImageFormat.Emf => "image/x-emf",
        ExcelImageFormat.Wmf => "image/x-wmf",
        ExcelImageFormat.Tiff => "image/tiff",
        _ => "application/octet-stream"
    };

    internal int ContentHash
    {
        get
        {
            // Cheap dedup key; collisions fall back to a full compare in the media collector.
            var span = Data.Span;
            int hash = 17;
            hash = hash * 31 + span.Length;
            int sample = Math.Min(span.Length, 64);
            for (int i = 0; i < sample; i++) hash = hash * 31 + span[i];
            hash = hash * 31 + span[^1];
            return hash;
        }
    }

    private static ExcelImageFormat DetectFormat(ReadOnlySpan<byte> s)
    {
        if (s.Length >= 8 && s[0] == 0x89 && s[1] == 'P' && s[2] == 'N' && s[3] == 'G')
            return ExcelImageFormat.Png;
        if (s.Length >= 3 && s[0] == 0xFF && s[1] == 0xD8 && s[2] == 0xFF)
            return ExcelImageFormat.Jpeg;
        if (s.Length >= 6 && s[0] == 'G' && s[1] == 'I' && s[2] == 'F' && s[3] == '8')
            return ExcelImageFormat.Gif;
        if (s.Length >= 2 && s[0] == 'B' && s[1] == 'M')
            return ExcelImageFormat.Bmp;
        if (s.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(s) == 0x00000001)
            return ExcelImageFormat.Emf;
        if (s.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)) == 0x9AC6CDD7)
            return ExcelImageFormat.Wmf;
        if (s.Length >= 4 && ((s[0] == 'I' && s[1] == 'I' && s[2] == 42) || (s[0] == 'M' && s[1] == 'M' && s[3] == 42)))
            return ExcelImageFormat.Tiff;
        return ExcelImageFormat.Unknown;
    }

    private static (int w, int h) Measure(ReadOnlySpan<byte> s, ExcelImageFormat fmt)
    {
        try
        {
            switch (fmt)
            {
                case ExcelImageFormat.Png:
                    // IHDR follows the 8-byte signature + 4 length + 4 "IHDR"
                    if (s.Length >= 24)
                        return (BinaryPrimitives.ReadInt32BigEndian(s.Slice(16)), BinaryPrimitives.ReadInt32BigEndian(s.Slice(20)));
                    break;
                case ExcelImageFormat.Gif:
                    if (s.Length >= 10)
                        return (BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(6)), BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8)));
                    break;
                case ExcelImageFormat.Bmp:
                    if (s.Length >= 24)
                        return (BinaryPrimitives.ReadInt32LittleEndian(s.Slice(18)), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(s.Slice(22))));
                    break;
                case ExcelImageFormat.Jpeg:
                    return MeasureJpeg(s);
            }
        }
        catch
        {
            // fall through to 0 dims; caller can override via ExcelImageAttribute
        }
        return (0, 0);
    }

    private static (int w, int h) MeasureJpeg(ReadOnlySpan<byte> s)
    {
        // Walk the marker chain until an SOFn (0xC0..0xCF except C4/C8/CC) is found.
        int i = 2;
        while (i < s.Length - 9)
        {
            if (s[i] != 0xFF) { i++; continue; }
            byte marker = s[i + 1];
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                int h = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(i + 5));
                int w = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(i + 7));
                return (w, h);
            }
            int len = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(i + 2));
            i += 2 + len;
        }
        return (0, 0);
    }

    public bool Equals(ExcelImage? other) => other is not null && Data.Span.SequenceEqual(other.Data.Span);
    public override bool Equals(object? obj) => obj is ExcelImage img && Equals(img);
    public override int GetHashCode() => ContentHash;
}
