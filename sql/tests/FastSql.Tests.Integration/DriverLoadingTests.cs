using FastSql.DriverLoading;
using FastSql.Providers.SqlServer;
using FastSql.Query;
using FastSql.TestInfra;
using Xunit;

namespace FastSql.Tests.Integration;

/// <summary>§1.2 原则 3 与 §9.5 的第一次真跑：清单 → 独立 <c>AssemblyLoadContext</c> →
/// <see cref="System.Data.Common.DbProviderFactory"/>，然后由它连上库。
///
/// <b>这里没有任何 <c>new SqlClientFactory()</c></b>：那样会把要验的东西整个绕过去，
/// 于是"驱动能装上"这件事在 P0 一次也没被证明过，只能等到只有私有源驱动的 P3 才第一次运行
/// —— 那是 §14 代价表里点名的坑。</summary>
public class DriverLoadingTests
{
    /// <summary>一个刻意谁也不会去配的键，只属于下面那条"清单里没有"的用例。</summary>
    const string UnlistedKey = "nosuchprovider";

    [Fact]
    public void Manifest_in_the_output_folder_is_what_resolves_the_factory()
    {
        // 清单没复制到输出目录时 DriverManifest.Load() 静默返回 Empty，本文件的所有断言就会变成
        // "驱动不存在"这种看不出原因的红 —— 所以先把这份文件本身钉住。
        Assert.True(File.Exists(DriverManifest.DefaultPath),
            $"输出目录里没有 FastSqlDrivers.json（{DriverManifest.DefaultPath}），运行期解析链无从谈起。");

        var manifest = DriverManifest.Load();
        var reference = manifest.Find(SqlServerProvider.DriverKey);

        Assert.NotNull(reference);
        Assert.Equal("Microsoft.Data.SqlClient", reference!.AssemblyName);

        // path 为空 = 走默认探测路径：公开包本来就在依赖图里，复制进独立上下文只会让同一类型出现两份。
        Assert.Null(reference.Path);
    }

    [Fact]
    public void Resolved_factory_is_the_driver_the_queries_run_on()
    {
        var factory = SqlServer.DriverFromManifest();

        // 按类型名断言，而不是 is SqlClientFactory：后者要编译期引用驱动包，而 §1.2 原则 3 要验的
        // 恰恰是"没有任何一处需要那个引用"。
        Assert.Equal("Microsoft.Data.SqlClient.SqlClientFactory", factory.GetType().FullName);

        // 探测走 provider 自己那一条（只 CreateConnection，不 Open）：驱动装上了但原生依赖缺失
        // 会变成 TypeLoadException，那正是"装不上且报不出原因"的故障，要在启动期就报出来。
        Assert.True(SqlServer.Provider(factory).Connectors.TryEnsureLoaded(out var failure), failure);
    }

    [Fact]
    public void Unlisted_key_names_the_manifest_and_the_shape_to_add()
    {
        var registry = DriverRegistry.Load();

        // 一个清单里永远不会有的键：v0.22 之前这里用的是 "kingbase"，而那一版把金仓写进了清单，
        // 于是那个键的含义从"没配"变成了"配了但装不上"——两件事各有各的修法，所以各占一条用例。
        var failure = Record.Exception(() => registry.Factory(UnlistedKey));

        Assert.IsType<DriverNotAvailableException>(failure);

        var message = failure!.Message;

        Assert.Contains("FastSqlDrivers.json", message, StringComparison.Ordinal);
        Assert.Contains(UnlistedKey, message, StringComparison.Ordinal);
        // 清单里没有时，消息要能把该补的那一项原样写出来，而不是只说"不存在"。
        Assert.Contains("\"assembly\"", message, StringComparison.Ordinal);

        // TryFactory 的三元形态：失败原因走字符串而不是异常，给 doctor 这类"把所有驱动都问一遍"的入口用。
        Assert.False(registry.TryFactory(UnlistedKey, out var factory, out var reason));
        Assert.Null(factory);
        Assert.Equal(message, reason);
    }

    /// <summary>清单里有、程序集不在：信创现场最常见的故障（忘了丢 DLL、名字写错、私有源没同步）。
    /// 这一条钉的是"报得清"而不是"装得上"—— 金仓驱动在本仓库的机器上从来没有装上过，
    /// 所以 <c>Kdbndp</c> 这个程序集名本身仍是 §13.5 台账里那一行「未实测」。</summary>
    [Fact]
    public void A_listed_but_uninstalled_driver_reports_the_assembly_it_tried()
    {
        var registry = DriverRegistry.Load();

        var failure = Record.Exception(() => registry.Factory("kingbase"));

        Assert.IsType<DriverNotAvailableException>(failure);

        var message = failure!.Message;

        Assert.Contains("Kdbndp", message, StringComparison.Ordinal);
        Assert.Contains("清单路径", message, StringComparison.Ordinal);

        // 同一条失败要能被 doctor 这类入口一次问完：重取一次不重走加载路径，也不换一条堆栈。
        var second = Record.Exception(() => registry.Factory("kingbase"));

        Assert.Equal(message, second!.Message);
    }

    /// <summary>方言侧同样不认识驱动：清单解析出来的工厂装好之后，<c>ToSql()</c> 这条路
    /// 仍然一次驱动调用都不发生（§3）。桩工厂那半边在方言套件里已经跑过，这里是另一半。</summary>
    [Fact]
    public void Generated_sql_never_touches_a_driver_even_when_one_is_installed()
    {
        // 占位串而不是配置串：这条用例断言的正是"一次连接都不发生"，那它就不该需要活库。
        // （没配置时它照样过，就是这句话的证法。）
        var orm = SqlServer.Orm(SqlServer.DriverFromManifest(), SqlServer.OfflineConnectionString);

        var query = orm.Select<Order>()
            .Where(o => o.Note == Database.Tag)
            .OrderBy(o => o.Id)
            .Skip(2)
            .Take(2);

        var sql = query.ToSql();

        Assert.Contains("OFFSET", sql, StringComparison.Ordinal);
        Assert.Contains("@p", sql, StringComparison.Ordinal);

        var log = query.ToLogSql();

        // 内联之后不该再有占位符，而中文/文本字面量必须带 N 前缀（§10.4）。
        Assert.DoesNotContain("@p", log, StringComparison.Ordinal);
        Assert.Contains("N'fastsql-p0'", log, StringComparison.Ordinal);
    }
}
