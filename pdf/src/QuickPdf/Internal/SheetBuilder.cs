using System.Collections;

namespace QuickPdf.Internal;

internal sealed record SheetInput(string Name, IEnumerable<object?> Rows, ColumnMetadata[] Columns);

/// <summary>
/// Normalises whatever the caller passed (typed IEnumerable, dictionary/ExpandoObject rows,
/// DataTable, or a sequence of <see cref="PdfSheet"/>) into (name, columns, rows) triples.
/// </summary>
internal static class SheetBuilder
{
    public static List<SheetInput> Build(object value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));

        if (value is IEnumerable<PdfSheet> multi)
        {
            var list = new List<SheetInput>();
            int i = 1;
            foreach (var s in multi)
                list.Add(FromRows(s.Rows, string.IsNullOrEmpty(s.Name) ? $"Sheet{i++}" : s.Name));
            return list;
        }

        if (value is PdfSheet single)
            return new List<SheetInput> { FromRows(single.Rows, single.Name) };

        return new List<SheetInput> { FromRows(value, "Sheet1") };
    }

    private static SheetInput FromRows(object rows, string sheetName)
    {
        if (rows is not IEnumerable seq)
            throw new ArgumentException($"Export value must be a sequence (or {nameof(PdfSheet)}).");

        Type elem = GetEnumerableElement(rows, seq);

        // Dictionary / ExpandoObject rows: derive columns from keys
        bool dictRows = IsStringKeyedDictionary(elem) ||
                        (elem == typeof(object) && FirstIsDictionary(seq, out _));
        if (dictRows)
        {
            List<string>? keys = null;
            if (elem == typeof(object))
                FirstIsDictionary(seq, out keys);
            else
                PeekDictionaryKeys(seq, out keys);
            if (keys is null || keys.Count == 0)
                throw new ArgumentException("Cannot infer columns: the dictionary sequence is empty or has no keys. Pass an explicit header or use POCOs.");
            var cols = new List<ColumnMetadata>();
            int order = 0;
            foreach (var key in keys)
                cols.Add(SynthesizeDictColumn(key, order++));
            return new SheetInput(sheetName, Cast(seq), cols.ToArray());
        }

        // DataTable rows
        if (typeof(System.Data.DataTable).IsAssignableFrom(elem))
            return FromDataTable((System.Data.DataTable)rows, sheetName);

        return new SheetInput(sheetName, Cast(seq), TypeShape.For(elem));
    }

    private static SheetInput FromDataTable(System.Data.DataTable table, string sheetName)
    {
        var cols = new ColumnMetadata[table.Columns.Count];
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var dc = table.Columns[i];
            int idx = i;
            cols[i] = new ColumnMetadata
            {
                HeaderName = dc.ColumnName,
                MemberType = dc.DataType,
                Order = i,
                Getter = row =>
                {
                    object v = row switch
                    {
                        System.Data.DataRow dr => dr[idx],
                        System.Data.DataRowView drv => drv.Row[idx],
                        _ => ((System.Data.DataRow)row)[idx]
                    };
                    return v is DBNull ? null : v;
                },
            };
        }
        // rows as their own type; getter handles DataRow
        return new SheetInput(sheetName, Cast(table.Rows), cols);
    }

    private static ColumnMetadata SynthesizeDictColumn(string key, int order) => new()
    {
        HeaderName = key,
        MemberType = typeof(object),
        Order = order,
        Getter = row => row switch
        {
            IDictionary<string, object> d => d.TryGetValue(key, out var v) ? v : null,
            IDictionary d2 => d2.Contains(key) ? d2[key] : null,
            _ => null
        },
    };

    private static IEnumerable<object?> Cast(IEnumerable seq)
    {
        foreach (var item in seq)
            yield return item;
    }

    private static bool IsStringKeyedDictionary(Type t)
    {
        if (t == typeof(object)) return false;
        foreach (var i in new[] { t }.Concat(t.GetInterfaces()))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                && i.GetGenericArguments()[0] == typeof(string))
                return true;
        return false;
    }

    private static void PeekDictionaryKeys(IEnumerable seq, out List<string>? keys)
    {
        keys = null;
        foreach (var item in seq)
        {
            if (item is IDictionary<string, object> d) { keys = d.Keys.ToList(); return; }
            if (item is IDictionary nonGeneric) { keys = nonGeneric.Keys.Cast<object>().Select(k => k.ToString()!).ToList(); return; }
            return;
        }
    }

    private static bool FirstIsDictionary(IEnumerable seq, out List<string>? keys)
    {
        keys = null;
        foreach (var item in seq)
        {
            if (item is IDictionary<string, object> d) { keys = d.Keys.ToList(); return true; }
            return false;
        }
        return false;
    }

    private static Type GetEnumerableElement(object value, IEnumerable seq)
    {
        var t = value.GetType();
        foreach (var i in new[] { t }.Concat(t.GetInterfaces()))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return typeof(object);
    }
}
