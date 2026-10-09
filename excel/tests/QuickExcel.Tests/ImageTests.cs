using System.IO.Compression;
using QuickExcel.Internal.OpenXml;
using QuickExcel.Tests.Models;

namespace QuickExcel.Tests;

public class ImageTests : IDisposable
{
    // 8x6 red, 12x4 blue, 8x6 green (all valid PNGs)
    private static readonly byte[] Red  = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAGCAIAAABxZ0isAAAAEklEQVR4nGP8z4AdMOEQp4sEAJiFAQtsMIKbAAAAAElFTkSuQmCC");
    private static readonly byte[] Blue = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAwAAAAECAIAAAA1REndAAAAFElEQVR4nGNkYPjPQAgwEVRBVUUAk1MBB0ZRoqAAAAAASUVORK5CYII=");
    private static readonly byte[] Green= Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAGCAIAAABxZ0isAAAAE0lEQVR4nGNk+M+AFTBhF6aPBACXhgELkc4magAAAABJRU5ErkJggg==");

    private readonly string _dir;
    public ImageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quickexcel_img_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    private string TempPath(string n = "img.xlsx") => Path.Combine(_dir, n);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Image_Roundtrips_And_Deduplicates()
    {
        var rows = new List<Photo>
        {
            new Photo { Name = "红", Pic = new ExcelImage(Red) },
            new Photo { Name = "蓝", Pic = new ExcelImage(Blue) },
            new Photo { Name = "红2", Pic = new ExcelImage(Red) }, // duplicate bytes -> should reuse media part
        };

        var path = TempPath();
        Excel.SaveAs(path, rows);

        // dedup: only 2 distinct images => 2 media parts
        using (var zip = ZipFile.OpenRead(path))
        {
            var media = zip.Entries.Where(e => e.FullName.StartsWith("xl/media/")).ToList();
            Assert.Equal(2, media.Count);
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("xl/drawings/drawing"));
            Assert.Contains(zip.Entries, e => e.FullName == "[Content_Types].xml");
        }

        // reopen with openpyxl-equivalent: read back via QuickExcel import
        var result = Excel.Import<Photo>(path);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors));
        Assert.Equal(3, result.Data.Count);
        Assert.NotNull(result.Data[0].Pic);
        Assert.True(Red.AsSpan().SequenceEqual(result.Data[0].Pic!.Data.Span));
        Assert.True(Blue.AsSpan().SequenceEqual(result.Data[1].Pic!.Data.Span));
        Assert.True(Red.AsSpan().SequenceEqual(result.Data[2].Pic!.Data.Span));

        // dimensions preserved
        Assert.Equal(8, result.Data[0].Pic!.PixelWidth);
        Assert.Equal(6, result.Data[0].Pic!.PixelHeight);
        Assert.Equal(12, result.Data[1].Pic!.PixelWidth);
    }

    [Fact]
    public void Byte_Array_Image_Column_Works()
    {
        var rows = new List<BytePhoto>
        {
            new BytePhoto { Name = "a", Image = Red },
            new BytePhoto { Name = "b", Image = Green },
        };
        var path = TempPath("byte.xlsx");
        Excel.SaveAs(path, rows);
        var back = Excel.Import<BytePhoto>(path);
        Assert.Equal(2, back.Data.Count);
        Assert.True(Red.AsSpan().SequenceEqual(back.Data[0].Image));
        Assert.True(Green.AsSpan().SequenceEqual(back.Data[1].Image));
    }

    [Fact]
    public void ExcelImage_Detects_Format_And_Size()
    {
        var png = new ExcelImage(Red);
        Assert.Equal(ExcelImageFormat.Png, png.Format);
        Assert.Equal("png", png.Extension);
        Assert.Equal(8, png.PixelWidth);
        Assert.Equal(6, png.PixelHeight);
        Assert.Equal("image/png", png.ContentType);
    }

    // Templates ship pre-styled valueless rows (<c s="1"/> with no <v>) to carry borders. They are
    // blank rows even though the workbook also carries an anchored image.
    [Fact]
    public void Styled_Valueless_Rows_Are_Skipped_When_Workbook_Has_Images()
    {
        var path = TempPath("blank_rows.xlsx");
        Excel.SaveAs(path, new List<Photo> { new Photo { Name = "红", Pic = new ExcelImage(Red) } });
        InjectStyledEmptyRows(path, startRow: 3, count: 4, columns: 2);

        var result = Excel.Import<Photo>(path);

        Assert.Null(result.Exception);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors));
        Assert.Single(result.Data);
        Assert.Equal("红", result.Data[0].Name);
    }

    private static void InjectStyledEmptyRows(string path, int startRow, int count, int columns)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(entry);
        string xml;
        using (var reader = new StreamReader(entry!.Open()))
            xml = reader.ReadToEnd();

        string rows = "";
        for (int i = 0; i < count; i++)
        {
            int r = startRow + i;
            string cells = "";
            for (int c = 0; c < columns; c++)
                cells += $"<c r=\"{XmlUtility.ColumnLetter(c)}{r}\" s=\"1\"/>";
            rows += $"<row r=\"{r}\" spans=\"1:{columns}\">{cells}</row>";
        }

        xml = xml.Replace("</sheetData>", rows + "</sheetData>");
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open());
        writer.Write(xml);
    }
}
