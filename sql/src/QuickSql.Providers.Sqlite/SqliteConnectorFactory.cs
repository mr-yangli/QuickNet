using System.Data.Common;
using QuickSql.Providers;

namespace QuickSql.Providers.Sqlite;

/// <summary>连接与命令的创建（§4，第七家）。<b>类体与另六族的那六份逐字同形，而本家这一份是七家里
/// 唯一一颗"重复是纯证明"的</b>：provider 之间禁止互引（§2），所以"只有 ADO 抽象层虚调用"那一段没法共用，
/// 只能各写一份 —— 而本家对面那颗驱动是 <c>Microsoft.Data.Sqlite</c>，它与 Core 之间没有任何一家
/// 血统基类、没有原生同伴的摆法问题（见 <see cref="TryEnsureLoaded"/>），也就是说这一格的"能同形"
/// 到这里已经<em>不依赖</em>任何一族的特殊约定。<b>连接层从头到尾不需要知道对面是哪一家</b>，
/// 第七家是这条结论的第七次同样读数。</summary>
///
/// <para><b>【本家连接串那一条与另六家不同，而差在"它指向什么"而不是"它怎么写"】</b>本仓库六家的
/// 连接串写的都是"到一台服务器的一条路"（主机 ∶ 端口 ∶ 库名 ∶ 凭据），provider 拿到它是原样递给驱动；
/// 本家那一颗写的是<b>一个文件的名字</b>——第一轮的那条连接就是 <c>Data Source=&lt;目录&gt;/db/probe.sqlite</c>，
/// 到第四十二轮为止留档的每一份探针脚本开头那一条都是这一形（<c>obj/sqlite-probe2/Program*.cs.txt</c>
/// 二十一份 ∶ <c>obj/sqlite-probe/Program1.cs.txt</c> ∶ 同目录 <c>Program5.cs.txt</c>）——
/// <b>而这一句要按"探针自己的口径"读，它不是任何一颗读数的内容</b>（A0/A1 那两颗量的是工厂类型与
/// 引擎版本，跟连接串怎么写无关）。"一条串指向一个文件"这一判的正面读数在第 30 轮：把第二条路
/// <c>ATTACH</c> 进同一颗连接就多出一颗模式【实测·3000/3001】，
/// 于是 §9.5 那句"provider 不解释连接串"在这里最直白：<b>连接串就是"这次连的是哪一个库"这件事本身</b>，
/// 而它同时决定了 <see cref="SqliteCodeFirst"/> 类注释第二条那颗"模式"到底是什么（<c>main</c> 由它给，
/// 别的名字要靠 <c>ATTACH</c> 或同一串里的多库写法带进来【实测·3000/3001】）。这条线在另六家不存在：
/// 那六家的"模式"是服务器内部的对象，而这里它是连接串的一个字段所指向的东西的别名。
/// <b>所以本家的 provider 比另六家更需要"不要碰连接串"</b>：那一颗字段一换，建出来的表就落在另一个文件里，
/// 而同一条查询文本仍然绿 —— 这是 §14 那颗"连接串参数改变方言假设"缺口的本家实例，只是它改的不是字节算法
/// （虚谷那颗 <c>CHAR_SET</c>）而是<em>数据在不在</em>。</para>
public sealed class SqliteConnectorFactory(DbProviderFactory factory) : IConnectorFactory
{
    /// <summary>探测用的连接不打开、只创建。<b>这一家两层的分岔是量过的，而且是七家里第一次两层各自有读数</b>：
    /// <c>factory.CreateConnection()</c> 交回 <c>Microsoft.Data.Sqlite.SqliteConnection</c>【实测·A0b】，
    /// 它证明的是<em>托管类型</em>加载成功；真正打通到引擎的是下一条语句 —— <c>SELECT sqlite_version()</c>
    /// 交回 <c>3.46.1</c>【实测·A1】，而那颗函数活在原生库里，所以那一条读数本身就是"原生同伴到位"的见证。
    /// 与虚谷那一份的分别是<b>这里没有反例</b>：那一家量到"托管装上了而原生不在路径上时这一句照样交回
    /// <c>true</c>，缺失要到 <c>Open</c> 才以 <c>DllNotFoundException</c> 现身"（<c>XuguConnectorFactory</c>
    /// 类注释末段，出处 <c>obj/logs/v32-matrix-bare-open.log</c>）；本家的原生同伴
    /// （<c>e_sqlite3</c>）是随那颗托管程序集一起从同一份 NuGet 图落下来的，装不上是整颗装不上，
    /// 于是"这一句挡得住托管那一半、挡不住原生那一半"这一格在本家<em>没有可量的形状</em>。
    /// 方法照旧留在这里：它防的不是本家，是 Core 那条"启动期说清原因"的路（§1.2 原则 3）。</summary>
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
    /// 用工厂造出来的命令不会自动带上当前事务。本家这一颗工厂<em>确实</em>造得出无连接的命令
    /// （<c>SqliteFactory.Instance.CreateCommand()</c> 当场交回一颗 <c>SqliteCommand</c>，实测见 A0b 那一行的
    /// 后半），所以这里"只走连接那一条"在七家里是<em>最不吃约束</em>的一次：形状对，而它不是被库逼出来的。
    /// 紧接的一句<em>不是读数</em>而要留在这里：<c>DbConnection.DbProviderFactory</c> 那颗属性在 ADO 里是
    /// <c>protected</c>，从连接实例<em>问不出</em>它是谁造的 —— 那是<b>编译期的语言事实，不是运行时读数</b>，
    /// 所以本仓库不把它记在 A0b 名下（那一颗量的只有"工厂当场造得出哪两种对象"）。
    /// 于是驱动只能由外部注入，本仓库 §4 的"槽位接受注入"这一形状在本家是被语言可见性钉住的，与另六家同一条。</summary>
    public DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();

        command.CommandType = System.Data.CommandType.Text;

        return command;
    }
}
