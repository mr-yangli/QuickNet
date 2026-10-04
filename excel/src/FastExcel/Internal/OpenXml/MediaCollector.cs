namespace FastExcel.Internal.OpenXml;

/// <summary>One embedded image part after de-duplication.</summary>
internal sealed record MediaPart(string PartName, string Extension, string ContentType, byte[] Data, int ContentId);

/// <summary>
/// Accumulates images referenced by worksheets, de-duplicating identical bytes so a logo reused on
/// every row is stored once. Thread-unsafe by design: export is single-writer.
/// </summary>
internal sealed class MediaCollector
{
    private readonly Dictionary<int, List<(MediaPart Part, byte[] Bytes)>> _byContentHash = new();
    private readonly List<MediaPart> _parts = new();
    private int _nextId;

    /// <param name="startId">First media index to name; template rendering passes existing-count+1.</param>
    public MediaCollector(int startId = 1) => _nextId = startId;

    public IReadOnlyList<MediaPart> Parts => _parts;

    /// <summary>Returns the (possibly shared) media part for an image, creating it on first sight.</summary>
    public MediaPart Add(in ExcelImage image)
    {
        int hash = image.ContentHash;
        var candidateBytes = image.Data.ToArray();
        if (_byContentHash.TryGetValue(hash, out var bucket))
        {
            foreach (var (part, bytes) in bucket)
                if (bytes.AsSpan().SequenceEqual(candidateBytes.AsSpan()))
                    return part;
        }
        else
        {
            bucket = new List<(MediaPart, byte[])>();
            _byContentHash[hash] = bucket;
        }

        int id = _nextId++;
        var ext = image.Extension;
        var partName = $"xl/media/image{id}.{ext}";
        var media = new MediaPart(partName, ext, image.ContentType, candidateBytes, hash);
        _parts.Add(media);
        bucket.Add((media, candidateBytes));
        return media;
    }
}
