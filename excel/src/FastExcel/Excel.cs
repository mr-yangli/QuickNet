using FastExcel.Import;
using FastExcel.Internal.OpenXml;

namespace FastExcel;

/// <summary>
/// Entry point for FastExcel. All methods stream, so exporting or importing millions of rows keeps
/// memory roughly constant (O(column width), not O(row count)).
/// </summary>
public static class Excel
{
    // ---------------------------------------------------------------------------------------------
    // Export
    // ---------------------------------------------------------------------------------------------

    /// <summary>Writes <paramref name="value"/> to an .xlsx file, replacing it if it exists.</summary>
    /// <param name="path">Target file path.</param>
    /// <param name="value">An IEnumerable (POCO / dictionary / ExpandoObject / DataRow) or a sequence of <see cref="ExcelSheet"/> for multi-sheet output.</param>
    /// <param name="options">Export settings; null uses defaults.</param>
    public static void SaveAs(string path, object value, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        var sheets = SheetBuilder.Build(value, options);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, options.BufferSize);
        using var buffered = new BufferedStream(fs, options.BufferSize);
        new XlsxWriter(buffered, options).WriteAsync(sheets, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>Writes <paramref name="value"/> to a stream as a complete .xlsx package.</summary>
    public static void SaveAs(Stream stream, object value, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        var sheets = SheetBuilder.Build(value, options);
        new XlsxWriter(stream, options).WriteAsync(sheets, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>Asynchronously writes <paramref name="value"/> to an .xlsx file.</summary>
    public static async Task SaveAsAsync(string path, object value, ExportOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ExportOptions();
        var sheets = SheetBuilder.Build(value, options);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, options.BufferSize, useAsync: true);
        await using var buffered = new BufferedStream(fs, options.BufferSize);
        await new XlsxWriter(buffered, options).WriteAsync(sheets, ct).ConfigureAwait(false);
    }

    /// <summary>Asynchronously writes <paramref name="value"/> to a stream.</summary>
    public static async Task SaveAsAsync(Stream stream, object value, ExportOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ExportOptions();
        var sheets = SheetBuilder.Build(value, options);
        await new XlsxWriter(stream, options).WriteAsync(sheets, ct).ConfigureAwait(false);
    }

    /// <summary>Serialises <paramref name="value"/> and returns the .xlsx bytes.</summary>
    public static byte[] GetAsByteArray(object value, ExportOptions? options = null)
    {
        using var ms = new MemoryStream();
        SaveAs(ms, value, options);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------------------------------------
    // Query (read-only, no validation)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Streams rows as header→value dictionaries (or column-letter keys when <paramref name="useHeaderRow"/> is false).</summary>
    public static IEnumerable<IDictionary<string, object?>> Query(string path, bool useHeaderRow = true, ImportOptions? options = null)
    {
        options ??= new ImportOptions { HeaderRowIndex = 0 };
        using var fs = OpenRead(path);
        foreach (var row in ExcelQuery.Dynamic(fs, options, useHeaderRow))
            yield return row;
    }

    /// <summary>Streams rows from a seekable stream as header→value dictionaries.</summary>
    public static IEnumerable<IDictionary<string, object?>> Query(Stream stream, bool useHeaderRow = true, ImportOptions? options = null)
    {
        options ??= new ImportOptions { HeaderRowIndex = 0 };
        using var seekable = EnsureSeekable(stream, out bool own);
        foreach (var row in ExcelQuery.Dynamic(seekable, options, useHeaderRow))
            yield return row;
    }

    /// <summary>Streams strongly-typed rows (no validation; use <see cref="Import{T}(string, ImportOptions?)"/> for validation).</summary>
    public static IEnumerable<T> Query<T>(string path, ImportOptions? options = null) where T : class, new()
    {
        options ??= new ImportOptions();
        using var fs = OpenRead(path);
        foreach (var row in ExcelQuery.Typed<T>(fs, options))
            yield return row;
    }

    /// <summary>Streams strongly-typed rows from a stream.</summary>
    public static IEnumerable<T> Query<T>(Stream stream, ImportOptions? options = null) where T : class, new()
    {
        options ??= new ImportOptions();
        using var seekable = EnsureSeekable(stream, out bool own);
        foreach (var row in ExcelQuery.Typed<T>(seekable, options))
            yield return row;
    }

    // ---------------------------------------------------------------------------------------------
    // Import (read + DataAnnotations validation)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Reads and validates a worksheet into an <see cref="ImportResult{T}"/> (never throws on data errors).</summary>
    public static ImportResult<T> Import<T>(string path, ImportOptions? options = null) where T : class, new()
    {
        options ??= new ImportOptions();
        using var fs = OpenRead(path);
        return ExcelImporter.Import<T>(fs, options);
    }

    /// <summary>Reads and validates a worksheet from a stream.</summary>
    public static ImportResult<T> Import<T>(Stream stream, ImportOptions? options = null) where T : class, new()
    {
        options ??= new ImportOptions();
        using var seekable = EnsureSeekable(stream, out bool own);
        return ExcelImporter.Import<T>(seekable, options);
    }

    /// <summary>Async wrapper over <see cref="Import{T}(string, ImportOptions?)"/>.</summary>
    public static Task<ImportResult<T>> ImportAsync<T>(string path, ImportOptions? options = null, CancellationToken ct = default) where T : class, new()
        => Task.Run(() => Import<T>(path, options), ct);

    /// <summary>Async wrapper over <see cref="Import{T}(Stream, ImportOptions?)"/>.</summary>
    public static Task<ImportResult<T>> ImportAsync<T>(Stream stream, ImportOptions? options = null, CancellationToken ct = default) where T : class, new()
        => Task.Run(() => Import<T>(stream, options), ct);

    // ---------------------------------------------------------------------------------------------
    // Templates
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Renders an .xlsx template: replaces <c>{{Name}}</c> placeholders with values, expands
    /// <c>{{items.field}}</c> rows once per element (styles preserved) and embeds <c>{{@field}}</c>
    /// images. All other template parts (styles, theme, other sheets, existing logos) are kept.
    /// </summary>
    /// <param name="outputPath">Target file, replaced if it exists.</param>
    /// <param name="templatePath">Template .xlsx path.</param>
    /// <param name="value">A dictionary (string keys) or POCO; property/keys addressed by the placeholder names.</param>
    /// <param name="options">Template settings; null uses defaults.</param>
    public static void SaveAsByTemplate(string outputPath, string templatePath, object value, TemplateOptions? options = null)
    {
        options ??= new TemplateOptions();
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var template = OpenRead(templatePath);
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var buffered = new BufferedStream(fs, 1 << 16);
        TemplateEngine.Render(template, buffered, value, options);
    }

    /// <summary>Stream overload of <see cref="SaveAsByTemplate(string, string, object, TemplateOptions?)"/>; the template stream must be seekable.</summary>
    public static void SaveAsByTemplate(Stream output, Stream template, object value, TemplateOptions? options = null)
    {
        options ??= new TemplateOptions();
        using var seekable = EnsureSeekable(template, out _);
        TemplateEngine.Render(seekable, output, value, options);
    }

    /// <summary>Writes a rendered template to a stream, reading the template from disk.</summary>
    public static void SaveAsByTemplate(Stream output, string templatePath, object value, TemplateOptions? options = null)
    {
        options ??= new TemplateOptions();
        using var template = OpenRead(templatePath);
        TemplateEngine.Render(template, output, value, options);
    }

    public static Task SaveAsByTemplateAsync(string outputPath, string templatePath, object value, TemplateOptions? options = null, CancellationToken ct = default)
        => Task.Run(() => SaveAsByTemplate(outputPath, templatePath, value, options), ct);

    public static Task SaveAsByTemplateAsync(Stream output, Stream template, object value, TemplateOptions? options = null, CancellationToken ct = default)
        => Task.Run(() => SaveAsByTemplate(output, template, value, options), ct);

    /// <summary>Renders a template into memory and returns the .xlsx bytes.</summary>
    public static byte[] GetAsByteArrayByTemplate(string templatePath, object value, TemplateOptions? options = null)
    {
        using var ms = new MemoryStream();
        SaveAsByTemplate(ms, templatePath, value, options);
        return ms.ToArray();
    }

    /// <summary>
    /// Imports a data file using the column layout declared by a template: headers are read from the
    /// template sheet (at <see cref="ImportOptions.HeaderRowIndex"/>) and used to map data columns,
    /// so the data file itself may omit or reorder its header row.
    /// </summary>
    public static ImportResult<T> ImportByTemplate<T>(string dataPath, string templatePath, ImportOptions? options = null) where T : class, new()
    {
        options ??= new ImportOptions();
        using var data = OpenRead(dataPath);
        using var template = OpenRead(templatePath);
        return ExcelImporter.ImportByTemplate<T>(data, template, options);
    }

    /// <summary>Stream overload of <see cref="ImportByTemplate{T}(string, string, ImportOptions?)"/>; both streams must be seekable.</summary>
    public static ImportResult<T> ImportByTemplate<T>(Stream data, Stream template, ImportOptions? options = null) where T : class, new()
    {
        options ??= new ImportOptions();
        using var dataSeekable = EnsureSeekable(data, out _);
        using var templateSeekable = EnsureSeekable(template, out _);
        return ExcelImporter.ImportByTemplate<T>(dataSeekable, templateSeekable, options);
    }

    // ---------------------------------------------------------------------------------------------
    // Introspection
    // ---------------------------------------------------------------------------------------------

    /// <summary>Returns worksheet names in document order.</summary>
    public static IReadOnlyList<string> GetSheetNames(string path)
    {
        using var fs = OpenRead(path);
        using var pkg = XlsxPackageReader.Open(fs);
        return pkg.Sheets.Select(s => s.Name).ToList();
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

    private static Stream EnsureSeekable(Stream stream, out bool ownsCopy)
    {
        ownsCopy = false;
        if (stream.CanSeek) return stream;
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        ownsCopy = true;
        return ms;
    }
}
