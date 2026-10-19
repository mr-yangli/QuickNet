namespace QuickSql.DriverLoading;

/// <summary>驱动默认表：<b>程序集名与工厂类型名的唯一来源</b>。每颗 key 与 <see cref="DbKind"/> 的
/// <c>ToString().ToLowerInvariant()</c> 逐字相同，所以不需要映射表。这就是 FreeSql 那一档现场姿势 ——
/// <c>Type.GetType("FreeSql.MySql.MySqlProvider`1,FreeSql.Provider.MySql")</c> 是它核心里写死的一张按名字的表，
/// 而不是配置文件；本库的 provider 侧照 §1.7 走编译期登记，驱动侧在运行期按程序集名装载
/// （<see cref="DriverRegistry"/> 那条 <c>Assembly.Load</c>），原则 3（provider 程序集编译期不引用任何驱动）一格没动。
///
/// <para>曾经这里只是"清单没配时的退路"，第一优先是仓库根那份 <c>QuickSqlDrivers.json</c>。
/// 那一层（连同 <c>QUICKSQL_DRIVERS_CONFIG</c> 环境变量与 <c>DriverManifest</c> 解析）已整层删除：
/// 两份同构表必然漂移，而漂移钉住之后清单只剩覆盖手段一种身份，覆盖的两件事各有更直接的姿势 ——
/// 换程序集名 = 把厂商 DLL 按这里的名字丢进 <c>drivers/</c>；换工厂解析 = <c>DriverRegistry.Register</c> 注入。
/// 原清单各行 <c>$comment</c> 的出处台账已迁进本文件逐行注释，未实测口径照旧（§13.5）。</para></summary>
public static class BuiltinDrivers
{
    static readonly DriverRef[] _refs =
    [
        // factoryType 照厂商文档写全名：这一颗有两台活库实测过，全名不是猜的。
        new("sqlserver", "Microsoft.Data.SqlClient", "Microsoft.Data.SqlClient.SqlClientFactory"),

        // PG 本尊（v0.37 第二笔立行，第九笔实测）：Npgsql 是这一族线协议自己实现的驱动，名字不借不称。
        // 刻意不写 factoryType —— 省略它走「扫导出类型里第一个 DbProviderFactory 子类」兜底，它是全表第二颗
        // 靠兜底解析成功的键。第九笔改判：已在真库解析成功并跑完整本集成册（QUICKSQL_TEST_PROVIDER=postgresql，
        // 100∶0红∶14skip，obj/logs/v37-pg-int-3.log；库回包异常型名 Npgsql.PostgresException 两份日志可读，
        // 证明"兼容"不是前件）。四颗派生键（opengauss/gaussdb/highgo/gbase8c）不因本格改判，逐行读自己的注释。
        new("postgresql", "Npgsql", null),

        // 金仓 V8：两颗名字都按厂商文档写、未实测（§13.5）。factoryType 照文档写全名，而现场装不上时
        // 更稳的姿势是把 DLL 丢进 drivers/ 让它走兜底扫描 —— 猜错全名会把本来能成的解析变成"找不到类型"。
        // 兼容 Npgsql 的现场可把 DLL 按 Npgsql.dll 摆进 drivers/（独立上下文按文件名装载），provider 代码不用动。
        new("kingbase", "Kdbndp", "Kdbndp.KdbndpFactory"),

        // openGauss（v0.36 第二笔）：assembly 的来路是金仓行那句「兼容 Npgsql」—— 官方分发的是 Npgsql 的
        // fork（§9.1 第四列），fork 的托管程序集名没有厂商读数，不猜名字（达梦行立过的纪律），用两条真实
        // 存在的路里与血统同源的那一条。从未解析成功过、刻意不写 factoryType、整行未实测，本机没有实例。
        // 现场真装上那天，第一件事是按厂商读数改回它自己的程序集名，而不是把本行读成"已验证"。
        new("opengauss", "Npgsql", null),

        // GaussDB（v0.36 第三笔）：PG 血统第一次出现「两颗键、同一份 assembly」。比 openGauss 还薄一层：
        // GaussDB 自己分发的托管驱动叫什么，本仓库没有任何可引读数 —— 连"厂商分发的是 Npgsql 的 fork"
        // 那句都只写在 §9.1 的 openGauss 半边，本行不替它引申。处境全文继承：未解析、未实测、无本机实例。
        new("gaussdb", "Npgsql", null),

        // 瀚高 HighGo（v0.36 第四笔）：assembly 的来路是 §9.1 行 907 第四列那三个字「兼容 PG」——
        // 线协议就是 PG 的，Npgsql 是血统协议自己的驱动，不借邻家的臂。PG 血统到此凑齐三颗键同一份
        // assembly，但来路两种（借兼容臂 2 ∶ 直称 primary 1）的差别不抹平。未解析、未实测、无本机实例。
        new("highgo", "Npgsql", null),

        // GBase 8c（v0.36 第六笔）：与瀚高共享同一颗来路原词（「兼容 PG」，两行第四列逐字相同）。
        // PG 血统凑齐四颗键同一份 assembly，来路 2∶2（借臂 ∶ 直称），"凑齐四颗"不等于"四颗同质"。
        // GBase 自己分发的托管驱动叫什么没有可引读数，不猜名字。未解析、未实测、无本机实例。
        new("gbase8c", "Npgsql", null),

        // Oracle 本尊（v0.37 第三笔）：assembly 的来路是全表第四种、也是最薄的一种 —— 既不是厂商文档口径，
        // 也不是邻家兼容臂，更不是协议自证，而是本仓库自家注释里的一句话（OracleProviderBase 类注释
        // 「本程序集编译期不认识 Oracle.ManagedDataAccess」），现场不能拿它当厂商读数用。刻意不写
        // factoryType，且它连一颗有出处的工厂类名都没有。Oracle 血统三颗键凑齐三份互不相同的 assembly
        // （本行 ∶ DmProvider ∶ Oscar），同族之内驱动按厂商各发一份在本组完整成立、在 PG 组完全不成立。
        // 未解析、未实测、无本机实例；NuGet 侧 Oracle.ManagedDataAccess.Core 实测程序集名与本行逐字相同。
        new("oracle", "Oracle.ManagedDataAccess", null),

        // 达梦：只有 assembly 这一颗名字是按厂商文档写的，从未在一台机器上解析成功过（§13.5 整行未实测）。
        // 刻意不写 factoryType —— 按文档猜全名反而会把本来能成的解析变成"找不到类型"，省略它走兜底扫描。
        // 现场把达梦那份 ADO.NET 驱动丢进 <输出目录>/drivers/DmProvider.dll 即可。NuGet 的 DmProvider 2.0.0
        // 在 net10.0 上是实测死路（包内只有 lib/Dm.dll，程序集名对不上，NU1701），正解是厂商 .NET 版驱动走路 B。
        new("dameng", "DmProvider", null),

        // 神通 Oscar（v0.36 第五笔）：Oracle 血统第二颗键。assembly 的来路与金仓/达梦同一档 —— §9.1 行 902
        // 第四列全文就这一个词，是厂商文档口径而不是任何现场读数；且它连"是不是程序集名"都只有那一列支持。
        // 未解析、刻意不写 factoryType、未实测、无本机实例。
        new("shentong", "Oscar", null),

        // MySQL 血统：assembly 写 MySqlConnector（社区驱动；这一家官方分发的是 MySQL.Data，两条不同源）。
        // v0.37 第九笔改判：已解析成功（交回的事务对象是 MySqlConnector.MySqlTransaction，实测·T-01，
        // obj/logs/v37-my-recon-3.log）并跑完整本集成册（QUICKSQL_TEST_PROVIDER=mysql，100∶0红∶14skip，
        // obj/logs/v37-my-int-3.log），是全表第一颗靠"扫导出类型"兜底真跑通的键 —— 达梦/金仓立这条纪律时
        // 没有任何一家走过它。现场读到的是 MySQL Community Server 8.4.8（实测·S-01∶S-02），与厂商那份
        // MySQL.Data 的差别没有任何读数。tidb/oceanbase 两颗同 assembly 的键不因本格改判。
        new("mysql", "MySqlConnector", null),

        // TiDB（v0.35 第三笔）：全表第一次出现「两颗键、同一份 assembly」—— 驱动按协议族装、库种按方言
        // 假设装，解开的那一格由 provider 自己站死（§9.1 TiDB 行）。处境：从未解析成功过、不写 factoryType
        // 走兜底、整行未实测，本机没有任何一台 TiDB。mysql 行的实测推不到本行 —— 同一份 assembly 在两台
        // 库种之间可迁移这件事只证到了 mysql 自己那一台。
        new("tidb", "MySqlConnector", null),

        // OceanBase（MySQL 模，v0.35 第四笔）：「三颗键、同一份 assembly」的第三个现场。比 tidb 多一层
        // 本表表达不了的悬空：这一家是双模、模式在连接上决定（§9.1），本行 assembly 只在 MySQL 模成立 ——
        // Oracle 模该走的是另一颗今天不存在的键（缺口记 §14，与金仓兼容模式同一行）。mysql 行本笔的实测
        // 是 MySQL 官方 8.4.8 上的读数，推不到这一家自己的 MySQL 模实现。未解析、未实测、无本机实例。
        new("oceanbase", "MySqlConnector", null),

        // 虚谷（v0.32 改判）：assembly 与厂商文档给的驱动包名逐字同名已证 —— FullName = XuguClient,
        // Version=1.0.0.0，工厂解析到 XuguClient.XGProviderFactory（厂商参考页写 XGProviderFactory.Instance，
        // 实测是 public static 字段且就是工厂本身，兜底扫描 FACTORIES=1），六格摆法五格走到"打开+只读查询
        // 成功"（obj/logs/v32-matrix-*-open.log，gitignored）。重跑：先往环境摆 XUGU_HOST/PORT/DB/USER/PASS
        // 五颗变量（口令只走环境，脚本与日志都不落它），再 powershell -NoProfile -File
        // obj/probe-xugu-shipped/run.ps1 -Case probing -Open -Log obj/logs/v32-matrix-probing-open.log。
        // §13.5 台账这一行
        // 仍是「未实测」且不许被改判翻掉：解析与连通已见证，本程序集产出的文本一条都没在那台库上跑过。
        // 这一族比别家多一层失败面：要一份原生同伴（xugusql.dll / libxugusql.so），托管加载成功不等于能用 ——
        // 原生同伴缺失时 TryEnsureLoaded 照样返回 true，到 Open 才抛 DllNotFoundException（0x8007007E，bare 反证）。
        // 现场摆法实测可用：两颗 DLL 一起丢进 <输出目录>/drivers/，不写 path（显式路径那条声明入口已随清单层删除，
        // v0.32 那颗正斜杠归一化坑随之消失 —— 探测名由代码拼接，分隔符不再经过任何人的手）。
        new("xugu", "XuguClient", null),

        // GBase 8s（v0.37 第一笔）：Informix 血统第一颗键，也是全表唯一一颗「程序集名只有一处出处、
        // 而那处不是文档」的 —— GBase.DataDB 的全部来路是 §9.1 行 906 第四列；没有厂商文档、没有 shipped
        // 适配器、没有活库，也没有可借的臂（§9.1 第六列写这一族「独立」，Informix 官方托管驱动叫什么
        // 同样没有读数）。provider 层每格标【文档·IBM】而不是【文档·厂商】："Informix 的即 GBase 8s 的"
        // 是一颗借来的前提。未解析、刻意不写 factoryType（连一颗候选工厂类名都没有，兜底比别行更无从判断）、
        // 未实测、无本机实例。现场装上那天，改名字之前还有一步前置：先证那颗等价关系成立到什么程度（缺口记 §14）。
        new("gbase8s", "GBase.DataDB", null),

        // SQLite：全表第一颗「factoryType 不是照厂商文档写的、而是从活机器上读回来的」键 —— restore 到手
        // FullName = Microsoft.Data.Sqlite 9.0.0.0，导出类型里恰好一颗 DbProviderFactory 子类（SqliteFactory），
        // 引擎 sqlite_version() = 3.46.1（四颗读数记 §13.5 第十五台账）。也是第一颗不需要任何现场摆法的
        // 非 T-SQL 行：不要凭据、一个文件就是一座库 —— 这是访问面的差别而不是纪律的差别，不给任何一家开先例。
        // v0.37 第四笔集成册绿过一次（QUICKSQL_TEST_PROVIDER=sqlite，95∶0红∶14跳，obj/logs/v37-sqlite-int-4.log），
        // 跑出去的是 QueryEngine 与 InsertEngine 自己拼的语句；CodeFirst 的产物在本仓库没有执行路径，
        // 所以不写「每一格都实测过」，逐位档位记台账不记在这里。
        new("sqlite", "Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite.SqliteFactory"),

        // DuckDB（本笔：DuckDB 落笔）：PG 血统的第七颗叶子，也是内嵌单文件引擎这一档的第二颗键 ——
        // "库"就是连接串里那颗文件，不要凭据、也没有"建库"这条路（provider 那侧 CanCreateDatabase=false，
        // 血统的 EnsureDatabaseExistsAsync 在本家整格无害可走）。与 sqlite 行同判而方向相反：sqlite 那颗
        // factoryType 是从活机器上读回来的，本颗与金仓/达梦同档 —— assembly 名按厂商文档口径写
        // （DuckDB.NET.Data，官方 ADO.NET 驱动，NuGet 包按原生伴件分 Full 档），factoryType 刻意不写
        // 走兜底扫描。这个名字从未在任何机器上解析成功过，本机没有引擎，整行未实测（§13.5 台账逐位记）。
        new("duckdb", "DuckDB.NET.Data", null),
    ];

    public static IReadOnlyList<DriverRef> Drivers => _refs;

    public static DriverRef? Find(string key)
    {
        foreach (var reference in _refs)
            if (string.Equals(reference.Key, key, StringComparison.OrdinalIgnoreCase))
                return reference;

        return null;
    }
}
