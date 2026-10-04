using System.Data.Common;
using System.Text;
using FastSql.Execution;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Schema;
using FastSql.Sql;

namespace FastSql.Core.Schema;

/// <summary><c>SyncStructure</c> 的执行体（§9.5）。按 <see cref="IEntitySchemaRegistry.RegisteredTypes"/> 枚举全部实体，
/// 逐个比对库中是否已有对应表；缺失的用 <see cref="ICodeFirstDialect.EmitCreateTable"/> 生成 DDL，再按策略执行或收集。</summary>
/// <remarks>今天只做"表是否缺失"这一档 diff：<see cref="ISchemaReader"/> 读回的 <c>SchemaColumn</c> 不含主键、
/// 自增、注释、默认值，列级 diff（ADD COLUMN / ALTER COLUMN）要等这些字段补齐后才能做（§14 P2）。</remarks>
public sealed class SchemaSyncer
{
    readonly IDbProvider _provider;
    readonly IEntitySchemaRegistry _schemas;
    readonly string _connectionString;

    public SchemaSyncer(IDbProvider provider, IEntitySchemaRegistry schemas, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(connectionString);

        _provider = provider;
        _schemas = schemas;
        _connectionString = connectionString;
    }

    /// <summary>按策略同步全部已注册实体的表结构。</summary>
    /// <param name="policy">同步策略：<see cref="SchemaSyncPolicy.Execute"/> 直接执行、
    /// <see cref="SchemaSyncPolicy.Script"/> 只生成不执行、<see cref="SchemaSyncPolicy.Disabled"/> 返回空结果。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>同步结果：已创建的表清单与（Script 模式下的）完整 DDL 文本。</returns>
    public async Task<SchemaSyncResult> SyncAsync(SchemaSyncPolicy policy, CancellationToken ct = default)
    {
        if (policy == SchemaSyncPolicy.Disabled)
            return SchemaSyncResult.Empty;

        var types = _schemas.RegisteredTypes;

        if (types.Count == 0)
            return SchemaSyncResult.Empty;

        var created = new List<string>();
        var sb = new StringBuilder();
        var executed = false;

        // Script 模式不需要连库：只生成 DDL 文本，不检查表是否存在
        if (policy == SchemaSyncPolicy.Script)
        {
            foreach (var clrType in types)
            {
                var contract = _schemas.Contract(clrType);
                var ddl = EmitCreateTable(contract);
                created.Add(contract.Table.Name);

                if (sb.Length > 0)
                    sb.AppendLine().AppendLine();

                sb.Append(ddl);
                if (!ddl.EndsWith(';'))
                    sb.Append(';');
            }

            return new SchemaSyncResult(created, false, sb.ToString());
        }

        // Execute 模式要连库检查表是否存在；先尝试建库（如果 provider 支持）
        if (_provider.CanCreateDatabase)
            await _provider.EnsureDatabaseExistsAsync(_connectionString, ct).ConfigureAwait(false);

        await using var connection = _provider.Connectors.CreateConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        foreach (var clrType in types)
        {
            var contract = _schemas.Contract(clrType);
            var existing = await _provider.Schema.ReadTableAsync(connection, contract.Table, ct).ConfigureAwait(false);

            if (existing is not null)
                continue;

            var ddl = EmitCreateTable(contract);
            created.Add(contract.Table.Name);

            await ExecuteDdlAsync(connection, ddl, ct).ConfigureAwait(false);
            executed = true;
        }

        return new SchemaSyncResult(created, executed, sb.ToString());
    }

    string EmitCreateTable(EntityContract contract)
    {
        var builder = new StringBuilder();
        var parameters = new List<AdoParameter>();
        var writer = new SqlWriter(builder, parameters, _provider.Dialect.ParameterPrefix);

        _provider.CodeFirst.EmitCreateTable(writer, contract);

        return writer.ToSqlString();
    }

    async Task ExecuteDdlAsync(DbConnection connection, string ddl, CancellationToken ct)
    {
        await using var command = _provider.Connectors.CreateCommand(connection);
        command.CommandText = ddl;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary><c>SyncStructure</c> 的产物：已创建的表名清单、是否真正执行了 DDL、以及 Script 模式下的完整文本。</summary>
/// <remarks><see cref="Ddl"/> 在 Execute 模式下为空字符串（DDL 已逐条发掉，不回存）；
/// 在 Script 模式下包含全部 CREATE TABLE 语句，可直接交给 DBA 审批后手动执行。</remarks>
public sealed record SchemaSyncResult(
    IReadOnlyList<string> CreatedTables,
    bool Executed,
    string Ddl)
{
    /// <summary>没有需要同步的表（全部已存在，或没有注册任何实体）。</summary>
    public bool IsEmpty => CreatedTables.Count == 0;

    public static readonly SchemaSyncResult Empty = new(Array.Empty<string>(), false, string.Empty);
}
