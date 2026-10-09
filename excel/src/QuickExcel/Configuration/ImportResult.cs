namespace QuickExcel;

/// <summary>Why a value failed to import.</summary>
public enum ImportErrorKind
{
    /// <summary>The cell text could not be converted to the target CLR type.</summary>
    Conversion,
    /// <summary>A DataAnnotations rule (Required/Range/Regex/...) or IValidatableObject check failed.</summary>
    Validation,
    /// <summary>A required column was missing from the sheet entirely (template mismatch).</summary>
    MissingColumn
}

/// <summary>One field-level error located to a specific row, field and column.</summary>
public sealed class ImportError
{
    public required int RowNumber { get; init; }
    public required string FieldName { get; init; }
    public required string Header { get; init; }

    /// <summary>Zero-based worksheet column of the offending cell; null when the column could not be located.</summary>
    public int? ColumnIndex { get; init; }

    /// <summary>Excel-style "AB12" reference of the offending cell; null when unavailable.</summary>
    public string? CellReference { get; init; }

    public required string Message { get; init; }
    public ImportErrorKind Kind { get; init; } = ImportErrorKind.Validation;

    public override string ToString() =>
        $"row {RowNumber} [{Header}]: {Message}";
}

/// <summary>
/// Result of a validated import: successfully parsed rows plus a flat, located list of per-cell
/// errors. Unlike a throw-on-first-error design, every row is attempted so callers get a complete
/// error report in one pass.
/// </summary>
public sealed class ImportResult<T>
{
    /// <summary>Rows that parsed and validated cleanly.</summary>
    public List<T> Data { get; } = new();

    /// <summary>One entry per failed cell across all rows.</summary>
    public List<ImportError> Errors { get; } = new();

    /// <summary>Errors about the sheet shape itself (e.g. a required column not found).</summary>
    public List<string> TemplateErrors { get; } = new();

    /// <summary>Set when the whole import aborted (unreadable file, bad package, etc.).</summary>
    public Exception? Exception { get; internal set; }

    /// <summary>Total data rows read (excluding the header row).</summary>
    public int TotalRows { get; internal set; }

    public bool HasErrors => Errors.Count > 0 || TemplateErrors.Count > 0 || Exception is not null;

    /// <summary>Distinct worksheet row numbers that had at least one error.</summary>
    public IEnumerable<int> FailedRowNumbers()
    {
        var seen = new HashSet<int>();
        foreach (var e in Errors)
            if (seen.Add(e.RowNumber))
                yield return e.RowNumber;
    }
}
