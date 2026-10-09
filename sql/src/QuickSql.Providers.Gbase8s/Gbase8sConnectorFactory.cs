using System.Data.Common;
using QuickSql.Providers;

namespace QuickSql.Providers.Gbase8s;

/// <summary>连接与命令的创建（§4，第六族）。<b>类体与另五族的那五份逐字同形，而且这是故意的重复</b>：
/// provider 之间禁止互引（§2），所以"只有 ADO.NET 抽象层的虚调用"这一段没法共用，只能各写一份。
///
/// <para><b>这一份重复在第六家身上有了一件新的事实</b>：前五位里 <c>SqlServer</c> 与 <c>Xugu</c> 也是单层
/// provider，它们的连接器同样只由 §2 那条禁令解释；这一家把同一句"这一份拷贝不是分层没做到位"
/// 说到第三遍，而第三遍的价值不在论证而在分布 —— 六份同形拷贝里已经有三份出自<em>没有基类可挂</em>的族，
/// 也就是说"连接层从头到尾不需要知道对面是哪一家"这一句今天的证据里，一半来自血统层、一半来自单层。
/// 那句判据的可核形式仍然是一行 grep：<c>grep -c "factory.CreateConnection" src/QuickSql.Providers.*/*.cs</c>
/// 数得到几份，就是几份同形拷贝。</para>
///
/// <para>驱动实例由 Core 的 <c>DriverRegistry</c> 按 <c>QuickSqlDrivers.json</c> 在运行期解析后注入
/// （§1.2 原则 3），热路径上没有反射。这一族的程序集名 <c>GBase.DataDB</c> 只来自 §9.1 那一行第四列，
/// <b>从未在一台机器上解析成功过</b>，清单里也刻意不写 <c>factoryType</c>（理由与达梦、神通那两行逐字相同：
/// 省略它走"扫导出类型里第一个 <c>DbProviderFactory</c> 子类"那条兜底，比猜一个全名更容易成）。
/// 它比另五家还薄一格的处境要说死：这一族连<em>托管程序集名</em>都只有 §9.1 那一列支持，
/// 而 <c>XuguClient</c> 那一格在 v0.32 已经被实测翻成"同名"（§9.5）—— 所以本行今天没有那半格旁证。</para>
///
/// <para><b>本族没有原生同伴这一档已知事实，也没有反向的已知</b>：<c>XuguConnectorFactory</c> 类注释里那句
/// "托管加载成功不等于能用"的教训（原生库缺失要到 <c>Open</c> 才以 <c>DllNotFoundException</c> 现身）
/// 在这一家<em>不能</em>反过来写成"没有原生依赖"——IBM 的 Informix .NET 提供者（<c>IfxDateTime</c> 那一族类型）
///  historically 是要 <c>ifxglu.dll</c> 的【文档·IBM，本次未逐页回读】，而 <c>GBase.DataDB</c> 那份是它的移植还是
/// 重写，本仓库没有任何读数。下面那颗 <see cref="TryEnsureLoaded"/> 因此在这一家既不许被当成"够用了"，
/// 也不许被当成"必然挡不住"，它只是一个探测口。</para></summary>
public sealed class Gbase8sConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建：驱动<em>托管</em>类型加载不了时会在 <c>CreateConnection</c>
    /// 里抛 <see cref="TypeLoadException"/>，那是"装不上且报不出原因"这一类故障唯一的启动期入口。
    /// 这一格在六家里的读法见类注释末段。</summary>
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
    /// 用工厂造出来的命令不会自动带上当前事务（与另五族同一条理由，第六份）。</summary>
    public DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();

        command.CommandType = System.Data.CommandType.Text;

        return command;
    }
}
