using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using QuickWord;
using Xunit;

namespace QuickWord.Tests;

/// <summary>2026-10-17 审查第一档数行级修复的门禁：元素文本的快路径必须把 XML 1.0 非法控制字符
/// 也算进“要走慢路”的那一集合。修前 <see cref="Internal.XmlUtility.Escape"/> 只探 <c>&amp; &lt; &gt;</c>,
/// <c>"\u0001abc"</c> 这种值原样漏出、进 <c>&lt;w:t&gt;</c> 落成非法 XML 1.0 —— Word/WPS 会把整个
/// 包报成损坏。丢控制字符的循环（\t \n \r 三颗除外）对这类值本来不可达，所以牙要钉在“零实体字符
/// 的控制字符也被清掉”这一格上。</summary>
public class PackageGuardTests
{
    private record GuardRow(string Name, string Memo, string Clean);

    private static readonly GuardRow[] Rows =
    [
        new("实体档&AB<C>",        "清洗档\u0001\u000B\u001F脏",   "无脏字符\t照常"),
        new("换行档",               "慢路\u0001共脏&清洗",           "快慢两路\n各验一遍"),
    ];

    [Fact]
    public void A_value_with_xml10_control_characters_keeps_the_package_parseable()
    {
        var bytes = Word.GetAsByteArray(Rows);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        Assert.NotNull(entry);

        var xml = ReadAll(entry);

        // 修前这一行抛 XmlException：含 \u0001 的值越过快路径原样落进 <w:t>。
        var doc = XDocument.Parse(xml);

        // 控制字符确实被丢掉了（而不是被转义成别的形状藏进包里）。
        Assert.False(xml.Contains("\u0001"), "the exported part still carries U+0001");
        Assert.False(xml.Contains("\u000B"), "the exported part still carries U+000B");
        Assert.False(xml.Contains("\u001F"), "the exported part still carries U+001F");

        // 三个合法控制字符与实体字符照旧。
        Assert.Contains("无脏字符\t照常", xml);
        Assert.Contains("实体档&amp;AB&lt;C&gt;", xml);

        // 丢掉控制字符之后剩下的值在文本位上原样可读。
        var texts = doc.Descendants(W + "t").Select(t => t.Value).ToList();
        Assert.Contains("清洗档脏", texts);
        Assert.Contains("慢路共脏&清洗", texts);
    }

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
