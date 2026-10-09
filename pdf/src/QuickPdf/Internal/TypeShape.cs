using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace QuickPdf.Internal;

/// <summary>
/// Type -> export columns, built once per type from the <c>[Pdf*]</c> attributes and cached.
/// Getters are compiled expression trees (declared-type convert + member access + box).
/// </summary>
internal static class TypeShape
{
    private static readonly ConcurrentDictionary<Type, ColumnMetadata[]> _cache = new();

    public static ColumnMetadata[] For(Type type) => _cache.GetOrAdd(type, Build);

    private static ColumnMetadata[] Build(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                   .Cast<MemberInfo>()
                   .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                   .Where(m => m.GetCustomAttribute<PdfIgnoreAttribute>() is null)
                   .Where(CanRead)
                   .Select((m, i) => (Column: Describe(m), DeclarationIndex: i))
                   .OrderBy(d => d.Column.Order)
                   .ThenBy(d => d.DeclarationIndex)
                   .Select(d => d.Column)
                   .ToArray();
    }

    private static bool CanRead(MemberInfo m) => m switch
    {
        PropertyInfo p => p.CanRead && p.GetMethod?.IsStatic == false,
        FieldInfo => true,
        _ => false
    };

    private static ColumnMetadata Describe(MemberInfo member)
    {
        var col = member.GetCustomAttribute<PdfColumnAttribute>();
        var fmt = member.GetCustomAttribute<PdfFormatAttribute>();
        var widthAttr = member.GetCustomAttribute<PdfColumnWidthAttribute>();
        var memberType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

        return new ColumnMetadata
        {
            HeaderName = col?.Name ?? member.Name,
            MemberType = memberType,
            Order = col?.Order ?? 1000,
            Width = widthAttr is not null ? widthAttr.Width
                  : col is not null && col.Width >= 0 ? col.Width : -1,
            FormatString = col?.Format ?? fmt?.FormatString,
            Getter = CompileGetter(member),
        };
    }

    private static Func<object, object?> CompileGetter(MemberInfo member)
    {
        var instanceParam = Expression.Parameter(typeof(object), "instance");
        var declaring = member.DeclaringType!;
        Expression access = member switch
        {
            PropertyInfo pr => Expression.Property(Expression.Convert(instanceParam, declaring), pr),
            FieldInfo f => Expression.Field(Expression.Convert(instanceParam, declaring), f),
            _ => throw new InvalidOperationException()
        };
        Expression body = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(body, instanceParam).Compile();
    }
}
