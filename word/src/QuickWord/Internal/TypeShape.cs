using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace QuickWord.Internal;

/// <summary>
/// Derives the (header / order / width / format / compiled getter) shape of a POCO type from
/// QuickWord's own attributes, cached per type. First-touch reflection only; the hot path runs
/// through compiled delegates.
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
                   .Where(m => m.GetCustomAttribute<WordIgnoreAttribute>() is null)
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
        var col = member.GetCustomAttribute<WordColumnAttribute>();
        var fmt = member.GetCustomAttribute<WordFormatAttribute>();
        var widthAttr = member.GetCustomAttribute<WordColumnWidthAttribute>();

        var memberType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

        return new ColumnMetadata
        {
            HeaderName = col?.Name ?? member.Name,
            MemberType = memberType,
            Order = col?.Order ?? 1000,
            Width = widthAttr is not null ? widthAttr.Width
                  : col is not null && col.Width >= 0 ? col.Width
                  : -1,
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
        // single Convert: boxes value types, no-op for reference types
        Expression body = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(body, instanceParam).Compile();
    }
}
