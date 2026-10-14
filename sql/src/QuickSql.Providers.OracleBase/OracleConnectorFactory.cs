using System.Data.Common;
using QuickSql.Providers;

namespace QuickSql.Providers.OracleBase;

/// <summary>连接与命令的创建（§4）。<b>类体与另两族的那两份同形，而且这是故意的重复</b>：
/// provider 之间禁止互引（§2），所以"只有 ADO 抽象层虚调用"这一段没法共用，只能各写一份 ——
/// 它同时也是一个证据：这一层没有任何一处需要知道对面是哪一家，包括"这一家有 OUT 绑定吗"
/// 那一格（<see cref="OracleKeys"/> 要的正是这里给不出的东西）。
/// <b>那句"两份"到今天是一份第五份的来路</b>：v0.30 的第四家与 v0.31 的第五家各自又抄了同一段
/// （五份同形，<c>grep -c "TryEnsureLoaded" 数得到</c>），而第五家在这段重复上多留了一枚指纹：
/// 它的驱动带一份<em>原生</em>同伴（<c>xugusql.dll</c> / <c>libxugusql.so</c>），
/// 所以"托管程序集加载成功"在那一家不等于"能用"—— 同一段代码在五家里的<em>用途</em>权重变了，
/// 形状没变（见 <c>XuguConnectorFactory</c> 类注释）。
/// 驱动实例由 Core 的 <c>DriverRegistry</c> 按 内建表 <c>BuiltinDrivers</c> 在运行期解析后注入（§1.2 原则 3），
/// 热路径上没有反射，也没有字符串找类型。</summary>
public sealed class OracleConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建：这一族的驱动（ODP.NET / 达梦的那一份）在原生客户端库缺失时
    /// 会在 <see cref="DbProviderFactory.CreateConnection"/> 里抛 <see cref="TypeLoadException"/>，
    /// 而这一家比另两族更容易撞上这一格 —— 它的驱动常带一层非托管依赖。
    /// <b>v0.31 的第五家把这一格从"更容易"顶成"必然会被走到"</b>：它同样带一份原生同伴
    /// （<c>xugusql.dll</c> / <c>libxugusql.so</c>），而那一家说这是启动期<em>唯一</em>能报出原因的入口
    /// （<c>XuguConnectorFactory.TryEnsureLoaded</c> 的注释）。<b>这一格五家都有</b>：五个
    /// <c>*ConnectorFactory.cs</c> 里 5 个文件全命中（本文件 2 处，其余各 1 处），T-SQL 那一处甚至点了
    /// <c>sni.dll</c>。所以"这一家比另两家更容易"那句从来不是"别家不需要这一格"，而是
    /// <b>两族把原生同伴的文件名写进了注释，三族只写了"原生依赖"四个字</b> —— 差别在写没写，不在有没有。</summary>
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
