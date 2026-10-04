using FastWord.Internal;

namespace FastWord;

/// <summary>
/// Entry point for FastWord: streams any tabular data source (POCO / dictionary / ExpandoObject /
/// DataTable / <see cref="WordSheet"/> sequences) into a .docx table, using FastWord's own
/// column attributes (<c>[WordColumn]</c>, <c>[WordColumnWidth]</c>, <c>[WordFormat]</c>,
/// <c>[WordIgnore]</c>). Memory is O(columns), independent of row count.
/// <see cref="Read(string, WordReadOptions?)"/> goes the other way: a .docx comes back as paragraphs
/// and tables, with the same column model available through <see cref="WordTable.Query"/>.
/// </summary>
public static class Word
{
    /// <summary>Writes <paramref name="value"/> to a .docx file, replacing it if it exists.</summary>
    public static void SaveAs(string path, object value, WordOptions? options = null)
    {
        options ??= new WordOptions();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        new DocxWriter(options).Write(fs, value);
    }

    /// <summary>Writes <paramref name="value"/> to a stream as a complete .docx package.</summary>
    public static void SaveAs(Stream stream, object value, WordOptions? options = null)
    {
        options ??= new WordOptions();
        new DocxWriter(options).Write(stream, value);
    }

    /// <summary>Asynchronously writes <paramref name="value"/> to a .docx file.</summary>
    public static Task SaveAsAsync(string path, object value, WordOptions? options = null, CancellationToken ct = default)
        => Task.Run(() => SaveAs(path, value, options), ct);

    /// <summary>Serialises <paramref name="value"/> and returns the .docx bytes.</summary>
    public static byte[] GetAsByteArray(object value, WordOptions? options = null)
    {
        using var ms = new MemoryStream();
        SaveAs(ms, value, options);
        return ms.ToArray();
    }

    /// <summary>Writes <paramref name="lines"/> as a prose .docx (one paragraph per line), replacing the file if it exists.</summary>
    public static void SaveLines(string path, IEnumerable<string> lines, WordOptions? options = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        new DocxWriter(options ?? new WordOptions()).WriteLines(fs, lines);
    }

    /// <summary>Writes <paramref name="lines"/> as a prose .docx into a stream.</summary>
    public static void SaveLines(Stream stream, IEnumerable<string> lines, WordOptions? options = null)
        => new DocxWriter(options ?? new WordOptions()).WriteLines(stream, lines);

    /// <summary>Serialises <paramref name="lines"/> and returns the .docx bytes.</summary>
    public static byte[] GetLinesAsByteArray(IEnumerable<string> lines, WordOptions? options = null)
    {
        using var ms = new MemoryStream();
        SaveLines(ms, lines, options);
        return ms.ToArray();
    }

    /// <summary>
    /// Reads a .docx into a block model (<see cref="WordDocument.Paragraphs"/> / <see cref="WordDocument.Tables"/>).    /// The reader is deliberately literal: it applies no document rules of its own, so callers parse the blocks
    /// with their own conventions.
    /// </summary>
    public static WordDocument Read(string path, WordReadOptions? options = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return DocxReader.Read(fs, options ?? new WordReadOptions());
    }

    /// <summary>Reads a .docx package from a stream (non-seekable streams are buffered first).</summary>
    public static WordDocument Read(Stream stream, WordReadOptions? options = null)
        => DocxReader.Read(stream, options ?? new WordReadOptions());

    /// <summary>Asynchronously reads a .docx file.</summary>
    public static Task<WordDocument> ReadAsync(string path, WordReadOptions? options = null, CancellationToken ct = default)
        => Task.Run(() => Read(path, options), ct);
}
