using System.Data.Common;
using QuickSql.Providers;

namespace QuickSql.Providers.SqlServer;

/// <summary>连接与命令的创建（§4）。<b>这里没有任何 <c>SqlConnection</c>/<c>SqlClientFactory</c> 的类型名</b>：
/// 驱动实例由 Core 的 <c>DriverRegistry</c> 按 内建表 <c>BuiltinDrivers</c> 在运行期解析后注入（§1.2 原则 3），
/// 之后全部是 <see cref="DbProviderFactory"/> 的虚调用。热路径上没有反射，也没有字符串找类型。</summary>
public sealed class SqlServerConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建：驱动的原生依赖（<c>sni.dll</c> 之类）缺失时会在
    /// <see cref="DbProviderFactory.CreateConnection"/> 里抛 <see cref="TypeLoadException"/>，
    /// 这正是"装不上且报不出原因"的那一类故障，要能在启动期说清。</summary>
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
    /// 用工厂造出来的命令不会自动带上当前事务。</summary>
    public DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();

        command.CommandType = System.Data.CommandType.Text;

        return command;
    }
}
