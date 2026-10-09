using QuickSql.DriverLoading;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§9.5 那颗 <c>path</c> 的下半截：<b>不连库、也不要任何一份真驱动</b>就能钉住的断言。
/// 上一版在这里什么都没有 —— 清单解析的失败面只有 <c>tests/QuickSql.Tests.Integration/DriverLoadingTests.cs</c>
/// 覆盖，而那个套件要一台活 SQL Server 才跑得动，于是"配了 path 但文件不在"这一格从来没被离线钉过。
///
/// <para>v0.32 在虚谷上量到了为什么值得钉：<c>DriverRegistry.LoadAssembly</c> 原样把声明串交给
/// <c>LoadFromAssemblyPath</c>，而 <c>File.Exists</c> 对正斜杠、对 <c>..</c> 一律照收 —— 托管程序集确实加载上了，
/// 带<em>原生</em>同伴的那份 <c>xugusql.dll</c> 却按"最后一个反斜杠之前是目录"去找探测路径，
/// 于是故障一路让到第一次真正连库，报出来是一条看不出原因的 <c>DllNotFoundException</c>。
/// 同一份摆法只把分隔符换一颗，OPEN 从成功翻成失败（五格 A/B 记 §13.5）。修复是 <c>Path.GetFullPath</c> 一格。</para>
///
/// <para>本文件钉的是这条修复在离线侧唯一看得见的表现：<b>报错里回读到的那个文件路径，是归一化之后的</b>。
/// 它钉不到原生探测目录那半边 —— 那一半只有带原生同伴的驱动才量得到，所以两半各留一处见证。</para></summary>
public sealed class DriverPathTests : IDisposable
{
    const string Marker = "声明的驱动文件不存在：";

    readonly string dir = Path.Combine(Path.GetTempPath(), "quicksql-driver-path-" + Guid.NewGuid().ToString("N")[..8]);

    public DriverPathTests() => Directory.CreateDirectory(dir);

    [Fact]
    public void A_declared_path_is_resolved_before_the_failure_names_it()
    {
        // 一颗混合写法：正斜杠 + 一层 drivers/ 再退回来。三个毛病（分隔符、. 段、根本不存在的文件）
        // 挤在同一条声明里，而回读到的路径只该有归一化之后的那一个样子。
        WriteManifest("""{"key":"xugu","assembly":"XuguClient","path":"drivers/../nosuch.dll"}""");

        var failure = Record.Exception(() => DriverRegistry.Load(ManifestFile).Factory("xugu"));

        Assert.IsType<DriverNotAvailableException>(failure);

        Assert.Equal(Path.Combine(dir, "nosuch.dll"), ReportedPath(failure!.Message));
    }

    [Fact]
    public void The_declared_path_that_gets_blamed_is_the_one_the_manifest_wrote()
    {
        // 同一条消息里还得有"你本来写的是什么"：现场改清单时手里那份是声明串，不是我们算出来的绝对路径。
        WriteManifest("""{"key":"xugu","assembly":"XuguClient","path":"drivers/../nosuch.dll"}""");

        var failure = Record.Exception(() => DriverRegistry.Load(ManifestFile).Factory("xugu"));

        Checks.Has("drivers/../nosuch.dll", failure!.Message);
        Checks.Has("清单路径", failure.Message);
    }

    string ManifestFile => Path.Combine(dir, "QuickSqlDrivers.json");

    void WriteManifest(string driverLine)
        => File.WriteAllText(ManifestFile, $$"""{"drivers":[{{driverLine}}]}""");

    /// <summary>把 <c>声明的驱动文件不存在：</c> 后面那段截出来 —— 消息里同时还有原始声明串，
    /// 整条比对会被它撞车，所以判据只看这一格。</summary>
    static string ReportedPath(string message)
    {
        var start = message.IndexOf(Marker, StringComparison.Ordinal);

        Assert.True(start >= 0, $"消息里没有 {Marker}：{Environment.NewLine}{message}");

        var value = message[(start + Marker.Length)..];
        var end = value.IndexOf('；');

        return end < 0 ? value : value[..end];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不算测试失败：这颗夹具只有一张写着假路径的 JSON。
        }
    }
}
