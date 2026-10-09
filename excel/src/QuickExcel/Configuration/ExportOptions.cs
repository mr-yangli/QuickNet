using System.IO.Compression;

namespace QuickExcel;

/// <summary>Settings that control how a worksheet is produced. All have sensible defaults.</summary>
public sealed class ExportOptions
{
    /// <summary>Worksheet name when not supplied per sheet. Default "Sheet1".</summary>
    public string SheetName { get; set; } = "Sheet1";

    /// <summary>Write a header row from column names. Default true.</summary>
    public bool PrintHeader { get; set; } = true;

    /// <summary>Bold the header row. Default false.</summary>
    public bool BoldHeader { get; set; }

    /// <summary>Freeze the header row so it stays visible when scrolling. Default false.</summary>
    public bool FreezeHeaderRow { get; set; }

    /// <summary>Inline (default, O(1) memory) vs shared-string storage.</summary>
    public StringStorageMode StringStorage { get; set; } = StringStorageMode.Inline;

    /// <summary>Zip compression level for worksheet data. NoCompression is fastest; MaximalCompress yields the smallest file.</summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;

    /// <summary>Stream buffer size in bytes for worksheet writing. Default 1 MiB.</summary>
    public int BufferSize { get; set; } = 1 << 20;

    /// <summary>Create a fresh export options instance with defaults.</summary>
    public ExportOptions() { }

    /// <summary>Convenience clone-modify for tweaks without rebuilding defaults.</summary>
    public ExportOptions Clone() => (ExportOptions)MemberwiseClone();
}
