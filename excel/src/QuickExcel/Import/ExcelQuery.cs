using QuickExcel.Internal;
using QuickExcel.Internal.Metadata;
using QuickExcel.Internal.OpenXml;

namespace QuickExcel.Import;

/// <summary>
/// Lazy worksheet queries. Rows are produced as they are parsed from the underlying stream, so
/// memory stays bounded regardless of row count; the returned enumerators own the file/stream for
/// their lifetime.
/// </summary>
internal static class ExcelQuery
{
    /// <summary>Enumerates rows as header→value dictionaries. When <paramref name="useHeaderRow"/> is false, keys are column letters.</summary>
    public static IEnumerable<IDictionary<string, object?>> Dynamic(Stream stream, ImportOptions options, bool useHeaderRow)
    {
        using var pkg = XlsxPackageReader.Open(stream);
        using var ws = pkg.ReadSheet(options);

        string[]? headers = null;
        if (useHeaderRow)
        {
            if (!ws.Read()) yield break;
            int max = ws.Row.MaxColumn;
            headers = new string[max + 1];
            for (int c = 0; c <= max; c++)
                if (ws.TryGet(c, out var hv) && hv is not null)
                    headers[c] = hv.ToString()!;
        }

        int yielded = 0;
        while (ws.Read())
        {
            if (options.MaxRows > 0 && yielded >= options.MaxRows) yield break;
            if (!ws.HasAny) continue;
            yielded++;

            if (headers is not null)
            {
                // Every header column is present, null-filled when the cell was blank — so
                // row["部门"] never throws on an empty cell.
                var dict = new Dictionary<string, object?>(headers.Length);
                for (int c = 0; c < headers.Length; c++)
                {
                    if (headers[c] is null) continue;
                    dict[headers[c]] = ws.TryGet(c, out var v) ? v : null;
                }
                yield return dict;
            }
            else
            {
                var dict = new Dictionary<string, object?>(ws.Row.MaxColumn + 1);
                for (int c = 0; c <= ws.Row.MaxColumn; c++)
                    if (ws.TryGet(c, out var v))
                        dict[XmlUtility.ColumnLetter(c)] = v;
                yield return dict;
            }
        }
    }

    /// <summary>Enumerates typed rows without validation (conversion failures leave the member at its default).</summary>
    public static IEnumerable<T> Typed<T>(Stream stream, ImportOptions options)
    {
        // ReadUnvalidated materialises; for streaming we reuse the same column-resolution but yield.
        foreach (var row in TypedLazy<T>(stream, options))
            yield return row;
    }

    private static IEnumerable<T> TypedLazy<T>(Stream stream, ImportOptions options)
    {
        var meta = TypeMetadataCache.For(typeof(T));
        using var pkg = XlsxPackageReader.Open(stream);
        using var ws = pkg.ReadSheet(options);

        if (!ws.Read()) yield break; // header row
        var headersByColumn = new Dictionary<int, string>();
        foreach (var (col, val) in ws.Cells())
            if (val is string s && s.Trim().Length > 0)
                headersByColumn[col] = TypeMetadataCache.NormalizeHeader(s);

        var columnToMeta = new Dictionary<int, int>();
        var usedMeta = new HashSet<int>();
        for (int m = 0; m < meta.Columns.Length; m++)
            if (meta.Columns[m].ColumnIndex >= 0)
            {
                columnToMeta[meta.Columns[m].ColumnIndex] = m;
                usedMeta.Add(m);
            }
        if (options.MatchHeadersByName)
            foreach (var (worksheetCol, headerKey) in headersByColumn)
                if (!columnToMeta.ContainsKey(worksheetCol) &&
                    meta.HeaderIndex.TryGetValue(headerKey, out int m) && !usedMeta.Contains(m))
                {
                    columnToMeta[worksheetCol] = m;
                    usedMeta.Add(m);
                }

        int yielded = 0;
        Dictionary<(int, int), ExcelImage>? images = null;
        if (meta.HasImageColumns)
        {
            var map = pkg.ReadImagesFor(options);
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
        }
        while (ws.Read())
        {
            if (options.MaxRows > 0 && yielded >= options.MaxRows) yield break;
            if (!ws.HasAny) continue;
            yielded++;

            object instance = meta.Factory();
            foreach (var (worksheetCol, metaIdx) in columnToMeta)
            {
                ws.TryGet(worksheetCol, out object? cell);
                var col = meta.Columns[metaIdx];
                if (col.IsImage && cell is null or string && images is not null &&
                    images.TryGetValue((ws.RowNumber - 1, worksheetCol), out var img))
                    cell = img;
                if (cell is string cs && col.AutoTrim) cell = cs.Trim();
                if (ValueConverter.TryConvert(cell, col.MemberType, options.TrimStrings, out object? converted, out _))
                    col.Setter(instance, converted);
            }
            yield return (T)instance;
        }
    }
}
