using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.ClickHouse;

/// <summary>从 <c>system.tables</c> ∶ <c>system.columns</c> 两张系统表读结构
/// （§4，本家档）。与各家的 read 侧同一纪律：这里只有 ADO.NET 抽象层的虚调用，本文件不认识任何驱动类型
/// （§1.2 原则 3）。整层【文档·ClickHouse】（system.tables 与 system.columns 两页目录手册，
/// clickhouse.com/docs），【实测·2026-10-20 活库 26.9.21.9 ∶ 驱动 7.14.0】`system.columns` 那一问
/// 在探针里走通过（`name` ∶ `type` ∶ `position` 三格逐格与这里读的法子对上，0 行之策未走到——
/// 那一问先不翻）。
///
/// <para><b>读法与分岔逐条</b>：
/// <list type="bullet">
/// <item><b>列清单走 <c>system.columns</c> 一张表就够</b>：本家的目录把"列"与"它的形状"放同一行
///（<c>name</c>、型 <c>type</c>、序 <c>position</c>），不需要 GBase 8s 那一家的三表 join。
/// 表存在性由"0 行"给出：查不到列就当作表不在（与 T-SQL 那一档同读）。</item>
/// <item><b>表备注走 <c>system.tables.comment</c>，列备注走 <c>system.columns.comment</c></b>：
/// 建侧那一半（<see cref="ClickHouseCodeFirst.TryEmitTableComment"/> 的 <c>MODIFY COMMENT</c> 与
/// <see cref="ClickHouseCodeFirst.TryEmitColumnComment"/> 的 <c>COMMENT COLUMN</c>）写进去的备注，
/// 这一侧不读回就是单向发射（§9.5 的读回义务）—— beta.26 批起两半都读，空串口径同格：
/// 本家 comment 默认 <c>''</c>，"有这概念而没写"折成空串而不是 null，与
/// <see cref="SchemaTable.Comment"/> 那半同判。</item>
/// <item><b><see cref="SchemaColumn.IsIdentity"/> 恒 false</b>：本家没有"由库递增的列"那一档
///（<see cref="ClickHouseTypes"/> 类注释同一条），所以"这一列是不是库给值"的 library 侧答案是<em>否</em>
/// —— 显式 false 比交 null 好：那是读到事实而不是"读不出"。</item>
/// <item><see cref="SchemaColumn.IsPrimaryKey"/>、<see cref="SchemaColumn.Scale"/> 两格<em>不填</em>：
/// 主键在本家在引擎的 <c>ORDER BY</c> 里（<see cref="ClickHouseCodeFirst.EmitStorage"/> 那一段），
/// <c>system.columns</c> 的行上没有那一格；<c>Decimal(P,S)</c> 的 S 与 <c>DateTime64(3)</c> 的 3
/// 藏在 <c>type</c> 文本里，本笔不作文本解析 —— 两格都是 null = "这一家读不出"，不是"没有"。</item>
/// </list></para>
///
/// <para><b>【本文件最重要的一条：可空性的形变在型名里不在位里】</b>另九家判可空要么读一颗布尔位
///（GBase 8s 的 <c>coltype</c> 那颗位）、要么读 'Y'/'N' 文本；本家的可空性<em>长在型名里</em>：
/// 可空列的 <c>type</c> 是 <c>Nullable(T)</c>，非空列是裸 <c>T</c>。所以读侧要把包裹剥掉才对得上
/// 建侧的发射形（<see cref="ClickHouseTypes.GetStoreType"/> 发出去的是裸型名，包裹由
/// <see cref="ClickHouseCodeFirst"/> 缔结），剥法只认<em>最外层</em>那一颗 <c>Nullable(</c>：
/// <c>LowCardinality(Nullable(T))</c> 那一类嵌套不在本笔的形状里（本族建表从发 LowCardinality，
/// 现场手工建出嵌套那一档时按裸型读回，判据上会假红 —— 记 §14）。<b>剥粒度与归一</b>：
/// 本家目录在参数化型里带空格（<c>Nullable(Decimal(18, 4))</c>），而发射形不带
///（<c>Decimal(18,4)</c>）—— 剥皮之后一并把空格压掉，两侧才能同一个文本口。</para>
///
/// <para><b>【双文本形】</b>具名 database 与 currentDatabase 两支逐条并排，与 GBase 8s 同一对形状
/// 而来路不同：那一家的"模式"是所有者、具名那一支要两颗参数；本家的"模式"是 <c>database</c>
///（<see cref="DbCapabilities.Schemas"/> 那颗位在本家的落点），<c>CREATE DATABASE</c> 是独立语句
///【文档·ClickHouse：CREATE DATABASE 语句页】，两支各只一颗表名参数。那一句
/// <c>database = @db</c> 的参数带 '@' 前缀（见 <see cref="Param"/>）—— <see cref="ClickHouseDialect.ParameterPrefix"/>
/// 那颗 '@' 在本文件另一个消费点上续了档：发射层管写路径，读侧管目录问询，两处用同一份前缀读数。</summary>
public sealed class ClickHouseSchemaReader : ISchemaReader
{
    /// <summary>点给了 database 的读法。一颗参数、一个谓词 —— 具名形有"复用一颗参数"的奢侈
    ///（与 GBase 8s 的"位置形顺序即语义"对照读），所以 <c>@db</c>/<c>@table</c> 各自独立。
    /// <c>ORDER BY position</c>：<see cref="SchemaColumn.OrdinalPosition"/> 用目录给的序，
    /// 不折基（与另九家同一判据，"折成 1 基就是造假"）。</summary>
    const string ColumnsSql = """
        SELECT name, type, position, comment
        FROM system.columns
        WHERE database = @db AND table = @table
        ORDER BY position
        """;

    /// <summary>未点 database 那一支：<c>currentDatabase()</c> 把"缺省那一格"写进 SQL 而不写进绑定值。
    /// 与 GBase 8s 的 <c>CURRENT USER</c> 那一手同族：参数可以吃 null，但本家对"连个名都没有"那格的
    /// 诚实写法是让目录函数自己答。</summary>
    const string ColumnsSqlForCurrentDatabase = """
        SELECT name, type, position, comment
        FROM system.columns
        WHERE database = currentDatabase() AND table = @table
        ORDER BY position
        """;

    /// <summary>表备注一对：与列清单同一对谓词形状（两支逐条且只差那一颗 Selected 名）。
    /// <c>name</c> 是 <c>system.tables</c> 那一侧的表名字段（<c>system.columns</c> 那一侧的表字段叫
    /// <c>table</c>，两张目录不同名 —— 这一点逐页对过才敢写，并记在这里）。</summary>
    const string TableCommentSql = """
        SELECT comment
        FROM system.tables
        WHERE database = @db AND name = @table
        """;

    /// <summary>表备注的缺省 database 那一支（与 <see cref="TableCommentSql"/> 成对，同理由）。</summary>
    const string TableCommentSqlForCurrentDatabase = """
        SELECT comment
        FROM system.tables
        WHERE database = currentDatabase() AND name = @table
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        var database = table.Schema is { Length: > 0 } named ? named : null;

        var columns = new List<SchemaColumn>();

        await using (var command = Command(connection, database is null ? ColumnsSqlForCurrentDatabase : ColumnsSql))
        {
            command.Parameters.Add(Param(command, "db", database));
            command.Parameters.Add(Param(command, "table", table.Name));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            // 0 行 ─ 表不在（类注释第一条）。真读进来的每一行：名字、型（剥 Nullable 包裹）、目录给的序。
            do
            {
                columns.Add(new SchemaColumn(
                    reader.GetString(0),
                    StoreType(reader.GetString(1)),
                    (int?)null,
                    (int?)null,
                    IsNullable(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2))
                {
                    IsIdentity = false,

                    // 列备注与表备注同一空串口径：本家 comment 默认 ''，"有这概念而没写"折成空串，
                    // 不是 null（后者是"读不出"那一格，§9.5 比差别时两形要分得开）。
                    Comment = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                });
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        string tableComment;

        await using (var command = Command(connection, database is null ? TableCommentSqlForCurrentDatabase : TableCommentSql))
        {
            command.Parameters.Add(Param(command, "db", database));
            command.Parameters.Add(Param(command, "table", table.Name));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            // 空串 = 有这概念而没写（本家 comment 默认 ''），缺行 = 读不出；两形按
            // SchemaTable.Comment 的口径分开。（列清单已经证明表在场，这里 0 行多半是目录的边界，
            // 按读不出记而不是兜一个空串。）
            tableComment = await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0) ? reader.GetString(0) : string.Empty;
        }

        return new SchemaTable(table, [.. columns]) { Comment = tableComment };
    }

    /// <summary>恒 null：String 那一档没有库侧第二把尺可问（§10.4，与 <see cref="ClickHouseTypes"/>
    /// 的 Char 恒定同对 —— 本家"宽"只在仓库侧，探测口在这里没有对象）。交 null = "本家判不了"
    /// 而不是"没有宽"。</summary>
    public Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
        => Task.FromResult<LengthSemanticsReading?>(null);

    /// <summary>发出去的字形对齐建侧的哪一支（<see cref="ClickHouseCodeFirst"/> 的那两形）：
    /// <list type="bullet">
    /// <item><b>剥 <c>Nullable(</c> 在包裹在型名上</b>（类注释那条）：只剥最外层一颗。
    /// 副作用：入参用整条 <c>type</c>（未剥）交给判可空那颗。</item>
    /// <item><b>参数化型里的空格压成没有</b>：目录给 <c>Decimal(18, 4)</c> 而发射形 <c>Decimal(18,4)</c>，
    /// 空格压掉才同一个文本口（与 PostgreSQL 那一支 Normalize 同判据、不同路 —— 本家不做整条小写折名，
    /// 因为 ClickHouse 型名大小写敏感【文档没逐页回读】，折之即假）。</item>
    /// </list>
    /// 两步是「剥皮→压空格」：<c>Nullable(String)</c> → <c>String</c>、
    /// <c>Nullable(Decimal(18, 4))</c> → <c>Decimal(18,4)</c>、
    /// 裸 <c>DateTime64(3)</c> 原样。</summary>
    static string StoreType(string storeType)
    {
        var text = storeType.Trim();

        if (text.StartsWith("Nullable(", StringComparison.Ordinal) && text.EndsWith(')'))
            text = text[9..^1].Trim();

        return text.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    static bool IsNullable(string storeType)
        => storeType.Trim().StartsWith("Nullable(", StringComparison.Ordinal);

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    /// <summary>具名参数：前缀 '@' 写在对象名上（与 MySql/PostgreSQL/SqlServer 三族同一形状）。
    /// 文本里的占位符也是 <c>@db</c>/<c>@table</c> 那一形 —— <see cref="ClickHouseDialect.ParameterPrefix"/>
    /// 那颗 '@' 的读数出自官方 ADO.NET 客户端页【文档·ClickHouse】；<b>对象名要不要带前缀</b>
    /// 的那一半没有读数【推断·与前缀三方族同形，第一台引擎到手时问试那两颗之一】。</summary>
    static DbParameter Param(DbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = '@' + name;
        parameter.Value = value ?? (object)DBNull.Value;

        return parameter;
    }
}
