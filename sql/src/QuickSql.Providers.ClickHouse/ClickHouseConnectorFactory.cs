using System.Data.Common;
using QuickSql.Providers;

namespace QuickSql.Providers.ClickHouse;

/// <summary>连接与命令的创建（§4，第十族的单层落地）。<b>类体与 GBase 8s 那一份逐字同形，而且这是故意的重复</b>：
/// provider 之间禁止互引（§2），所以"只有 ADO.NET 抽象层的虚调用"这一段没法共用，只能各写一份。
/// 单层 provider 这一档的拷贝计数到这里升到四位（SqlServer、Xugu、Gbase8s 与本家）——
/// "连接层从头到尾不需要知道对面是哪一家"这一句的核形式仍然是一行 grep：
/// <c>grep -c "factory.CreateConnection" sql/src/QuickSql.Providers.*/*.cs</c> 数得出来的份数，
/// 就是同形拷贝的份数。
///
/// <para>驱动实例由 Core 的 <c>DriverRegistry</c> 按 内建表 <c>BuiltinDrivers</c> 在运行期解析后注入
/// （§1.2 原则 3），热路径上没有反射。本族的程序集名 <c>ClickHouse.Client</c> 按厂商文档口径写
///【文档·ClickHouse：官方 .NET 客户端页，https://clickhouse.com/docs/integrations/language-clients/dotnet/ ；
/// 装配名那颗与 <see cref="ClickHouseProvider.DriverKey"/> 那一行同源写入 <c>BuiltinDrivers</c>】，
/// <b>【实测·2026-10-20】这一行在本机第一次解析成功</b>：NuGet 拉包（当时一手 7.14.0）后由
/// "扫导出类型里第一个 <c>DbProviderFactory</c> 子类"那条兜底收走
/// <c>ClickHouse.Client.ADO.ClickHouseConnectionFactory</c>（7.x 把工厂从 5.x 时代的
/// <c>ClickHouseClientFactory</c> 更了名，而导出类型里 DbProviderFactory 子类只有它一颗，兜底扫描
/// 因此对改名不敏感 —— 内建表里不写 <c>factoryType</c> 的取舍当场兑现了一次）。</para>
///
/// <para><b>本族没有原生同伴这一档已成实测</b>：那一包纯托管、装上即连，没有 native 依赖
///【实测·2026-10-20，手工探针连 26.9.21.9 成功，无须任何拷 dll 的 csproj Target】—— 与官方
/// managed 客户端的文档表述一致，那个【推断】就此结。</para></summary>
public sealed class ClickHouseConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建：驱动<em>托管</em>类型加载不了时会在 <c>CreateConnection</c>
    /// 里抛 <see cref="TypeLoadException"/>，那是"装不上且报不出原因"这一类故障唯一的启动期入口。
    /// 这一格与 GBase 8s 逐字同形，本家的读出没有另外的分岔。</summary>
    public bool TryEnsureLoaded(out string? failure)
    {
        try
        {
            using var probe = factory.CreateConnection();

            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    public DbConnection CreateConnection(string connectionString)
    {
        var connection = factory.CreateConnection() ?? throw new QuickSqlException($"{factory.GetType().Name} 未实现 CreateConnection");

        connection.ConnectionString = connectionString;

        return connection;
    }

    /// <summary>命令必须由<b>连接</b>创建而不是工厂：连接池与 <c>Transaction</c> 的归属由连接决定，
    /// 用工厂造出来的命令不会自动带上当前事务（与另几族同一条理由）。<b>本家的事务档不存在</b>
    ///（<see cref="ClickHouseProvider"/> 不声明 <c>DbCapabilities.Transactions</c>），所以这一格的
    /// "当前事务"在公开路径上恒为空 —— 那格"没有"是诚实条款，不是缺席实现。</summary>
    public DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();

        command.CommandType = System.Data.CommandType.Text;

        return command;
    }
}
