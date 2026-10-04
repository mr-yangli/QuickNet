using System.IO.Compression;
using FastExcel.Tests.Models;

namespace FastExcel.Tests;

/// <summary>
/// Builds a WPS-style .xlsx by hand: the picture lives in xl/cellimages.xml and the cell carries
/// a =_xlfn.DISPIMG("ID",1) formula instead of a standard drawing anchor.
/// </summary>
public class DispImgTests : IDisposable
{
    private static readonly byte[] Red = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAGCAIAAABxZ0isAAAAEklEQVR4nGP8z4AdMOEQp4sEAJiFAQtsMIKbAAAAAElFTkSuQmCC");

    private readonly string _dir;
    public DispImgTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fastexcel_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string BuildWpsFile()
    {
        var p = Path.Combine(_dir, "wps.xlsx");
        using (var fs = File.Create(p))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                var e = zip.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), new System.Text.UTF8Encoding(false));
                w.Write(content);
            }

            Add("[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Default Extension="png" ContentType="image/png"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/cellimages.xml" ContentType="application/vnd.wps-office.cellimages+xml"/>
                </Types>
                """);
            Add("_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add("xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Add("xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            Add("xl/worksheets/sheet1.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1"><c r="A1" t="inlineStr"><is><t>名称</t></is></c><c r="B1" t="inlineStr"><is><t>图片</t></is></c></row>
                    <row r="2">
                      <c r="A2" t="inlineStr"><is><t>红图</t></is></c>
                      <c r="B2" t="str"><f>_xlfn.DISPIMG("ID_5B1E9E70A95A4A17",1)</f><v>=DISPIMG</v></c>
                    </row>
                  </sheetData>
                </worksheet>
                """);
            Add("xl/cellimages.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <etc:cellImages xmlns:etc="http://www.wps.cn/officeDocument/2017/etCustomData"
                  xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing"
                  xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                  xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <etc:cellImage>
                    <xdr:pic>
                      <xdr:nvPicPr><xdr:cNvPr id="2" name="ID_5B1E9E70A95A4A17"/><xdr:cNvPicPr/></xdr:nvPicPr>
                      <xdr:blipFill><a:blip r:embed="rId1"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>
                    </xdr:pic>
                  </etc:cellImage>
                </etc:cellImages>
                """);
            Add("xl/_rels/cellimages.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image1.png"/>
                </Relationships>
                """);
            {
                var e = zip.CreateEntry("xl/media/image1.png", CompressionLevel.NoCompression);
                using var w = e.Open();
                w.Write(Red);
            }
        }
        return p;
    }

    [Fact]
    public void Wps_DispImg_Imported_As_ExcelImage()
    {
        var path = BuildWpsFile();
        var result = Excel.Import<Photo>(path);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Single(result.Data);
        var pic = result.Data[0].Pic;
        Assert.NotNull(pic);
        Assert.True(Red.AsSpan().SequenceEqual(pic!.Data.Span));
        Assert.Equal(8, pic.PixelWidth);
        Assert.Equal(6, pic.PixelHeight);
        Assert.Equal("红图", result.Data[0].Name);
    }

    [Fact]
    public void Wps_DispImg_Query_Typed()
    {
        var path = BuildWpsFile();
        var rows = Excel.Query<Photo>(path).ToList();
        Assert.Single(rows);
        Assert.True(Red.AsSpan().SequenceEqual(rows[0].Pic!.Data.Span));
    }
}
