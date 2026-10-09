using System.Data.Common;
using QuickSql.Providers;

namespace QuickSql.Providers.Xugu;

/// <summary>连接与命令的创建（§4）。<b>类体与另四族的那四份逐字同形，而且这是故意的重复</b>：
/// provider 之间禁止互引（§2），所以"只有 ADO 抽象层虚调用"这一段没法共用，只能各写一份 ——
/// 它同时也是一个证据，而且到今天已经是第五份：<b>连接层从头到尾不需要知道对面是哪一家</b>。
/// 这一格在第五家身上还多一条可说的：<c>DialectBase</c> 与 <c>QuickSql.Providers</c> 那一层可以复用，
/// 而"复用"在这一族压根不是问题（它没有血统基类，§9.1 那份清单里没有第二种挂在这家底下的库种），
/// 所以这一份重复是纯粹由 §2 那条禁令造成的，不是分层没做到位。
///
/// <para>驱动实例由 Core 的 <c>DriverRegistry</c> 按 <c>QuickSqlDrivers.json</c> 在运行期解析后注入
/// （§1.2 原则 3），热路径上没有反射，也没有字符串找类型。这一族的驱动比另四家多一个失败面：
/// 它带一份<em>原生</em>同伴（<c>xugusql.dll</c> / <c>libxugusql.so</c>），所以"托管程序集加载成功"
/// 不等于"能用"—— 下面那颗 <see cref="TryEnsureLoaded"/> 在这一家比在另四家更该被调用。
/// <b>而"更该被调用"这一句在 v0.32 只兑现了一半，原文照抄在这里：「清单解析成功而原生依赖缺失时，
/// 只有这一句能报出原因」</b> —— 那颗库真到手、真接上以后量到的是：原生同伴不在探测路径上时，
/// <c>CreateConnection</c> 照样成功、<see cref="TryEnsureLoaded"/> 照样交回 <c>true</c>，
/// 缺失到 <c>Open</c> 那一步才以 <c>DllNotFoundException</c>（0x8007007E）现身。
/// 这一颗反证的出处是 <c>obj/logs/v32-matrix-bare-open.log</c>，机制与摆法记 §9.5。</para></summary>
public sealed class XuguConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建：驱动<em>托管</em>类型加载不了时会在 <see cref="DbProviderFactory.CreateConnection"/>
    /// 里抛 <see cref="TypeLoadException"/>，这正是"装不上且报不出原因"的那一类故障，要能在启动期说清。
    /// <b>这一家它是必然会被走到的一格</b>（类注释末段：托管 + 原生两份）—— 但"必然会被走到"不等于"必然报得出"：
    /// v0.32 的 bare 那颗反证量到的是原生依赖缺失时这里<em>不</em>抛，它只挡得住托管那一半，
    /// 原生那一半要到 <c>Open</c> 才响（同一格写在上面类注释里，出处 <c>obj/logs/v32-matrix-bare-open.log</c>）。</summary>
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
