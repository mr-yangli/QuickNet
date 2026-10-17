using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using QuickExcel.Internal;
using QuickExcel.Internal.OpenXml;
using QuickExcel.Tests.Models;
using Xunit;

namespace QuickExcel.Tests;

/// <summary>2026-10-17 审查第一档数行级修复的门禁，四颗各钉一处：
/// <list type="number">
/// <item><see cref="XmlUtility.Escape"/> 的快路径必须把 XML 1.0 非法控制字符也算进“要走慢路”的集合
/// —— 修前 <c>"\u0001abc"</c> 走快路径原样返回，丢控制字符的循环对这种值不可达；</item>
/// <item>属性上下文要用 <see cref="XmlUtility.EscapeAttribute"/> —— <c>"</c> 在元素文本里合法，
/// 落进双引号属性里就是结构破坏，所以这两颗工具各管一段、各有自己的快路径探针；</item>
/// <item>带着引号的 sheet 名（含属性注入形状 <c>Sheet" r:id="rId9</c>）产出的 workbook.xml
/// 必须能被 XML 解析器当场验过 —— 修前 <c>Escape</c> 不转义 <c>"</c>，同名会产出非法 XML，
/// 注入形状会产出两颗同名属性，两种都过不了解析器；</item>
/// <item>4–7 字节的图像缓冲不再把构造函数炸掉（WMF 判据要 8 字节：DWORD magic 在偏移 4）。</item>
/// </list>
/// 另有两颗转 Culture 的 decimal 用例（见方法注释）与一克文化现场自恢复。</summary>
public class EscapingGuardTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void Escape_drops_xml10_control_even_when_no_entity_char_is_present()
    {
        // 修前这三颗里只有含 & < > 的那颗经过慢路：控制字符原样漏出，产出非法 XML 1.0。
        Assert.Equal("abc", XmlUtility.Escape("\u0001a\u000Bb\u001Fc"));

        // 三个合法控制字符照旧保留。
        Assert.Equal("a\tb\nc\rd", XmlUtility.Escape("a\tb\nc\rd"));
    }

    [Fact]
    public void EscapeAttribute_escapes_the_quote_that_element_text_may_keep()
    {
        Assert.Equal("A&quot;B", XmlUtility.EscapeAttribute("A\"B"));
        Assert.Equal("A&quot;B&amp;C", XmlUtility.EscapeAttribute("A\"B&C"));

        // 元素文本那颗照旧不碰引号（两颗工具的分工在各自的快路径探针里，不在慢路里）。
        Assert.Equal("A\"B", XmlUtility.Escape("A\"B"));
    }

    [Theory]
    [InlineData("A\"B")]                          // 纯引号：非法 XML 的根
    [InlineData("She\"et foo=\"bar")]             // 属性注入形状：修前会凭空多出属性
    public void A_quoted_sheet_name_yields_a_parseable_workbook_with_exactly_three_attrs(string sheetName)
    {
        var rows = new[] { new Employee { Id = 1, Name = "甲", Salary = 1m, HireDate = new(2026, 1, 1) } };

        var bytes = Excel.GetAsByteArray(rows, new ExportOptions { SheetName = sheetName });

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = Assert.Single(zip.Entries, e => e.FullName == "xl/workbook.xml");

        // 修复前这一行抛 XmlException：坏格（"未转义）与注入格（属性被截断、凭空多出）都过不了解析器。
        // .NET 的解析器不容忍同名重复属性，所以"能解析"本身已含住"注入形状落不了地"那一半。
        var workbook = XDocument.Parse(new StreamReader(entry.Open(), System.Text.Encoding.UTF8).ReadToEnd());
        var sheet = Assert.Single(workbook.Descendants(Main + "sheet"));
        Assert.Equal(sheetName, (string?)sheet.Attribute("name"));

        // sheet 元素恰好三颗属性（name / sheetId / r:id——r:id 在根上绑定了 r 命名空间，
        // 所以解析后的 LocalName 是 "id"）——多出来的任何一颗都是注入。
        Assert.Equal(["name", "sheetId", "id"],
            sheet.Attributes().Select(a => a.Name.LocalName).ToList());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public void A_four_to_seven_byte_buffer_no_longer_explodes_the_image_constructor(int length)
    {
        // 修前构造函数在这里抛 ArgumentOutOfRangeException：WMF 判据写 s.Length >= 4 而读的是 s.Slice(4)。
        var image = new ExcelImage(new byte[length]);

        Assert.Equal(ExcelImageFormat.Unknown, image.Format);
    }

    [Fact]
    public void A_decimal_cell_parses_invariant_even_under_a_comma_decimal_culture()
    {
        // 修前这一颗在 de-DE 下得 314m：decimal 分支收的是文本格（raw 是 string），却用
        // CurrentCulture 解析——"3.14" 的句点在德文里是分组符，静默放大一百倍。
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.True(ValueConverter.TryConvert("3.14", typeof(decimal), trim: false, out var value, out var error));
            Assert.Null(error);
            Assert.Equal(3.14m, value);

            // 对照：整型分支从来都是不变文化，"3,140" 在 de-DE 的现行文化里是 3.14，在不变文化里仍 3140。
            Assert.True(ValueConverter.TryConvert("3,140", typeof(long), trim: false, out var longValue, out var longError));
            Assert.Null(longError);
            Assert.Equal(3140L, longValue);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
