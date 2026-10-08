using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;

namespace FastExcel.Internal.Metadata;

/// <summary>
/// Cached column layout for a POCO type plus a compiled factory. Obtained through
/// <see cref="TypeMetadataCache.For(Type)"/>; never rebuilt after first access.
/// </summary>
internal sealed class TypeMetadata
{
    public required Type Type { get; init; }
    public required string? SheetName { get; init; }

    /// <summary>Columns in export order.</summary>
    public required ExcelColumnMetadata[] Columns { get; init; }

    /// <summary>Header-to-column index for name-based import matching (case/space-insensitive keys).</summary>
    public required Dictionary<string, int> HeaderIndex { get; init; }

    public required Func<object> Factory { get; init; }

    public bool HasImageColumns
    {
        get
        {
            foreach (var c in Columns)
                if (c.IsImage) return true;
            return false;
        }
    }
}

internal static class TypeMetadataCache
{
    private static readonly ConcurrentDictionary<Type, TypeMetadata> _cache = new();

    public static TypeMetadata For(Type type) => _cache.GetOrAdd(type, Build);

    private static TypeMetadata Build(Type type)
    {
        var described = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                          .Cast<MemberInfo>()
                          .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                          .Where(m => m.GetCustomAttribute<ExcelIgnoreAttribute>() is null)
                          .Where(CanWriteOrRead)
                          .Select((m, i) => Describe(m, i))
                          .OrderBy(d => d.Column.Order)
                          .ThenBy(d => d.DeclarationIndex)
                          .ToArray();

        var columns = new ExcelColumnMetadata[described.Length];
        var headerIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < described.Length; i++)
        {
            columns[i] = described[i].Column;
            // first writer wins so duplicate headers map to the earliest column
            var key = NormalizeHeader(described[i].Column.HeaderName);
            if (!headerIndex.ContainsKey(key))
                headerIndex[key] = i;
            foreach (var alias in described[i].Column.Aliases)
                if (!headerIndex.ContainsKey(alias))
                    headerIndex[alias] = i;
        }

        var sheet = type.GetCustomAttribute<ExcelSheetAttribute>()?.Name;
        var meta = new TypeMetadata
        {
            Type = type,
            SheetName = sheet,
            Columns = columns,
            HeaderIndex = headerIndex,
            Factory = CompileFactory(type)
        };
        return meta;
    }

    private static bool CanWriteOrRead(MemberInfo m) => m switch
    {
        PropertyInfo p => p.CanRead && p.GetMethod?.IsStatic == false,
        FieldInfo => true,
        _ => false
    };

    private sealed record Described(ExcelColumnMetadata Column, int DeclarationIndex);

    private static Described Describe(MemberInfo member, int index)
    {
        int declIndex = index;
        var col = member.GetCustomAttribute<ExcelColumnAttribute>();
        var fmt = member.GetCustomAttribute<ExcelFormatAttribute>();
        var widthAttr = member.GetCustomAttribute<ExcelColumnWidthAttribute>();
        var imgAttr = member.GetCustomAttribute<ExcelImageAttribute>();

        string header = col?.Name ?? member.Name;
        Type memberType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

        bool isImage = imgAttr is not null || memberType == typeof(ExcelImage) || Nullable.GetUnderlyingType(memberType) == typeof(ExcelImage);

        var validation = member.GetCustomAttributes<ValidationAttribute>(inherit: true).ToArray();

        var column = new ExcelColumnMetadata
        {
            HeaderName = header,
            Aliases = col?.Aliases is null or { Length: 0 }
                ? System.Array.Empty<string>()
                : col.Aliases.Select(NormalizeHeader).ToArray(),
            MemberName = member.Name,
            MemberType = memberType,
            Order = col?.Order ?? 1000,
            ColumnIndex = col?.ColumnIndex ?? -1,
            Width = ResolveWidth(col, widthAttr),
            FormatString = col?.Format ?? fmt?.FormatString,
            // Attribute Required defaults to false, so it must not hide the DataAnnotations [Required] contract.
            Required = (col?.Required ?? false) || validation.OfType<RequiredAttribute>().Any(),
            IsImage = isImage,
            AutoTrim = col?.AutoTrim ?? true,
            ImageOptions = imgAttr,
            Getter = CompileGetter(member),
            Setter = CompileSetter(member),
            ValidationAttributes = validation
        };

        return new Described(column, declIndex);
    }

    private static double ResolveWidth(ExcelColumnAttribute? col, ExcelColumnWidthAttribute? widthAttr)
    {
        if (widthAttr is not null) return widthAttr.Width;
        if (col is not null && col.Width >= 0) return col.Width;
        return -1;
    }

    public static string NormalizeHeader(string header) => header.Replace(" ", "").Replace("\t", "").Trim();

    // ---- expression compilation ----

    private static Func<object, object?> CompileGetter(MemberInfo member)
    {
        var instanceParam = Expression.Parameter(typeof(object), "instance");
        var declaring = member.DeclaringType!;
        Expression access = member switch
        {
            PropertyInfo p => Expression.Property(Expression.Convert(instanceParam, declaring), p),
            FieldInfo f => Expression.Field(Expression.Convert(instanceParam, declaring), f),
            _ => throw new InvalidOperationException()
        };
        // single Convert: boxes value types, no-op for reference types
        Expression body = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(body, instanceParam).Compile();
    }

    private static Action<object, object?> CompileSetter(MemberInfo member)
    {
        // Read-only properties (e.g. anonymous types used for export-only) simply ignore writes.
        if (member is PropertyInfo { CanWrite: false })
            return static (_, _) => { };

        var instanceParam = Expression.Parameter(typeof(object), "instance");
        var valueParam = Expression.Parameter(typeof(object), "value");
        var declaring = member.DeclaringType!;
        var targetType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

        Expression value = targetType.IsValueType
            ? Expression.Convert(valueParam, targetType)
            : Expression.TypeAs(valueParam, targetType);

        Expression assign = member switch
        {
            PropertyInfo pr => Expression.Assign(
                Expression.Property(Expression.Convert(instanceParam, declaring), pr), value),
            FieldInfo f => Expression.Assign(
                Expression.Field(Expression.Convert(instanceParam, declaring), f), value),
            _ => throw new InvalidOperationException()
        };

        return Expression.Lambda<Action<object, object?>>(assign, instanceParam, valueParam).Compile();
    }

    private static Func<object> CompileFactory(Type type)
    {
        var ctor = type.GetConstructor(Type.EmptyTypes);
        if (ctor is null)
        {
            // fall back to runtime activation for records / single-ctor types
            return () => Activator.CreateInstance(type)!;
        }
        return Expression.Lambda<Func<object>>(Expression.New(ctor)).Compile();
    }
}
