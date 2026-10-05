using System.Data.Common;
using System.Text;
using FastSql.Execution;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Schema;
using FastSql.Sql;

namespace FastSql.Core.Schema;

/// <summary><c>SyncStructure</c> 的执行体（§9.5）。按 <see cref="IEntitySchemaRegistry.RegisteredTypes"/> 枚举全部实体，
/// 逐个比对库中是否已有对应表；缺失的用 <see cref="ICodeFirstDialect.EmitCreateTable"/> 生成 DDL，再按策略执行或收集。
///
/// <para><b>一册实体从此可能住在几座库里</b>（§16.4 的分库）：这一格不再是"打开那条连接、逐张问一遍"，
/// 而是<em>先按每张表自己声明的那座集群分组，再一座一趟</em>。分组判据与读写共用同一颗
/// <see cref="DataSourceRouting.ClusterOf"/>，所以"这张表建在哪座库"与"这条 INSERT 发往哪座库"
/// 不会是两件事——那是这一格最坏的一种错法（表建在 A 库、写进 B 库，症状是运行期"表不存在"）。</para></summary>
/// <remarks>今天只做"表是否缺失"这一档 diff：<see cref="ISchemaReader"/> 读回的 <c>SchemaColumn</c> 不含主键、
/// 自增、注释、默认值，列级 diff（ADD COLUMN / ALTER COLUMN）要等这些字段补齐后才能做（§14 P2）。</remarks>
/// <remarks><b>分表实体在这一格里是跳过的</b>（两档策略都跳，跳了的名字进
/// <see cref="SchemaSyncResult.SkippedSplitTemplates"/>）：模板名带着占位符，它不是一张能建的表，
/// 而"把全部片都建出来"要的范围只有业务知道。那几张片由 <see cref="CreateShardsAsync"/> 按清单建，
/// 入口是 <c>orm.CreateSplitTables&lt;T&gt;(起, 止)</c> 与 <c>orm.SplitTablesScript&lt;T&gt;(…)</c>（§16.4）。</remarks>
public sealed class SchemaSyncer
{
    readonly IDbProvider _provider;
    readonly IEntitySchemaRegistry _schemas;
    readonly ConnectionSources _sources;

    public SchemaSyncer(IDbProvider provider, IEntitySchemaRegistry schemas, ConnectionSources sources)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(sources);

        _provider = provider;
        _schemas = schemas;
        _sources = sources;
    }

    /// <summary>按策略同步全部已注册实体的表结构。<b>一次调用 = 在册的每座集群各一趟</b>：
    /// 每张表按它自己声明的那座集群落库，缺省集群那张顺序与原样（不声明集群的实体全在一趟里、
    /// 一条连接上，与这一格落地之前逐字同形）。</summary>
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
        var skipped = new List<string>();
        var sb = new StringBuilder();
        var executed = false;

        // 先分组再动工：一张表属于哪座库这件事只在这一处问一次（判据与读写共用 DataSourceRouting），
        // 于是"这一趟要开几条连接、脚本要分几段"是分组的结果，而不是各处再判一遍。
        var byCluster = GroupByCluster(skipped);

        // Script 模式不需要连库：只生成 DDL 文本，不检查表是否存在
        if (policy == SchemaSyncPolicy.Script)
        {
            // 只有一座集群时<em>不加</em>那行段头：那一份文本与这一格落地之前逐字相等，
            // 而 §13 的脚本金标基线因此不必为一件本来不存在的事改口径。多座才分得出段，段头才有读者。
            var segmented = byCluster.Count > 1;

            foreach (var cluster in byCluster.Keys.Order(StringComparer.Ordinal))
            {
                var segment = new List<string>();

                if (segmented)
                    segment.Add($"-- 集群 {cluster}：下面这几句要落在这座集群的主库上");

                foreach (var contract in byCluster[cluster])
                {
                    var ddl = EmitCreateTable(contract);
                    created.Add(contract.Table.Name);
                    segment.Add(ddl.EndsWith(';') ? ddl : ddl + ';');
                }

                if (sb.Length > 0)
                    sb.AppendLine().AppendLine();

                sb.Append(string.Join(Environment.NewLine + Environment.NewLine, segment));
            }

            return new SchemaSyncResult(created, false, sb.ToString()) { SkippedSplitTemplates = [.. skipped] };
        }

        // Execute 模式要连库检查表是否存在；一座一趟，每趟先尝试建库（如果 provider 支持）
        foreach (var cluster in byCluster.Keys.Order(StringComparer.Ordinal))
        {
            var primary = _sources.PrimaryConnectionString(cluster);

            if (_provider.CanCreateDatabase)
                await _provider.EnsureDatabaseExistsAsync(primary, ct).ConfigureAwait(false);

            await using var connection = _provider.Connectors.CreateConnection(primary);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            foreach (var contract in byCluster[cluster])
            {
                var existing = await _provider.Schema.ReadTableAsync(connection, contract.Table, ct).ConfigureAwait(false);

                if (existing is not null)
                    continue;

                var ddl = EmitCreateTable(contract);
                created.Add(contract.Table.Name);

                await ExecuteDdlAsync(connection, ddl, ct).ConfigureAwait(false);
                executed = true;
            }
        }

        return new SchemaSyncResult(created, executed, sb.ToString()) { SkippedSplitTemplates = [.. skipped] };
    }

    /// <summary>把册子里每张表分到它自己那座集群那一格，分不进去的分表模板进 <paramref name="skipped"/>。
    /// <b>集群不在册时这里就抛</b>（<see cref="ConnectionSources.PrimaryConnectionString"/> 那句带在册清单的）：
    /// 实体写了一个配置里没有的集群名，症状本来就是"表建不出、语句发不出"，而这一格是它最便宜的现场 ——
    /// 连库还没连，报错里能同时看见那张表与那几座库的名字。</summary>
    Dictionary<string, List<EntityContract>> GroupByCluster(List<string> skipped)
    {
        var byCluster = new Dictionary<string, List<EntityContract>>(StringComparer.Ordinal);

        foreach (var clrType in _schemas.RegisteredTypes)
        {
            var contract = _schemas.Contract(clrType);

            if (contract.Table.Split is { } split)
            {
                skipped.Add($"{contract.Table.Name}（{split.Kind} 档，分片键列 {split.ColumnName}）");
                continue;
            }

            var cluster = DataSourceRouting.ClusterOf(contract.Table);
            AssertKnown(cluster, contract.Table.Name);

            if (!byCluster.TryGetValue(cluster, out var tables))
                byCluster[cluster] = tables = [];

            tables.Add(contract);
        }

        return byCluster;
    }

    void AssertKnown(string cluster, string tableName)
    {
        if (_sources.Knows(cluster))
            return;

        // 借 PrimaryConnectionString 那一颗把消息整个复用（它已经写着"在册的是哪几座"），不另造一份口径。
        _ = _sources.PrimaryConnectionString(cluster);
    }

    /// <summary>分表实体的一次建片（§16.4）：<b>清单是调用方给的，这里不猜范围</b>——现存的分片该有哪几段
    /// 只有业务知道，而"默认建全部"在一颗日档的表上等于凭空造几千张。逐张先问存在再发 DDL，
    /// 所以这一颗是幂等的：同一个范围跑两次，第二次一张也不建。</summary>
    /// <remarks>存在判据与 <see cref="SyncAsync"/> 同一颗（<see cref="ISchemaReader.ReadTableAsync"/>），
    /// 因此这一格<em>不需要新的方言能力位</em>：各家今天已能回答"这张表在不在"，而
    /// <c>CREATE TABLE IF NOT EXISTS</c> 那一族（MySQL/PG/SQLite 有、T-SQL 与 Oracle 没有）会让 DDL
    /// 在四家分成四种文本，正是要避开的那类分岔。</remarks>
    /// <remarks><b>索引不随片发</b>：<see cref="EntityContract.ForShardDdl"/> 交回的契约是空的索引清单，
    /// 而 <see cref="SplitTablesResult.IndexesNotCreated"/> 把模板上那几条的名字点出来。这不是"实现一半"，
    /// 是"两条出路都不肯悄悄走"（改名会多出第六份命名口径，只发第一张是一句假绿）。</remarks>
    public async Task<SplitTablesResult> CreateShardsAsync(
        EntityContract template, IReadOnlyList<TableInfo> shards, CancellationToken ct = default)
    {
        // 片落在模板自己声明的那座集群上（§16.4 两档叠在一起时的口径）：分片只改表名，不改住在哪座库。
        var primary = _sources.PrimaryConnectionString(DataSourceRouting.ClusterOf(template.Table));

        if (_provider.CanCreateDatabase)
            await _provider.EnsureDatabaseExistsAsync(primary, ct).ConfigureAwait(false);

        await using var connection = _provider.Connectors.CreateConnection(primary);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var created = new List<string>();
        var existing = new List<string>();

        foreach (var shard in shards)
        {
            if (await _provider.Schema.ReadTableAsync(connection, shard, ct).ConfigureAwait(false) is not null)
            {
                existing.Add(shard.Name);
                continue;
            }

            var ddl = EmitCreateTable(template.ForShardDdl(shard));
            created.Add(shard.Name);
            await ExecuteDdlAsync(connection, ddl, ct).ConfigureAwait(false);
        }

        var requested = new List<string>(shards.Count);

        foreach (var shard in shards)
            requested.Add(shard.Name);

        return new SplitTablesResult([.. requested], created, existing) { IndexesNotCreated = IndexNames(template) };
    }

    /// <summary><see cref="CreateShardsAsync"/> 的那一份文本，不连库（§9.5 的 Script 模式同一条路）：
    /// 给 DBA 审批、也给离线用例钉"同一个范围算出哪几张表"。脚本里同样没有 <c>CREATE INDEX</c>，
    /// 所以审批的人看见的就是执行时会发出去的那几句，一处不多一处不少。</summary>
    public string ShardsScript(EntityContract template, IReadOnlyList<TableInfo> shards)
    {
        var sb = new StringBuilder();

        // 只有不在缺省集群上时才写这一行段头：那一座才是"这段脚本要在哪个库里跑"的唯一读者信息，
        // 而缺省集群那一份文本与这一格落地之前逐字相等（§13 的分片脚本金标因此不用改一个字节）。
        var cluster = DataSourceRouting.ClusterOf(template.Table);

        if (!string.Equals(cluster, DataSourceRouting.DefaultCluster, StringComparison.Ordinal))
            sb.AppendLine($"-- 集群 {cluster}：这几句要落在这座集群的主库上").AppendLine();

        foreach (var shard in shards)
        {
            if (sb.Length > 0)
                sb.AppendLine().AppendLine();

            var ddl = EmitCreateTable(template.ForShardDdl(shard));
            sb.Append(ddl);

            if (!ddl.EndsWith(';'))
                sb.Append(';');
        }

        return sb.ToString();
    }

    static IReadOnlyList<string> IndexNames(EntityContract template)
    {
        if (template.Indexes.Count == 0)
            return [];

        var names = new List<string>(template.Indexes.Count);

        foreach (var index in template.Indexes)
            names.Add(index.Name);

        return [.. names];
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

    /// <summary>被这一趟<em>跳过</em>的分表模板，每项写着模板名、分片档与分片键列。<b>跳过不是漏掉</b>：
    /// 整库同步拿不到"要建哪几段"的范围，而默认建全部片是一张日档表上凭空造几千张 —— 范围由
    /// <c>orm.CreateSplitTables&lt;T&gt;(起, 止)</c> 那一格给。它非空时说明库里那些片还不存在，写侧要到
    /// 目标片上撞库错才看得见，所以这一格要有人看。</summary>
    public IReadOnlyList<string> SkippedSplitTemplates { get; init; } = [];

    public static readonly SchemaSyncResult Empty = new(Array.Empty<string>(), false, string.Empty);
}

/// <summary>一次分表建片的产物：<see cref="Requested"/> 是换算出来的片清单（顺序就是清单顺序），
/// <see cref="Created"/> 与 <see cref="AlreadyThere"/> 是它的两个子集。<b>三格合起来才是完整答案</b>：
/// "只建了几张"与"另外几张本来就在"要能分得开，否则重复跑这一颗看起来像什么都没做。</summary>
public sealed record SplitTablesResult(
    IReadOnlyList<string> Requested,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> AlreadyThere)
{
    /// <summary>模板上声明了、而这一趟<em>没有随任何一张片建出去</em>的索引名。它们一条都没发，
    /// 不是"发了一部分"：索引名在 PG 与 Oracle 那一族是模式内唯一的，同一个模板的 N 张片各发同一颗名字
    /// 到第二张就撞（理由与两条被否掉的出路写在 <see cref="EntityContract.ForShardDdl"/> 上，规格 §16.4 记为已裁）。
    /// <b>这一格非空就是"片上现在没有这些索引"</b>，读侧到那一张上是全表扫，不是慢一点的索引扫。</summary>
    public IReadOnlyList<string> IndexesNotCreated { get; init; } = [];
}
