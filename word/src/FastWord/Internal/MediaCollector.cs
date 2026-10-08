namespace FastWord.Internal;

/// <summary>One embedded image part after de-duplication.</summary>
internal sealed record MediaPart(
    string PartName,
    string FileName,
    string Extension,
    string ContentType,
    byte[] Data,
    int ContentId);

/// <summary>
/// Accumulates images referenced by document, header and footer parts, de-duplicating identical bytes
/// so a signature pasted into three placeholders is stored once. Single-writer by design.
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
    public MediaPart Add(WordImage image)
    {
        int hash = image.ContentHash;
        byte[] bytes = image.Data.ToArray();
        if (_byContentHash.TryGetValue(hash, out var bucket))
        {
            foreach (var (part, existing) in bucket)
                if (existing.AsSpan().SequenceEqual(bytes.AsSpan()))
                    return part;
        }
        else
        {
            bucket = new List<(MediaPart, byte[])>();
            _byContentHash[hash] = bucket;
        }

        int id = _nextId++;
        string fileName = $"image{id}.{image.Extension}";
        var media = new MediaPart($"word/media/{fileName}", fileName, image.Extension, image.ContentType, bytes, hash);
        _parts.Add(media);
        bucket.Add((media, bytes));
        return media;
    }
}
