using System.ComponentModel.DataAnnotations;
using FastExcel.Internal;
using FastExcel.Internal.Metadata;
using FastExcel.Internal.OpenXml;

namespace FastExcel.Import;

/// <summary>
/// Maps worksheet rows onto a POCO type and validates each populated instance with DataAnnotations
/// (and IValidatableObject). Every row is attempted; problems are reported as located
/// <see cref="ImportError"/> entries instead of aborting on the first failure.
/// </summary>
internal static class ExcelImporter
{
    public static ImportResult<T> Import<T>(Stream stream, ImportOptions options)
        => ImportInternal<T>(stream, options, validate: true);

    /// <summary>Lenient typed read used by Query&lt;T&gt;: no validation, conversion failures fall back to default.</summary>
    public static ImportResult<T> ReadUnvalidated<T>(Stream stream, ImportOptions options)
        => ImportInternal<T>(stream, options, validate: false);

    /// <summary>
    /// Import where the column layout is taken from a template file: the template's header row
    /// (at <see cref="ImportOptions.HeaderRowIndex"/>) is mapped onto the target type, and the data
    /// sheet is read using that mapping instead of its own header text.
    /// </summary>
    public static ImportResult<T> ImportByTemplate<T>(Stream data, Stream template, ImportOptions options)
    {
        var headers = ReadTemplateHeaders(template, options);
        return ImportInternal<T>(data, options, validate: true, overrideHeaders: headers);
    }

    private static Dictionary<int, string> ReadTemplateHeaders(Stream template, ImportOptions options)
    {
        using var pkg = XlsxPackageReader.Open(template);
        using var ws = pkg.ReadSheet(options);
        int targetRow = options.HeaderRowIndex + 1;
        if (!ws.Read())
            throw new InvalidDataException("Template sheet is empty.");
        while (ws.RowNumber < targetRow && ws.Read()) { }

        var headers = new Dictionary<int, string>();
        foreach (var (col, val) in ws.Cells())
            if (val is string s && s.Trim().Length > 0)
                headers[col] = TypeMetadataCache.NormalizeHeader(s);
        return headers;
    }

    private static ImportResult<T> ImportInternal<T>(Stream stream, ImportOptions options, bool validate,
        Dictionary<int, string>? overrideHeaders = null)
    {
        var result = new ImportResult<T>();
        var meta = TypeMetadataCache.For(typeof(T));

        try
        {
            using var pkg = XlsxPackageReader.Open(stream);
            using var ws = pkg.ReadSheet(options);

            // 1. Header row (advance to HeaderRowIndex)
            int targetRow = options.HeaderRowIndex + 1;
            if (!ws.Read())
                return result;
            while (ws.RowNumber < targetRow && ws.Read()) { }

            var headersByColumn = overrideHeaders ?? new Dictionary<int, string>();
            if (overrideHeaders is null)
            {
                foreach (var (col, val) in ws.Cells())
                    if (val is string s && s.Trim().Length > 0)
                        headersByColumn[col] = TypeMetadataCache.NormalizeHeader(s);
            }
            int headerRowNumber = ws.RowNumber;

            // 2. Resolve column -> metadata index (name match, then explicit ColumnIndex)
            var columnToMeta = new Dictionary<int, int>(); // worksheet column -> meta column index
            var usedMeta = new HashSet<int>();

            // explicit positional columns
            for (int m = 0; m < meta.Columns.Length; m++)
            {
                var c = meta.Columns[m];
                if (c.ColumnIndex >= 0)
                {
                    columnToMeta[c.ColumnIndex] = m;
                    usedMeta.Add(m);
                }
            }

            if (options.MatchHeadersByName)
            {
                foreach (var (worksheetCol, headerKey) in headersByColumn)
                {
                    if (columnToMeta.ContainsKey(worksheetCol)) continue; // positional wins
                    if (meta.HeaderIndex.TryGetValue(headerKey, out int m) && !usedMeta.Contains(m))
                    {
                        columnToMeta[worksheetCol] = m;
                        usedMeta.Add(m);
                    }
                }
            }

            // 3. Required columns missing from the sheet => template errors
            for (int m = 0; m < meta.Columns.Length; m++)
            {
                var c = meta.Columns[m];
                if (c.Required && !usedMeta.Contains(m))
                    result.TemplateErrors.Add($"Required column '{c.HeaderName}' was not found in the worksheet.");
            }

            // 4. Data rows
            Dictionary<(int, int), ExcelImage>? images = null;
            HashSet<int>? imageRows = null;
            if (meta.HasImageColumns)
            {
                var map = pkg.ReadImagesFor(options);
                // WPS fallback: =DISPIMG("ID",1) formulas backed by xl/cellimages.xml
                var cellImgs = CellImageReader.Read(pkg.Archive);
                if (cellImgs is not null)
                {
                    var idMap = CellImageReader.ScanSheetFormulas(
                        pkg.Archive, pkg.ResolveSheet(options.SheetName).PartPath);
                    foreach (var (rc, id) in idMap)
                        if (cellImgs.TryGetValue(id, out var cimg) && !map.ContainsKey(rc))
                            map[rc] = cimg;
                }
                images = map;
                imageRows = new HashSet<int>(map.Keys.Select(k => k.Item1));
            }
            var validationResults = new List<ValidationResult>();
            int dataRows = 0;
            while (ws.Read())
            {
                if (options.MaxRows > 0 && dataRows >= options.MaxRows) break;
                if (!ws.HasAny && imageRows?.Contains(ws.RowNumber - 1) != true) continue; // blank row: images float over cells, judge per row
                dataRows++;
                result.TotalRows++;
                int rowNum = ws.RowNumber;

                object instance = meta.Factory();
                var rowErrors = new List<ImportError>();

                foreach (var (worksheetCol, metaIdx) in columnToMeta)
                {
                    ws.TryGet(worksheetCol, out object? cell);
                    var col = meta.Columns[metaIdx];

                    // image columns float over the cell, not in it (or hide behind WPS DISPIMG text)
                    if (col.IsImage && cell is null or string && images is not null &&
                        images.TryGetValue((rowNum - 1, worksheetCol), out var img))
                        cell = img;

                    cell = PreprocessCell(cell, col);

                    if (!ValueConverter.TryConvert(cell, col.MemberType, options.TrimStrings, out object? converted, out string? convErr))
                    {
                        if (validate)
                            rowErrors.Add(MakeError(rowNum, worksheetCol, col, convErr!, ImportErrorKind.Conversion));
                        continue; // leave property at default
                    }
                    try { col.Setter(instance, converted); }
                    catch (Exception ex)
                    {
                        if (validate)
                            rowErrors.Add(MakeError(rowNum, worksheetCol, col, $"setter failed: {ex.Message}", ImportErrorKind.Conversion));
                    }
                }

                // 5. Always run DataAnnotations + IValidatableObject (conversion errors above don't short-circuit it,
                //    so a single row can report a bad date AND a missing required name together).
                if (validate)
                {
                    validationResults.Clear();
                    var ctx = new ValidationContext(instance) { DisplayName = $"Row {rowNum}" };
                    Validator.TryValidateObject(instance, ctx, validationResults, validateAllProperties: true);

                    // .NET quirk: Validator.TryValidateObject silently drops IValidatableObject errors whose
                    // member names refer to properties without a ValidationAttribute. Invoke GetErrors directly
                    // and merge in whatever the validator omitted (deduped by message + member names).
                    if (instance is IValidatableObject validatable)
                    {
                        foreach (var extra in validatable.Validate(ctx) ?? Array.Empty<ValidationResult>())
                        {
                            bool dup = false;
                            foreach (var kept in validationResults)
                            {
                                if (kept.ErrorMessage == extra.ErrorMessage &&
                                    kept.MemberNames.SequenceEqual(extra.MemberNames)) { dup = true; break; }
                            }
                            if (!dup) validationResults.Add(extra);
                        }
                    }

                    foreach (var vr in validationResults)
                    {
                        string? member = vr.MemberNames.FirstOrDefault();
                        int mIdx = FindMetaByMember(meta, member);
                        var col = mIdx >= 0 ? meta.Columns[mIdx] : null;
                        int wsCol = col is null ? -1 : FindWorksheetCol(columnToMeta, mIdx);
                        rowErrors.Add(new ImportError
                        {
                            RowNumber = rowNum,
                            FieldName = col?.MemberName ?? member ?? "?",
                            Header = col?.HeaderName ?? member ?? "?",
                            ColumnIndex = wsCol >= 0 ? wsCol : null,
                            CellReference = wsCol >= 0 ? CellRef(rowNum, wsCol) : null,
                            Message = vr.ErrorMessage ?? "validation failed",
                            Kind = ImportErrorKind.Validation
                        });
                    }
                }

                bool rowValid = rowErrors.Count == 0;
                if (rowValid)
                    result.Data.Add((T)instance);

                if (rowErrors.Count > 0)
                {
                    if (options.ThrowOnError)
                        throw new FastExcelImportException(rowErrors);
                    result.Errors.AddRange(rowErrors);
                    if (options.IncludeInvalidRowsInData)
                        result.Data.Add((T)instance);
                }
            }
        }
        catch (FastExcelImportException) { throw; }
        catch (Exception ex)
        {
            result.Exception = ex;
        }

        return result;
    }

    private static object? PreprocessCell(object? cell, ExcelColumnMetadata col)
    {
        if (cell is string s)
        {
            if (col.AutoTrim) s = s.Trim();
            return s;
        }
        return cell;
    }

    private static ImportError MakeError(int row, int wsCol, ExcelColumnMetadata col, string message, ImportErrorKind kind) =>
        new()
        {
            RowNumber = row,
            FieldName = col.MemberName,
            Header = col.HeaderName,
            ColumnIndex = wsCol,
            CellReference = CellRef(row, wsCol),
            Message = message,
            Kind = kind
        };

    private static int FindMetaByMember(TypeMetadata meta, string? member)
    {
        if (member is null) return -1;
        for (int i = 0; i < meta.Columns.Length; i++)
            if (string.Equals(meta.Columns[i].MemberName, member, StringComparison.Ordinal))
                return i;
        return -1;
    }

    private static int FindWorksheetCol(Dictionary<int, int> columnToMeta, int metaIdx)
    {
        foreach (var (wsCol, m) in columnToMeta)
            if (m == metaIdx) return wsCol;
        return -1;
    }

    private static string CellRef(int row, int col) => $"{XmlUtility.ColumnLetter(col)}{row}";
}

/// <summary>Thrown by an import configured with <see cref="ImportOptions.ThrowOnError"/>.</summary>
public sealed class FastExcelImportException : Exception
{
    public IReadOnlyList<ImportError> Errors { get; }
    public FastExcelImportException(IReadOnlyList<ImportError> errors) : base(Describe(errors))
        => Errors = errors;

    private static string Describe(IReadOnlyList<ImportError> errors) =>
        errors.Count == 0 ? "Import failed." : $"Import failed on row {errors[0].RowNumber}: {errors[0].Message}";
}
