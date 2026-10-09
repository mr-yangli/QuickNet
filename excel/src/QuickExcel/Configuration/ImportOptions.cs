namespace QuickExcel;

/// <summary>Settings for import (reading + validating) a worksheet.</summary>
public sealed class ImportOptions
{
    /// <summary>Worksheet to read; null means the first sheet.</summary>
    public string? SheetName { get; set; }

    /// <summary>Zero-based row index that holds headers. -1 means auto-detect the first non-empty row.</summary>
    public int HeaderRowIndex { get; set; } = 0;

    /// <summary>When false, rows are matched positionally (by <see cref="ExcelColumnAttribute.ColumnIndex"/>) instead of by header text.</summary>
    public bool MatchHeadersByName { get; set; } = true;

    /// <summary>Throw on the first validation failure instead of collecting into <see cref="ImportResult{T}.Errors"/>. Default false.</summary>
    public bool ThrowOnError { get; set; }

    /// <summary>Stop reading after this many data rows; 0 = unlimited. Useful to bound import size.</summary>
    public int MaxRows { get; set; }

    /// <summary>Trim surrounding whitespace from string cell values before validation. Default true.</summary>
    public bool TrimStrings { get; set; } = true;

    /// <summary>Rows that fail validation are also returned (partially populated) in Data. Default false (only valid rows).</summary>
    public bool IncludeInvalidRowsInData { get; set; }

    /// <summary>
    /// Fill merged-cell ranges so every row inside a merged block sees the block's value. OOXML stores a
    /// merged block's value only in its top-left cell; with this on (default) that value is rehydrated into
    /// the otherwise-empty cells of the range as rows stream past. Set false to skip the merge scan.
    /// </summary>
    public bool FillMergedCells { get; set; } = true;

    public ImportOptions() { }
    public ImportOptions Clone() => (ImportOptions)MemberwiseClone();
}
