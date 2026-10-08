using System.Data.Common;
using System.Text;
using FastSql.Execution;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Schema;
using FastSql.Sql;

namespace FastSql.Core.Schema;

/// <summary><c>SyncStructure</c> 的执行体（§9.5）。按 <see cref="IEntitySchemaRegistry.RegisteredTypes"/> 枚举全部实体，
/// 逐个比对库中是否已有对应表；缺失的用 <see cref="ICodeFirstDialect.EmitCreateTable"/> 生成 DDL，已有的按
/// <see cref="StructureDiff"/> 算出列级改动，再按策略执行或收集。
///
/// <para><b>一册实体从此可能住在几座库里</b>（§16.4 的分库）：这一格不再是"打开那条连接、逐张问一遍"，
/// 而是<em>先按每张表自己声明的那座集群分组，再一座一趟</em>。分组判据与读写共用同一颗
/// <see cref="DataSourceRouting.ClusterOf"/>，所以"这张表建在哪座库"与"这条 INSERT 发往哪座库"
/// 不会是两件事——那是这一格最坏的一种错法（表建在 A 库、写进 B 库，症状是运行期"表不存在"）。</para></summary>
/// <remarks><b>列级 diff 只在 Execute 档判</b>：Script 档不连库，而 diff 的另一半是<em>库里的现形状</em>——
/// 没有读数就没有差别可判。那一档因此今天只出建表脚本，并在结果里写明这一格（不是静默少做，是
/// <see cref="SchemaSyncResult.NotJudged"/> 那一行）。要列级的脚本，出路是给一个只读的连接、走 Execute。</remarks>
/// <remarks><b>分表实体在这一格里是跳过的</b>（两档策略都跳，跳了的名字进
/// <see cref="SchemaSyncResult.SkippedSplitTemplates"/>）：模板名带着占位符，它不是一张能建的表，
/// 而"把全部片都建出来"要的范围只有业务知道。那几张片由 <see cref="CreateShardsAsync"/> 按清单建，
/// 入口是 <c>orm.CreateSplitTables&lt;T&gt;(起, 止)</c> 与 <c>orm.SplitTablesScript&lt;T&gt;(…)</c>（§16.4）。</remarks>
public sealed class SchemaSyncer
{
    readonly IDbProvider _provider;
    readonly IEntitySchemaRegistry _schemas;
    readonly ConnectionSources _sources;
    readonly bool _diffColumns;
    readonly bool _dropIndexes;

    /// <param name="diffColumns">列级同步那一档（<c>SchemaSyncColumns</c>）：关掉时已经在的表一格都不改。</param>
    /// <param name="dropIndexes">库里多出的索引要不要 <c>DROP</c>（<c>SchemaSyncDropIndexes</c>）。
    /// <b>默认关</b>：它与 <paramref name="diffColumns"/> 不是一个风险档——补一列不丢数据，删一条正在被用的
    /// 索引会当场拖慢查询，所以这一档只能由调用方显式开，不会因为"开了列级同步"就顺带生效。</param>
    public SchemaSyncer(
        IDbProvider provider,
        IEntitySchemaRegistry schemas,
        ConnectionSources sources,
        bool diffColumns = true,
        bool dropIndexes = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(sources);

        _provider = provider;
        _schemas = schemas;
        _sources = sources;
        _diffColumns = diffColumns;
        _dropIndexes = dropIndexes;
    }

    /// <summary>按策略同步全部已注册实体的表结构。<b>一次调用 = 在册的每座集群各一趟</b>：
    /// 每张表按它自己声明的那座集群落库，缺省集群那张顺序与原样（不声明集群的实体全在一趟里、
    /// 一条连接上，与这一格落地之前逐字同形）。</summary>
    /// <param name="policy">同步策略：<see cref="SchemaSyncPolicy.Execute"/> 直接执行、
    /// <see cref="SchemaSyncPolicy.Script"/> 只生成不执行、<see cref="SchemaSyncPolicy.Disabled"/> 返回空结果。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>同步结果：已创建的表、已补的列/改的列/发的备注/建的索引，以及两份给人看的清单
    /// （<see cref="SchemaSyncResult.NotJudged"/> 与 <see cref="SchemaSyncResult.Refused"/>）。</returns>
    public async Task<SchemaSyncResult> SyncAsync(SchemaSyncPolicy policy, CancellationToken ct = default)
    {
        if (policy == SchemaSyncPolicy.Disabled)
            return SchemaSyncResult.Empty;

        var types = _schemas.RegisteredTypes;

        if (types.Count == 0)
            return SchemaSyncResult.Empty;

        var tally = new Tally();
        var skipped = new List<string>();

        // 先分组再动工：一张表属于哪座库这件事只在这一处问一次（判据与读写共用 DataSourceRouting），
        // 于是"这一趟要开几条连接、脚本要分几段"是分组的结果，而不是各处再判一遍。
        var byCluster = GroupByCluster(skipped);

        // Script 模式不需要连库：只生成 DDL 文本，不检查表是否存在
        if (policy == SchemaSyncPolicy.Script)
        {
            // 只有一座集群时<em>不加</em>那行段头：那一份文本与这一格落地之前逐字相等，
            // 而 §13 的脚本金标基线因此不必为一件本来不存在的事改口径。多座才分得出段，段头才有读者。
            var segmented = byCluster.Count > 1;
            var sb = new StringBuilder();

            foreach (var cluster in byCluster.Keys.Order(StringComparer.Ordinal))
            {
                var segment = new List<string>();

                if (segmented)
                    segment.Add($"-- 集群 {cluster}：下面这几句要落在这座集群的主库上");

                foreach (var contract in byCluster[cluster])
                    foreach (var statement in CreateStatements(contract, tally.NotJudged.Add))
                    {
                        tally.Record(statement);
                        segment.Add(statement.Sql.EndsWith(';') ? statement.Sql : statement.Sql + ";");
                    }

                if (sb.Length > 0)
                    sb.AppendLine().AppendLine();

                sb.Append(string.Join(Environment.NewLine + Environment.NewLine, segment));
            }

            if (_diffColumns)
                tally.NotJudged.Add("列级同步在 Script 档不判：判据的另一半是库里的现形状，而这一档压根不连库（要列级脚本，出路是给一个只读连接走 Execute）");

            return new SchemaSyncResult(tally.Created, false, sb.ToString())
            {
                AddedColumns = [.. tally.AddedColumns],
                AlteredColumns = [.. tally.AlteredColumns],
                AppliedComments = [.. tally.Comments],
                CreatedIndexes = [.. tally.Indexes],
                DroppedIndexes = [.. tally.Dropped],
                DdlStatements = [.. tally.Sql],
                NotJudged = [.. tally.NotJudged],
                Refused = [.. tally.Refused],
                SkippedSplitTemplates = [.. skipped],
            };
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

                if (existing is null)
                {
                    foreach (var statement in CreateStatements(contract, tally.NotJudged.Add))
                    {
                        await ExecuteDdlAsync(connection, statement, tally.Sql, ct).ConfigureAwait(false);
                        tally.Record(statement);
                    }

                    continue;
                }

                if (!_diffColumns)
                    continue;

                await ApplyAsync(tally, connection, contract, existing, ct).ConfigureAwait(false);
            }
        }

        return new SchemaSyncResult(tally.Created, tally.Changed, string.Empty)
        {
            AddedColumns = [.. tally.AddedColumns],
            AlteredColumns = [.. tally.AlteredColumns],
            AppliedComments = [.. tally.Comments],
            CreatedIndexes = [.. tally.Indexes],
            DroppedIndexes = [.. tally.Dropped],
            DdlStatements = [.. tally.Sql],
            NotJudged = [.. tally.NotJudged],
            Refused = [.. tally.Refused],
            SkippedSplitTemplates = [.. skipped],
        };
    }

    /// <summary>一张已经在的表：先算计划，再把计划里的每一句发出去。<b>清单里没有的一句都不会发</b>，
    /// 而那几格拒（删列、改主键与自增；删索引要调用方显式开那一档）在计划层就已经挡掉了，
    /// 执行层拿不到它们。</summary>
    async Task ApplyAsync(Tally tally, DbConnection connection, EntityContract contract, SchemaTable existing, CancellationToken ct)
    {
        var plan = StructureDiff.Build(contract, existing, _provider.Schema, _provider.CodeFirst.Identifiers, _dropIndexes);

        foreach (var note in plan.NotJudged)
            tally.NotJudged.Add($"{contract.Table.Name}：{note}");

        foreach (var note in plan.Refused)
            tally.Refused.Add($"{contract.Table.Name}：{note}");

        foreach (var statement in ApplyStatements(contract, plan, tally))
        {
            await ExecuteDdlAsync(connection, statement, tally.Sql, ct).ConfigureAwait(false);
            tally.Record(statement);
        }
    }

    /// <summary>一句 <c>CREATE TABLE</c> 加上它欠的那几份备注。<b>建表那一趟从前一个字节都不发备注</b>：
    /// <c>TryEmitColumnComment</c> 在 SPI 上挂了很多版而没有调用点，于是"实体上写了备注、库里查不到"
    /// 是每一次新建表都发生的事，而不是只有改备注时才发生。</summary>
    List<Statement> CreateStatements(EntityContract contract, Action<string> notJudged)
    {
        var items = new List<Statement> { new(EmitCreateTable(contract), ChangeKind.CreateTable, contract.Table.Name) };

        foreach (var column in contract.Columns)
        {
            if (column.Comment is null)
                continue;

            var sql = ColumnCommentText(contract, column);

            if (sql is not null)
                items.Add(new Statement(sql, ChangeKind.ColumnComment, $"{contract.Table.Name}.{column.ColumnName}"));
            else
                notJudged($"{contract.Table.Name}.{column.ColumnName}：这一家写不出列注释，备注只落在契约里");
        }

        if (contract.Table.Comment is not null)
        {
            var sql = TableCommentText(contract.Table);

            if (sql is not null)
                items.Add(new Statement(sql, ChangeKind.TableComment, contract.Table.Name));
            else
                notJudged($"{contract.Table.Name}：这一家写不出表注释，备注只落在契约里");
        }

        return items;
    }

    List<Statement> ApplyStatements(EntityContract contract, StructurePlan plan, Tally tally)
    {
        var items = new List<Statement>();

        // 删索引排在补列与改列<em>之前</em>：T-SQL 上"这一列被索引覆盖着"会让 ALTER COLUMN 直接失败
        // （ALTER TABLE ... ALTER COLUMN ... 因为访问此列的对象而失败），而计划里那条索引正是建在这列上的
        // 那一条。顺序反过来就是"这一趟发不出去"，而不是"发得出去但慢一点"。
        foreach (var index in plan.DropIndexes)
        {
            var sql = DropIndexText(contract.Table, index);

            if (sql is not null)
                items.Add(new Statement(sql, ChangeKind.DropIndex, $"{contract.Table.Name}.{index.Name}"));
            else
                tally.NotJudged.Add($"{contract.Table.Name}.{index.Name}：这一家写不出删索引那一句，那条索引留在库里");
        }

        foreach (var column in plan.AddColumns)
            items.Add(new Statement(Emit(w => _provider.CodeFirst.EmitAddColumn(w, contract.Table, column)),
                ChangeKind.AddColumn, $"{contract.Table.Name}.{column.ColumnName}"));

        foreach (var column in plan.AlterColumns)
            items.Add(new Statement(Emit(w => _provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, column)),
                ChangeKind.AlterColumn, $"{contract.Table.Name}.{column.ColumnName}"));

        foreach (var column in plan.ColumnComments)
        {
            var sql = ColumnCommentText(contract, column);

            if (sql is not null)
                items.Add(new Statement(sql, ChangeKind.ColumnComment, $"{contract.Table.Name}.{column.ColumnName}"));
            else
                tally.NotJudged.Add($"{contract.Table.Name}.{column.ColumnName}：这一家写不出列注释");
        }

        if (plan.EmitTableComment)
        {
            var sql = TableCommentText(contract.Table);

            if (sql is not null)
                items.Add(new Statement(sql, ChangeKind.TableComment, contract.Table.Name));
            else
                tally.NotJudged.Add($"{contract.Table.Name}：这一家写不出表注释");
        }

        foreach (var index in plan.CreateIndexes)
            items.Add(new Statement(Emit(w => _provider.CodeFirst.EmitCreateIndex(w, contract.Table, index)),
                ChangeKind.CreateIndex, $"{contract.Table.Name}.{index.Name}"));

        return items;
    }

    /// <summary>交回 null 说的是<em>这一家没有这一档</em>（<c>TryEmit*</c> 交回 false），
    /// 别让"没有"退化成"发了一条空句"。</summary>
    string? ColumnCommentText(EntityContract contract, ColumnContract column)
        => EmitTry(w => _provider.CodeFirst.TryEmitColumnComment(w, contract.Table, column));

    string? TableCommentText(TableInfo table)
        => EmitTry(w => _provider.CodeFirst.TryEmitTableComment(w, table, table.Comment));

    string? DropIndexText(TableInfo table, SchemaIndex index)
        => EmitTry(w => _provider.CodeFirst.TryEmitDropIndex(w, table, index));

    string? EmitTry(Func<SqlWriter, bool> emit)
    {
        var writer = NewWriter();

        return emit(writer) ? writer.ToSqlString() : null;
    }

    string Emit(Action<SqlWriter> emit)
    {
        var writer = NewWriter();

        emit(writer);

        return writer.ToSqlString();
    }

    SqlWriter NewWriter() => new(new StringBuilder(), [], _provider.Dialect.ParameterPrefix);

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

        // 这一趟自己的"已生效"清单：分片不走 Tally（那一份是 SyncAsync 那趟的账），而失败时要报的
        // "前面建好了哪几张片"恰恰只有这一趟知道。
        var sent = new List<string>();

        foreach (var shard in shards)
        {
            if (await _provider.Schema.ReadTableAsync(connection, shard, ct).ConfigureAwait(false) is not null)
            {
                existing.Add(shard.Name);
                continue;
            }

            var statement = new Statement(EmitCreateTable(template.ForShardDdl(shard)), ChangeKind.CreateTable, shard.Name);
            created.Add(shard.Name);
            await ExecuteDdlAsync(connection, statement, sent, ct).ConfigureAwait(false);
            sent.Add(statement.Sql);
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

    /// <summary>发出一句 DDL，<b>失败时把"哪一句"与"前面已经落库的哪几句"一起报出去</b>
    /// （<see cref="SchemaSyncFailedException"/>）。这一格是唯一的抛出点：三个调用点都只交回
    /// <see cref="Statement"/> 与那份已经成功的清单，所以没有一处能漏掉这个包装。</summary>
    /// <param name="applied">这一句之前真发出去过的那些句（<c>Tally.Sql</c> 或分片那一趟的局部清单）。
    /// 传的是活清单，异常在抛出那一刻取快照。</param>
    async Task ExecuteDdlAsync(DbConnection connection, Statement statement, IReadOnlyList<string> applied, CancellationToken ct)
    {
        await using var command = _provider.Connectors.CreateCommand(connection);
        command.CommandText = statement.Sql;

        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SchemaSyncFailedException(
                statement.Sql,
                statement.Name,
                statement.Kind.ToString(),
                applied,
                connection.Database,
                ex);
        }
    }
}

/// <summary>一条要发出去的 DDL，连"它改的是哪一格"的那句话。<b>文本与名字同行</b>是因为两档策略读的是
/// 同一份清单：Execute 发文本、把名字记进结果；Script 把文本拼进脚本、同样把名字记进结果。
/// 少了名字那一格，结果里就只能说"发了几句"，而 §9.5 要答的是"哪一列被改了"。</summary>
readonly record struct Statement(string Sql, ChangeKind Kind, string Name);

/// <summary>一句 DDL 改的是哪一档。它决定这句话落进 <see cref="SchemaSyncResult"/> 的哪一份清单。</summary>
enum ChangeKind
{
    CreateTable,
    AddColumn,
    AlterColumn,
    ColumnComment,
    TableComment,
    CreateIndex,
    DropIndex,
}

/// <summary>一趟同步的七份点名清单。<b>内部类型，因为它只活在这一格里</b>：两档策略共用一个累加点
/// （<see cref="Record"/>），于是"哪些算动作、哪些只是给人的话"这一判据也只有一份。</summary>
sealed class Tally
{
    public List<string> Created { get; } = [];
    public List<string> AddedColumns { get; } = [];
    public List<string> AlteredColumns { get; } = [];
    public List<string> Comments { get; } = [];
    public List<string> Indexes { get; } = [];
    public List<string> Dropped { get; } = [];
    public List<string> NotJudged { get; } = [];
    public List<string> Refused { get; } = [];

    /// <summary>这一趟发出去（Script 档则是准备发出去）的每一句原文，顺序 = 发送顺序。<b>它与
    /// <see cref="SchemaSyncResult.DdlStatements"/> 是同一份清单</b>，只是两档的语义不同：Execute 档里
    /// 这一格是"真发过"，Script 档里是"会发"。收在这一格是因为 DDL 不走 <c>Aop.Executing</c>
    /// （那条钩子只管带参数的语句，而这一趟连的是同步自己那条连接），调用方要看见发了什么只有这一处。</summary>
    public List<string> Sql { get; } = [];

    /// <summary>这一趟真发出去过至少一句。<see cref="SchemaSyncResult.Executed"/> 读的是这一格，
    /// 而它说的不是"连了库"——一趟只读的同步连了库、判了所有档、一个字节没发，那一格必须是 false。</summary>
    public bool Changed { get; private set; }

    public void Record(Statement statement)
    {
        Changed = true;

        Sql.Add(statement.Sql);

        switch (statement.Kind)
        {
            case ChangeKind.CreateTable:
                Created.Add(statement.Name);
                break;
            case ChangeKind.AddColumn:
                AddedColumns.Add(statement.Name);
                break;
            case ChangeKind.AlterColumn:
                AlteredColumns.Add(statement.Name);
                break;
            case ChangeKind.ColumnComment:
            case ChangeKind.TableComment:
                Comments.Add(statement.Name);
                break;
            case ChangeKind.CreateIndex:
                Indexes.Add(statement.Name);
                break;
            case ChangeKind.DropIndex:
                Dropped.Add(statement.Name);
                break;
        }
    }
}

/// <summary><c>SyncStructure</c> 的产物：这一趟发了什么、判不了什么、拒了什么，
/// 以及（Script 模式下的）完整 DDL 文本。</summary>
/// <remarks><see cref="Ddl"/> 在 Execute 模式下为空字符串（那一档的原文在
/// <see cref="DdlStatements"/> 里，逐句一条）；在 Script 模式下包含全部 <c>CREATE TABLE</c> 与备注语句，
/// 可直接交给 DBA 审批后手动执行。</remarks>
public sealed record SchemaSyncResult(
    IReadOnlyList<string> CreatedTables,
    bool Executed,
    string Ddl)
{
    /// <summary>这一趟补上的列，逐项写 <c>表.列</c>。</summary>
    public IReadOnlyList<string> AddedColumns { get; init; } = [];

    /// <summary>这一趟 <c>ALTER COLUMN</c> 过的列，逐项写 <c>表.列</c>。改的可能是长度、标度，也可能是可空性——
    /// 那一格在 T-SQL 里必须整列定义重写，所以两者在同一句里，报告里也在同一格里。</summary>
    public IReadOnlyList<string> AlteredColumns { get; init; } = [];

    /// <summary>这一趟发过（或重发过）的备注，逐项写 <c>表.列</c>，表自身那一条只写表名。
    /// <b>新增与被改的列都在这一格</b>：ALTER 会连带抹掉那一列的备注，不发回去就是"同步成功而备注没了"。</summary>
    public IReadOnlyList<string> AppliedComments { get; init; } = [];

    /// <summary>这一趟建出来的索引，逐项写 <c>表.索引名</c>。</summary>
    public IReadOnlyList<string> CreatedIndexes { get; init; } = [];

    /// <summary>这一趟 <c>DROP</c> 掉的索引，逐项写 <c>表.索引名</c>，名字是库里读回来的那一个。
    /// <b>这一格在默认配置下永远是空的</b>：删索引要 <c>SchemaSyncDropIndexes</c> 显式开，而唯一约束
    /// 即便开了也不在这里（它进 <see cref="Refused"/>，因为 <c>DROP INDEX</c> 删不动约束）。
    /// 非空就是"库里现在少了几条索引"——那些查询从这一刻起走全表扫，这一格要有人看。</summary>
    public IReadOnlyList<string> DroppedIndexes { get; init; } = [];

    /// <summary>这一趟的 DDL 原文，逐句一条，顺序 = 发送顺序。<b>Execute 档里它是"真发过的那几句"</b>
    /// （<see cref="Ddl"/> 在那一档是空串，而 DDL 又不经过 <c>Aop.Executing</c> 那条日志钩子——
    /// 同步自己连的是另一条连接，所以调用方要在日志里看见发了什么，只有这一格是给得出的）；
    /// Script 档里它是"会发的那几句"，与 <see cref="Ddl"/> 同内容、只是没拼成一整篇。</summary>
    public IReadOnlyList<string> DdlStatements { get; init; } = [];

    /// <summary>这一家<em>读不出</em>所以判不了的档，逐条指名。<b>它非空就是"这一趟有几档根本没同步过"</b>：
    /// 六家在补齐读侧之前各有一串这样的名字，而一句"同步完成"盖不住它们（§12.2 那句不假装）。</summary>
    public IReadOnlyList<string> NotJudged { get; init; } = [];

    /// <summary>读得出而<em>故意不发</em>的那几格（删列、改主键与自增、DROP 索引），逐条指名并给出路。
    /// 与 <see cref="NotJudged"/> 的分工见 <see cref="StructurePlan"/>。</summary>
    public IReadOnlyList<string> Refused { get; init; } = [];

    /// <summary>这一趟一个字节都没发。<b>只看五份动作清单，不看两份话单</b>：判不了与要人工是那几档
    /// <em>本来就发不出东西</em>的格，把它们算进"有变化"会让幂等裁判永远红。</summary>
    public bool IsEmpty
        => CreatedTables.Count == 0 && AddedColumns.Count == 0 && AlteredColumns.Count == 0
           && AppliedComments.Count == 0 && CreatedIndexes.Count == 0 && DroppedIndexes.Count == 0;

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
