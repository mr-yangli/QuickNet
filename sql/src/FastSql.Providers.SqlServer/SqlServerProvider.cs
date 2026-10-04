using System.Data.Common;
using FastSql.Sql;
using FastSql.Values;

namespace FastSql.Providers.SqlServer;

/// <summary>SQL Server 的驱动槽位（§4）。P0 主靶（§14）。
///
/// <b>构造期不反射</b>：<see cref="DbProviderFactory"/> 由 Core 的 <c>DriverRegistry</c> 按
/// <c>FastSqlDrivers.json</c> 解析后注入，所以本程序集编译期不认识 <c>Microsoft.Data.SqlClient</c>
/// （§1.2 原则 3）。SqlServer 也走这条路径，是为了让"驱动反射加载"在 P0 第一次真跑起来，
/// 而不是等到只有私有源驱动的 P3 才首次运行——那是 §14 代价表里点名的坑。
///
/// 七个槽位共用同一批实例（<c>_types</c>/<c>_values</c>/…）：<c>Expressions.Register</c> 覆写必须同时
/// 对翻译器和方言生效，各 new 一份就会出现"注册了却不改行为"的幽灵 bug。</summary>
[FastSqlProvider(DbKind.SqlServer)]
public sealed class SqlServerProvider : IDbProvider
{
    /// <summary>清单里的 <c>key</c>。<c>assembly</c> 通常是 <c>Microsoft.Data.SqlClient</c>。</summary>
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
        DbCapabilities.TempTable;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new SqlServerSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new SqlServerConcurrency();

    public IChangeGuard ChangeGuard { get; } = new SqlServerChangeGuard();

    /// <summary>L1 有通道了（<c>SqlServerBulk.ResolveCopyChannel()</c> → <see cref="DbCapabilities.Bulk_Copy"/> 那颗位从此有对象），
    /// 而 2100 的参数预算从第一天就生效。通道是<em>懒</em>出来的：只产 SQL 的离线路径不会碰那次类型查找。</summary>
    public IBulkStrategy? Bulk { get; }

    public bool CanCreateDatabase => true;

    /// <summary>连到 <c>master</c> 库，查 <c>sys.databases</c>；不存在则 <c>CREATE DATABASE</c>。
    /// 连接串里的 <c>Database=</c> / <c>Initial Catalog=</c> 就是目标库名。</summary>
    public async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct = default)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (!builder.TryGetValue("Database", out var dbNameObj) || dbNameObj is not string dbName || dbName.Length == 0)
            return;

        builder.Remove("Database");
        builder["Database"] = "master";

        await using var connection = Connectors.CreateConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var checkCmd = Connectors.CreateCommand(connection);
        checkCmd.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
        var p = checkCmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = dbName;
        checkCmd.Parameters.Add(p);
        var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        if (exists) return;

        await using var createCmd = Connectors.CreateCommand(connection);
        createCmd.CommandText = $"CREATE DATABASE [{dbName.Replace("]", "]]")}]";
        await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
