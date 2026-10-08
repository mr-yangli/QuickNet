using System.Buffers.Binary;

namespace FastWord;

/// <summary>
/// An image to drop into a template placeholder: raw bytes plus the sniffed format and native pixel
/// size, so the writer can emit a correct inline drawing. Standalone twin of FastExcel's ExcelImage.
/// </summary>
public sealed class WordImage : IEquatable<WordImage>
{
    public ReadOnlyMemory<byte> Data { get; }
    public WordImageFormat Format { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }

    public WordImage(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0)
            throw new ArgumentException("Image data is empty.", nameof(data));
        Data = data;
        Format = DetectFormat(data.Span);
        (PixelWidth, PixelHeight) = Measure(data.Span, Format);
    }

    public WordImage(byte[] data) : this(new ReadOnlyMemory<byte>(data)) { }

    public static WordImage FromFile(string path) =>
        new(new ReadOnlyMemory<byte>(File.ReadAllBytes(path)));

    public static async Task<WordImage> FromFileAsync(string path, CancellationToken ct = default) =>
        new(new ReadOnlyMemory<byte>(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)));

    public static implicit operator WordImage(byte[] data) => new(data);

    /// <summary>File extension (without dot) of this image's media part.</summary>
    public string Extension => Format switch
    {
        WordImageFormat.Png => "png",
        WordImageFormat.Jpeg => "jpeg",
        WordImageFormat.Gif => "gif",
        WordImageFormat.Bmp => "bmp",
        _ => "bin"
    };

    /// <summary>Content type declared in [Content_Types].xml.</summary>
    public string ContentType => Format switch
    {
        WordImageFormat.Png => "image/png",
        WordImageFormat.Jpeg => "image/jpeg",
        WordImageFormat.Gif => "image/gif",
        WordImageFormat.Bmp => "image/bmp",
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
            return hash * 31 + span[^1];
        }
    }

    private static WordImageFormat DetectFormat(ReadOnlySpan<byte> s)
    {
        if (s.Length >= 8 && s[0] == 0x89 && s[1] == 'P' && s[2] == 'N' && s[3] == 'G')
            return WordImageFormat.Png;
        if (s.Length >= 3 && s[0] == 0xFF && s[1] == 0xD8 && s[2] == 0xFF)
            return WordImageFormat.Jpeg;
        if (s.Length >= 6 && s[0] == 'G' && s[1] == 'I' && s[2] == 'F' && s[3] == '8')
            return WordImageFormat.Gif;
        if (s.Length >= 2 && s[0] == 'B' && s[1] == 'M')
            return WordImageFormat.Bmp;
        return WordImageFormat.Unknown;
    }

    private static (int w, int h) Measure(ReadOnlySpan<byte> s, WordImageFormat fmt)
    {
        try
        {
            switch (fmt)
            {
                case WordImageFormat.Png:
                    if (s.Length >= 24)
                        return (BinaryPrimitives.ReadInt32BigEndian(s.Slice(16)),
                                BinaryPrimitives.ReadInt32BigEndian(s.Slice(20)));
                    break;
                case WordImageFormat.Gif:
                    if (s.Length >= 10)
                        return (BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(6)),
                                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8)));
                    break;
                case WordImageFormat.Bmp:
                    if (s.Length >= 24)
                        return (BinaryPrimitives.ReadInt32LittleEndian(s.Slice(18)),
                                Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(s.Slice(22))));
                    break;
                case WordImageFormat.Jpeg:
                    return MeasureJpeg(s);
            }
        }
        catch
        {
            // fall through to 0 dims; the caller can state an explicit render size
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
                return (BinaryPrimitives.ReadUInt16BigEndian(s.Slice(i + 7)),
                        BinaryPrimitives.ReadUInt16BigEndian(s.Slice(i + 5)));
            int len = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(i + 2));
            if (len < 2) break;
            i += 2 + len;
        }
        return (0, 0);
    }

    public bool Equals(WordImage? other) => other is not null && Data.Span.SequenceEqual(other.Data.Span);
    public override bool Equals(object? obj) => obj is WordImage img && Equals(img);
    public override int GetHashCode() => ContentHash;
}
