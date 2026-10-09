using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;

namespace QuickExcel.Internal.Metadata;

/// <summary>
/// Compiled, cached description of one member (property/field) as it maps to an Excel column.
/// Accessors are compiled once per type into delegates so import/export avoids reflection on the hot path.
/// </summary>
internal sealed class ExcelColumnMetadata
{
    /// <summary>Header text used for writing and for name-based matching on import.</summary>
    public required string HeaderName { get; init; }

    /// <summary>Extra header texts accepted on import (already normalized keys).</summary>
    public string[] Aliases { get; init; } = System.Array.Empty<string>();

    /// <summary>CLR member name (property/field).</summary>
    public required string MemberName { get; init; }

    public required Type MemberType { get; init; }

    public int Order { get; init; }

    /// <summary>Explicit left-to-right column index (&gt;=0) for positional mapping; -1 means name-based.</summary>
    public int ColumnIndex { get; init; } = -1;

    public double Width { get; init; } = -1;

    public string? FormatString { get; init; }

    public bool Required { get; init; }

    public bool IsImage { get; init; }

    public bool AutoTrim { get; init; } = true;

    /// <summary>The <see cref="ExcelImageAttribute"/> when this column carries images.</summary>
    public ExcelImageAttribute? ImageOptions { get; init; }

    /// <summary>Compiled getter: instance -&gt; value (boxes value types once).</summary>
    public required Func<object, object?> Getter { get; init; }

    /// <summary>Compiled setter: (instance, value) -&gt; void. Value is pre-converted to <see cref="MemberType"/>.</summary>
    public required Action<object, object?> Setter { get; init; }

    /// <summary>Validation attributes found on the member (DataAnnotations), used for cell-level checks.</summary>
    public IReadOnlyList<ValidationAttribute> ValidationAttributes { get; init; } = Array.Empty<ValidationAttribute>();
}
