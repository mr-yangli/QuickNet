namespace FastExcel;

/// <summary>Workbook container formats. Only <see cref="Xlsx"/> is implemented today; Csv is on the roadmap.</summary>
public enum ExcelType
{
    Xlsx,
    Csv
}

/// <summary>How string cell values are stored in the sheet. Inline avoids a shared table; Shared dedups repeats.</summary>
public enum StringStorageMode
{
    /// <summary>Each string is written directly in its cell (lowest memory, default).</summary>
    Inline,
    /// <summary>Unique strings are interned into xl/sharedStrings.xml and referenced by index (smaller files for repetitive data).</summary>
    Shared
}
