using System.Data.Common;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>PG 系的驱动槽位（§4 的能力继承链：<c>PostgreSqlBase</c> 那一档）。
/// 金仓/openGauss/瀚高共享这一份，各自只覆写真正分岔的那几处。
///
/// <b>构造期不反射</b>：<see cref="DbProviderFactory"/> 由 Core 的 <c>DriverRegistry</c> 按
/// 内建表 <c>BuiltinDrivers</c> 解析后注入，所以本程序集编译期不认识 <c>Npgsql</c> 也不认识 <c>Kdbndp</c>
/// （§1.2 原则 3）。
///
/// 各槽位共用同一批实例（与 <c>SqlServerProvider</c> 同一条纪律）：<c>Expressions.Register</c> 的覆写
/// 必须同时对翻译器和方言生效，各 new 一份就会出现"注册了却不改行为"的幽灵 bug。
///
/// <b>能力位的出处</b>：§4 原文给这一档的清单是 <c>Paging_LimitOffset | Returning | Upsert_OnConflict | Cte |
/// WindowFunctions</c>，这里在它之上补齐"代码今天真的走到"的那几项。三条刻意<b>不</b>声明的：
/// <list type="bullet">
/// <item><c>MultiStatement</c>：§9.4 点名的就是这一家 —— 一次发送里塞 <c>;</c> 分隔的多条语句在扩展协议下不合法，
/// 于是 <c>ExecuteAffrows</c> 的分批策略必须由能力位决定而不是假设能拼。</item>
/// <item><c>MultiResultSets</c>：这一族的多结果集只来自"一次发送多条语句"，刚关掉那一条就不好再声明这一条。</item>
/// <item><c>Guid_Char36</c>：§4 那句"Kingbase 覆写 <c>Guid_Char36 | Boolean_Type</c>"在这一版被改掉，
/// 理由是这一家有原生 <c>uuid</c>（16 字节二进制，与 <c>Guid_Raw16</c> 同形），而 <c>char(36)</c> 那一档
/// 是给没有原生类型的库留的退路。逐列想退回文本形态仍然够得到 —— <c>[Column(TypeName=…)]</c> 的
/// <c>StoreTypeName</c> 短路就在 <see cref="PostgreSqlTypes.GetStoreType"/> 第一行。论证回写在 §4。</item>
/// </list>
/// 每一位的出处都记在 §13.5 那本台账里：本家族的每一位今天都是"厂商文档 + 未实测"。</summary>
public abstract class PostgreSqlProviderBase : IDbProvider
{
    readonly PostgreSqlTypes _types = PostgreSqlTypes.Instance;
    readonly PostgreSqlValues _values = PostgreSqlValues.Instance;
    readonly PostgreSqlExpressions _expressions = new();
    readonly PostgreSqlKeys _keys = new();
    readonly PostgreSqlDialect _dialect;

    protected PostgreSqlProviderBase(DbKind kind, DbProviderFactory driverFactory)
    {
        Kind = kind;
        Connectors = new PostgreSqlConnectorFactory(driverFactory);
        // 【本笔（DuckDB 落笔）开的缝】这两处从"直 new"改成虚口：PG 血统此前六颗叶子字节同形、
        // 构造里从来没有覆写入口（§14 行 2044 那格"两种零覆写者"的另一半），今天第七颗叶子
        // （DuckDB）与血统的分岔先到，缝先于第二份证据落地。虚口从基类构造里调，与 Oracle 那一族
        // 同一条纪律：覆写里只许碰静态成员与基类已初始化的字段，共享的 keys/values/expressions
        // 作为参数递过去，各槽位仍然共用同一批实例。
        CodeFirst = CreateCodeFirst(_types);
        _dialect = CreateDialect(kind, _types, _keys, _values, _expressions);

        // 与 SqlServerProvider / MySqlProviderBase 同一条接线：装载类（导入器）不在工厂的抽象面上，
        // 只能从同一支驱动程序集里按类型名与形状找；构造期仍然一次反射都没有（见 PostgreSqlBulk 的抬头）。
        Bulk = new PostgreSqlBulk(driverFactory);
    }

    /// <summary>血统的第一道缝（【本笔（DuckDB 落笔）新增】，先例是 OracleProviderBase 那一颗）：
    /// 血统的 DDL 缺省交给 <see cref="PostgreSqlCodeFirst"/>。叶子要换 DDL 文本在这一层覆写，
    /// 而不是在构造里拼一颗新的。<paramref name="types"/> 缺省时就是血统自己那份类型表；
    /// 参数可被覆写方忽略（DuckDB 的那一格就带的是自己的 <c>DuckDbTypes</c>，与 <see cref="PostgreSqlTypes"/>
    /// 没有继承关系）。</summary>
    protected virtual ICodeFirstDialect CreateCodeFirst(PostgreSqlTypes types) => new PostgreSqlCodeFirst(types);

    /// <summary>血统的第二道缝（【本笔（DuckDB 落笔）新增】）：文本层缺省交给 <see cref="PostgreSqlDialect"/>。
    /// 它存在的全部理由是方言构造的六个参数里有四个是本类私有的共享实例（keys / values / expressions），
    /// 叶子没有别的入口能拿到它们 —— 与"各槽位共用同一批实例"那条纪律配套（见类抬头）。
    /// 同样是虚口从基类构造里调：覆写里不碰任何虚属性、不替 <c>_dialect</c> 字段以外的谁做赋值。</summary>
    protected virtual PostgreSqlDialect CreateDialect(
        DbKind kind, PostgreSqlTypes types, PostgreSqlKeys keys, PostgreSqlValues values, PostgreSqlExpressions expressions)
        => new(kind, types, keys, values, expressions);

    public DbKind Kind { get; }

    /// <summary>子类要改的是<b>加</b>或<b>减</b>这一档，而不是另起一份 —— 另起一份就等于把 §4 那条继承链作废。</summary>
    public virtual DbCapabilities Capabilities => FamilyCapabilities;

    /// <summary>这一档的位掩码本体。声明给子类，也声明给 §13.5 那本出处台账（它按 provider 数"未实测"的位）。
    /// <b>【本笔加了一颗 <c>Bulk_Copy</c>】</b>判据与 MySQL 家那颗逐字同一条："代码今天真的走到" ——
    /// 通道接线在 <see cref="PostgreSqlBulk.ResolveCopyChannel"/>，反射装配这一格今天还没有集成册读数
    /// （活 PG 实例在场而 L1 路径未跑过，测试册补跑前按"已接线、未见证"读）。这颗加在<b>族档</b>上、
    /// 六颗 PG 血统的键一起领，而各叶子现场的驱动程序集里到底有没有导入器，是 <c>TryResolve</c> 的当场答案
    /// 不是掩码的承诺 —— 与"同一位的分叉库共用一份掩码"那条旧纪律不打架（那句话说的是文本分岔，
    /// 这一颗根本不走文本）。</summary>
    protected static DbCapabilities FamilyCapabilities =>
        DbCapabilities.Paging_LimitOffset | DbCapabilities.Returning | DbCapabilities.Upsert_OnConflict |
        DbCapabilities.Cte | DbCapabilities.WindowFunctions | DbCapabilities.Count_Over_Window |
        DbCapabilities.Boolean_Type | DbCapabilities.Guid_Raw16 | DbCapabilities.Schemas | DbCapabilities.Json_Type |
        DbCapabilities.CaseSensitive_Identifiers | DbCapabilities.Comment_On_Column |
        DbCapabilities.Alter_Column_Precision | DbCapabilities.TempTable | DbCapabilities.Bulk_Copy |
        DbCapabilities.Transactions;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    /// <summary><b>【本笔（DuckDB 落笔）开成虚口】</b>血统的类型表缺省是 <see cref="PostgreSqlTypes"/> 那一份。
    /// 开虚口的理由写全：只换方言那一格不够 —— 表达式翻译里 CAST 走的是
    /// <c>ExpressionTranslator.cs:1339</c> 那一句 <c>provider.Types.ApplyStorageDefaults(probe)</c>，
    /// 那条路读的就是这颗属性，叶子只覆写方言的话 <c>TIMESTAMP(3)</c>/<c>DOUBLE PRECISION</c>
    /// 这类血统类型名会从 CAST 一路漏进叶子产出的 SQL。</summary>
    public virtual ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new PostgreSqlSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new PostgreSqlConcurrency();

    /// <summary>PG 血统的完整性码表（§8.4）。每一个数都取自 2026-10-23 在一台可达的 PG 18.6 上的实测：
    /// <c>pg_temp</c> 里一张带齐四种约束的夹具，五种违例各撞一次，取值全程只碰
    /// <see cref="DbException"/> 这颗基类面（<c>SqlState</c> 与 <c>Data["ConstraintName"]</c>），
    /// 一个 Npgsql 的类型都没引用——所以这一格与 §1.2 原则 3「provider 编译期不认识驱动」不打架。
    /// <para>三条实测口径：<b>非空那一档库不给约束名</b>（那一档本来就没有命名约束，给的是列名与表名）；
    /// <b>外键的插入侧与删除侧同报 23503</b>，分岔只在文案；<b>DETAIL 在默认连接串上被驱动整段涂掉</b>
    /// （"Specify 'Include Error Detail'"），于是"从文案里取键列"这条路在生产连接串上不是不可靠、
    /// 而是根本没有材料——键列要么按约束名回契约查（§16.22 ②，还没接），要么就不给。</para>
    /// <para><b>金仓 / openGauss / 瀚高 / GBase 那几颗叶子今天跟着这一份走</b>，依据是它们自己声明继承
    /// 本家；这句不等于"验过"。各叶在真库上跑一遍 §13 那条分类用例才算数，跑出错就在叶子上覆写这一格——
    /// 这也是它写成 <see langword="virtual"/> 而不是封死在本家的原因。</para></summary>
    public virtual IntegrityViolation? ClassifyIntegrityError(DbException error) => error.SqlState switch
    {
        "23505" => new IntegrityViolation(IntegrityKind.Unique, PgConstraintName(error)),
        "23502" => new IntegrityViolation(IntegrityKind.NotNull),
        "23503" => new IntegrityViolation(IntegrityKind.ForeignKey, PgConstraintName(error)),
        "23514" => new IntegrityViolation(IntegrityKind.Check, PgConstraintName(error)),
        _ => null,
    };

    /// <summary>PG 把约束名放在 ADO.NET 的 <c>Data</c> 字典里（实测键名就是 <c>ConstraintName</c>），
    /// 所以这一格不需要驱动类型。非空那一档没有这个键，取到 <c>null</c> 是正常形状而不是失败。</summary>
    static string? PgConstraintName(DbException error) => error.Data["ConstraintName"] as string;

    public IChangeGuard ChangeGuard { get; } = new PostgreSqlChangeGuard();

    /// <summary><b>【本笔改判 —— 原文照抄在前，改判在后】</b>原文：「只有预算、没有高速通道：这一家的高速层是
    /// <c>CopyFrom</c>（驱动专有类型，要反射），P2 才接。」今天接上了：交的形状是 <c>BeginBinaryImportAsync</c>
    /// 的二进制 COPY 逐行推入面（反射绑定，与另两家同一惰性纪律），<see cref="DbCapabilities.Bulk_Copy"/>
    /// 那颗位跟着进本档族掩码。两颗必须分开读：<b>位说的是这份代码走到了，叶子现场装的那支驱动里有没有
    /// 装载类由 <c>TryResolve</c> 当场答</b> —— PG 血统五颗键共填 Npgsql 一颗 assembly 而借臂的叶子各装各的
    /// （§9.1 那几行的前件），fork 里没有导入器时整家退 L3，那一种现场这颗位报的是"代码可达而这一支驱动没有"，
    /// 与 SqlServer 那一家被裁剪驱动同一档处境（<c>ResolveCopyChannel</c> 抬头那句行为级的税，本家多一个真实读者）。
    /// 预算本身从第一天就要生效，否则分块公式会按 T-SQL 的 2100 去切一条这一家收得下的语句 —— L1 不吃预算，
    /// 那一格管的是退回文本层之后。</summary>
    public IBulkStrategy? Bulk { get; }

    /// <summary><b>【本笔（DuckDB 落笔）开成虚口，血统的值原样保留 true】</b>：现有六颗 PG 血统叶与
    /// 血统探针不走这一格，缺省值一个字不动的先例是"只开缝、不改血统行为"那条边界。DuckDB 是
    /// 内嵌单文件的库（"库"就是连接串里那颗文件，与 SQLite 同档），"建库"这一操作在本家没有对象，
    /// 由叶子覆写成 false（<c>SchemaSyncer</c> 按这一格绕开 <see cref="EnsureDatabaseExistsAsync"/>）。</summary>
    public virtual bool CanCreateDatabase => true;

    /// <summary>这一家认的库名键。按 Npgsql 文档列 <c>Database</c>/<c>dbname</c>/<c>db</c> 三颗
    /// （与 MySQL 血统那两处不同：那一家是实测钉下来的两颗，这一家<em>没有</em>驱动可测，
    /// 所以只列到文档写到那一格，别当成实测读数）。</summary>
    static readonly ConnectionStringCatalog Catalog = new("Database", "dbname", "db");

    /// <summary>PG 系没有 <c>CREATE DATABASE IF NOT EXISTS</c>，先查 <c>pg_database</c> 再建。
    /// 连到 <c>postgres</c> 库（每台 PG 服务器都有的默认库）执行检查与建库语句。
    /// 目标库名从连接串按同义词读（<see cref="Catalog"/>），与 MySQL 血统那一格同一个坑：
    /// 读不到就走「没库名、不建库」那一岔是静默的，所以候选键要一颗颗列全。
    /// <b>【本笔（DuckDB 落笔）开成虚口】</b>：血统的建库逻辑一句不改；叶子哪天要改"建库"
    /// 的表现（DuckDB 那一格是整格不存在——内嵌单文件无服务端），覆写点在这里。</summary>
    public virtual async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct = default)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        var dbName = Catalog.Read(builder);

        if (dbName is null) return;

        // 先把候选键全摘干净再写锚点库：只覆盖 Database 一颗的话，串里会同时留着
        // Database=postgres 与 dbname=原库，谁说了算交给驱动的解析顺序。
        Catalog.Clear(builder);

        builder["Database"] = "postgres";

        await using var connection = Connectors.CreateConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var checkCmd = Connectors.CreateCommand(connection);
        checkCmd.CommandText = "SELECT COUNT(*) FROM pg_database WHERE datname = @name";
        var p = checkCmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = dbName;
        checkCmd.Parameters.Add(p);
        var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        if (exists) return;

        await using var createCmd = Connectors.CreateCommand(connection);
        createCmd.CommandText = $"CREATE DATABASE \"{dbName.Replace("\"", "\"\"")}\"";
        await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
