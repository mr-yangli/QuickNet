using System.Data.Common;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.SqlServer;

/// <summary>SQL Server 的驱动槽位（§4）。P0 主靶（§14）。
///
/// <b>构造期不反射</b>：<see cref="DbProviderFactory"/> 由 Core 的 <c>DriverRegistry</c> 按
/// 内建表 <c>BuiltinDrivers</c> 解析后注入，所以本程序集编译期不认识 <c>Microsoft.Data.SqlClient</c>
/// （§1.2 原则 3）。SqlServer 也走这条路径，是为了让"驱动反射加载"在 P0 第一次真跑起来，
/// 而不是等到只有私有源驱动的 P3 才首次运行——那是 §14 代价表里点名的坑。
///
/// 七个槽位共用同一批实例（<c>_types</c>/<c>_values</c>/…）：<c>Expressions.Register</c> 覆写必须同时
/// 对翻译器和方言生效，各 new 一份就会出现"注册了却不改行为"的幽灵 bug。</summary>
[QuickSqlProvider(DbKind.SqlServer)]
public sealed class SqlServerProvider : IDbProvider
{
    /// <summary>内建表里的 <c>key</c>。<c>assembly</c> 通常是 <c>Microsoft.Data.SqlClient</c>。</summary>
    public const string DriverKey = "sqlserver";

    readonly SqlServerTypes _types = SqlServerTypes.Instance;
    readonly SqlServerValues _values = SqlServerValues.Instance;
    readonly SqlServerExpressions _expressions = new();
    readonly SqlServerKeys _keys = new();
    readonly SqlServerDialect _dialect;

    public SqlServerProvider(DbProviderFactory driverFactory)
    {
        Connectors = new SqlServerConnectorFactory(driverFactory);
        CodeFirst = new SqlServerCodeFirst(_types);
        _dialect = new SqlServerDialect(_types, _keys, _values, _expressions);

        // 批量策略要认识工厂住的那个程序集：装载类（<c>SqlBulkCopy</c>）不在 <see cref="DbProviderFactory"/>
        // 的抽象面上，只能从同一支驱动程序集里按名字取 —— 现场同时装两家时，跟着连接所属的那一家走才是唯一
        // 可能对的答案。这里交的是工厂而不是程序集名，所以构造期仍然一次反射都没有（见 SqlServerBulk 的抬头）。
        Bulk = new SqlServerBulk(driverFactory);
    }

    public DbKind Kind => DbKind.SqlServer;

    /// <summary>能力位就是 §9.1 那一行：有 <c>OUTPUT</c>、有 <c>MERGE</c>、有原生 <c>BIT</c> 与
    /// <c>uniqueidentifier</c>；<b>没有</b> <c>SKIP LOCKED</c>（T-SQL 只有 <c>READPAST</c> 提示，语义不等价），
    /// 也<b>没有</b> <c>Truncate_Cascade</c>。别为了让代码跑得过去而往上加位。</summary>
    public DbCapabilities Capabilities =>
        DbCapabilities.Paging_OffsetFetch | DbCapabilities.Returning | DbCapabilities.Upsert_MergeInto |
        DbCapabilities.Boolean_Type | DbCapabilities.Guid_Raw16 | DbCapabilities.Identity_ExplicitInsert |
        DbCapabilities.Schemas | DbCapabilities.Cte | DbCapabilities.WindowFunctions | DbCapabilities.Json_Type |
        DbCapabilities.Bulk_Copy | DbCapabilities.MultiStatement | DbCapabilities.MultiResultSets |
        DbCapabilities.Count_Over_Window | DbCapabilities.Comment_On_Column | DbCapabilities.Alter_Column_Precision |
        DbCapabilities.TempTable | DbCapabilities.Transactions;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new SqlServerSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new SqlServerConcurrency();

    /// <summary>SQL Server 的完整性码表（§8.4）。这一家的形状与 PG 差在两处，两处都是本机实测出来的
    /// （2026-10-23，集成目标实例上撞一次唯一索引）：<b><c>SqlState</c> 是空的</b>——
    /// <c>SqlException.SqlState</c> 在这家放的是 <c>ERROR_STATE()</c> 而不是 SQLSTATE，实测直接 <c>null</c>，
    /// 所以"通用层读 SqlState 就通吃"这一句在这里当场不成立（§8.4 那条待拍举的就是这一家）；
    /// 号在<b>基类的 <c>Data</c> 字典</b>里（<c>HelpLink.EvtID</c> = <c>2601</c>、
    /// <c>HelpLink.EvtSrc</c> = <c>MSSQLServer</c>），于是仍然不需要反射调驱动成员——
    /// §1.2 原则 3「加载可以反射，调用不行」在这一格守得住。</summary>
    /// <para><b>547 故意不分类</b>：那一颗号同时覆盖外键冲突、检查约束与命名唯一约束，
    /// 分开只能读文案。而 §8.4 的判据是"误分类比不分类坏"——把外键冲突说成"重复提交"，
    /// 用户会照着提示再点一次而库里根本没有那一行。所以 547 交回 <c>null</c>（驱动原样出洞），
    /// 等一家愿意按文案分档的时机再说，而不是今天赌一把。</para>
    /// <para><c>2601</c> 实测；<c>2627</c>（唯一约束/主键）与 <c>515</c>（非空）是厂商文档的号，
    /// 前者已被集成册那条"插同一个主键第二行"的用例转成实测，后者仍未在本机撞过。</para>
    public IntegrityViolation? ClassifyIntegrityError(System.Data.Common.DbException error)
    {
        // EvtSrc 那一格不是装饰：这根字典槽位是 ADO.NET 给"帮助链接"用的，别家驱动也可能填，
        // 所以按它点名的来源收口，认错了家就什么都别翻。
        if (!string.Equals(error.Data["HelpLink.EvtSrc"] as string, "MSSQLServer", StringComparison.Ordinal))
            return null;

        return error.Data["HelpLink.EvtID"] switch
        {
            "2601" or "2627" => new IntegrityViolation(IntegrityKind.Unique),
            "515" => new IntegrityViolation(IntegrityKind.NotNull),
            _ => null,
        };
    }

    public IChangeGuard ChangeGuard { get; } = new SqlServerChangeGuard();

    /// <summary>L1 有通道了（<c>SqlServerBulk.ResolveCopyChannel()</c> → <see cref="DbCapabilities.Bulk_Copy"/> 那颗位从此有对象），
    /// 而 2100 的参数预算从第一天就生效。通道是<em>懒</em>出来的：只产 SQL 的离线路径不会碰那次类型查找。</summary>
    public IBulkStrategy? Bulk { get; }

    public bool CanCreateDatabase => true;

    /// <summary>这一家认的库名键，三颗都列：SqlClient 认 <c>Database</c>、<c>Initial Catalog</c>
    /// 与不带空格的同一种写法，而<em>裸</em> <see cref="DbConnectionStringBuilder"/> <b>不做同义词归一</b>
    /// （实测只按字面键比，仅忽略大小写），所以候选表少列一颗就静默少读一颗。
    /// 判据与症状都记在 <see cref="ConnectionStringCatalog"/>，MySQL 血统那一格炸过。</summary>
    static readonly ConnectionStringCatalog Catalog = new("Database", "Initial Catalog", "InitialCatalog");

    /// <summary>连到 <c>master</c> 库，查 <c>sys.databases</c>；不存在则 <c>CREATE DATABASE</c>。
    /// 目标库名按 <see cref="Catalog"/> 读，读不到就按原样跳过建库。</summary>
    public async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct = default)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        var catalog = Catalog.Read(builder);

        if (catalog is null) return;

        // 几颗同义词一起摘、改完只留一份：留两颗互相矛盾的键等于把"哪一颗说了算"交给解析顺序。
        Catalog.Clear(builder);
        builder["Initial Catalog"] = "master";

        await using var connection = Connectors.CreateConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var checkCmd = Connectors.CreateCommand(connection);
        checkCmd.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
        var p = checkCmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = catalog;
        checkCmd.Parameters.Add(p);
        var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        if (exists) return;

        await using var createCmd = Connectors.CreateCommand(connection);
        createCmd.CommandText = $"CREATE DATABASE [{catalog.Replace("]", "]]")}]";
        await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
