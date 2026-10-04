using FastSql.Providers;
using FastSql.TestInfra;
using Xunit;

namespace FastSql.Tests.Dialects;

/// <summary>§4 一行注册的登记面。这一册不测方言文本，测的是"生成器有没有跑起来 + 表能不能读"：
/// <see cref="ProviderRegistry"/> 里应当有 16 颗 <see cref="DbKind"/>，与 §5 那张 provider 总表
/// 一颗不差；<see cref="FastSqlOrm.Create(DbKind,string,System.Data.Common.DbProviderFactory)"/>
/// 出来的 <see cref="FastSqlOrm"/> 应当带对的 <see cref="IDbProvider.Kind"/>。</summary>
/// <remarks>触发链是 <c>FastSql.Tests.Common.csproj</c> 把 <c>FastSql.Generators</c> 以
/// <c>OutputItemType="Analyzer"</c> 挂进去、并引用了 15 颗 provider 工程，所以本册拿到的登记表
/// 由 <c>FastSql.Tests.Common</c> 那一份 <c>[ModuleInitializer]</c> 一次性铺好。测试代码里
/// 必须先摸一次 <c>FastSql.TestInfra.SqlServer</c>（同汇编类型），module initializer 才会跑。
/// 第十六颗 <see cref="DbKind.Sqlite"/> 走同一份触发链，只是 provider 工程 <c>FastSql.Providers.Sqlite</c>
/// 在 <c>Common.csproj</c> 里是普通引用；<b>这一句在本册里不是可省的字</b>——若哪天 <c>Sqlite</c>
/// 从 <c>Common</c> 里被摘出去，本册第一次跑就会红在"Registered 少一颗"这一格而不是悄悄少测一家。</remarks>
public class ProviderRegistrationTests
{
    /// <summary>把 <c>FastSql.Tests.Common</c> 那一份程序集摸热：读同汇编的一颗静态属性即可，
    /// CLR 的模块初始化契约保证"取静态字段/属性"这一族动作之前会跑一次 <c>ModuleInitializer</c>。
    /// <b>只写 <c>typeof(SqlServer)</c> 不够</b>——类型句柄不触发 initializer，本册第一次跑就踩过这个坑。
    /// 少了这一句，<see cref="ProviderRegistry.Registered"/> 会是空集，测试红的其实是"初始化没跑"，
    /// 不是"表没铺"。</summary>
    static void TouchCommonAssemblyModuleInitializer() => _ = SqlServer.NoDriverFactory.Instance;

    [Fact]
    public void Registry_HasAllSixteenKinds()
    {
        TouchCommonAssemblyModuleInitializer();

        var expected = new[]
        {
            DbKind.SqlServer, DbKind.MySql, DbKind.Oracle, DbKind.PostgreSql,
            DbKind.Kingbase, DbKind.OpenGauss, DbKind.GaussDb, DbKind.Highgo,
            DbKind.Dameng, DbKind.ShenTong, DbKind.Xugu, DbKind.Gbase8s,
            DbKind.Gbase8c, DbKind.TiDb, DbKind.OceanBase, DbKind.Sqlite,
        };

        foreach (var kind in expected)
            Assert.True(ProviderRegistry.Contains(kind), $"DbKind.{kind} 未登记（生成器没跑或 provider 没引用）");

        Assert.Equal(16, expected.Length);
        Assert.Equal(16, ProviderRegistry.Registered.Distinct().Count());
    }

    [Fact]
    public void Create_ReturnsProviderForKind()
    {
        TouchCommonAssemblyModuleInitializer();

        var orm = FastSqlOrm.Create(DbKind.MySql, "Server=offline;Database=never;Integrated Security=True",
            SqlServer.NoDriverFactory.Instance);

        Assert.Equal(DbKind.MySql, orm.Provider.Kind);
        Assert.Equal("mysql", ProviderRegistry.DriverKeyOf(DbKind.MySql));
    }

    [Fact]
    public void Create_ThrowsForUnknownKind_WhenKindNotRegistered()
    {
        TouchCommonAssemblyModuleInitializer();

        // 用一颗不存在的整数当 DbKind 走"未登记"那一支——今天值域是 0..15（第六笔压平后连续），
        // 99 落在这个范围之外，等价于"注册表里没这颗"，抛的是 InvalidOperationException 而不是 DriverNotAvailable。
        var unregistered = (DbKind) 99;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FastSqlOrm.Create(unregistered, "x", SqlServer.NoDriverFactory.Instance));
        Assert.Contains("未在 ProviderRegistry 中登记", ex.Message);
    }
}
