using System.Collections;
using System.Globalization;
using System.Reflection;

namespace FastExcel.Internal;

/// <summary>
/// Name-based value lookup used by the template engine: supports dictionaries, ExpandoObject and
/// POCOs, with a small reflection cache. Template rendering is low-volume, so reflection
/// (rather than expression compilation) is the right trade-off here.
/// </summary>
internal static class ValueResolver
{
    private static readonly Dictionary<(Type, string), MemberInfo?> _members = new();
    private static readonly object _gate = new();

    /// <summary>Resolves a dotted path against a root value. Returns false when any segment is missing.</summary>
    public static bool TryResolve(object? root, string path, out object? value)
    {
        value = null;
        var segments = path.Split('.', 2, StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || !TryGetMember(root, segments[0], out object? cur))
            return false;
        if (segments.Length == 1) { value = cur; return true; }
        // remaining dotted tail
        return TryResolve(cur, path[(segments[0].Length + 1)..], out value);
    }

    public static bool TryGetMember(object? target, string name, out object? value)
    {
        value = null;
        if (target is null || name.Length == 0) return false;

        if (target is IDictionary<string, object?> d)
        {
            if (d.TryGetValue(name, out value)) return true;
            foreach (var kv in d)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
            return false;
        }
        if (target is IDictionary nd)
        {
            if (nd.Contains(name)) { value = nd[name]; return true; }
            foreach (DictionaryEntry e in nd)
                if (string.Equals((string?)e.Key, name, StringComparison.OrdinalIgnoreCase)) { value = e.Value; return true; }
            return false;
        }

        var member = LookupMember(target.GetType(), name);
        if (member is null) return false;
        value = member switch
        {
            PropertyInfo p => p.GetValue(target),
            FieldInfo f => f.GetValue(target),
            _ => null
        };
        return true;
    }

    private static MemberInfo? LookupMember(Type type, string name)
    {
        var key = (type, name);
        lock (_gate)
        {
            if (_members.TryGetValue(key, out var cached)) return cached;
        }
        MemberInfo? found = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (found is null)
            found = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        lock (_gate) _members[key] = found;
        return found;
    }

    /// <summary>True when the value should drive row expansion (a non-string enumerable).</summary>
    public static bool IsEnumerableValue(object? value) =>
        value is not null and not string and not byte[] and IEnumerable;

    public static List<object?> Materialize(object value)
    {
        var list = new List<object?>();
        foreach (var item in (IEnumerable)value) list.Add(item);
        return list;
    }

    /// <summary>Invariant-culture display text for scalar replacement.</summary>
    public static string ToDisplay(object? value) => value switch
    {
        null => "",
        bool b => b ? "TRUE" : "FALSE",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    public static bool IsNumeric(object? value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
