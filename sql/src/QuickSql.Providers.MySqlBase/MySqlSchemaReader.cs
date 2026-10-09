using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.MySqlBase;

/// <summary>从 <c>information_schema</c> 读结构（§4）。这里只有 ADO.NET 抽象层的虚调用，
/// 本文件不认识任何驱动类型（§1.2 原则 3）。
///
/// <para><b>本家这一份读法自这一笔起在活库上跑过</b>（用户提供的一台 MySQL 8.4.8，凭据只落仓库根那颗
/// 本地文件），并且 §9.5 结构同步欠的四样一次补齐：列备注（<c>columns.column_comment</c>）、表备注
/// （<c>tables.table_comment</c>）、索引清单（<c>statistics</c> 逐列一行，按 <c>seq_in_index</c> 聚回列序）、
/// 自增位（<c>columns.extra</c> 里那颗 <c>auto_increment</c>）—— 从前这四样一颗都不读，于是
/// "改了备注、加了索引"在这一家永远判不出差别。<b>上一版这里写的是"整份读法没有在一台活库上跑过
/// （授权实例里没有一家是 MySQL 血统）"</b>，那一句的出处与作废都记 §13.5 那本台账：授权清单里今天
/// 有 <c>mysql</c> 这一家（§13.4），而它交的读数在下面各条注释里逐格点名。</para>
///
/// <para><b>索引为什么走 <c>statistics</c> 而不是 <c>show index</c></b>：后者是一条命令而不是一张视图，
/// 它在 <c>information_schema</c> 之外另要一颗权限，而本层那条"只读探测不许要求 DDL 权限"（§9.5）
/// 同样不许它要求别的权限。代价是 <c>statistics</c> 把一条索引摊成<em>每列一行</em>，
/// 于是列序要按 <c>seq_in_index</c> 自己聚——列序是索引内容的一部分（<c>(a,b)</c> 与 <c>(b,a)</c> 不是同一条）。</para>
///
/// <para><b>两颗这一家特有的挡法</b>：① <c>column_name</c> 为 NULL 的那一行是<em>函数索引</em>
/// （8.0.13 起的表达式索引），契约表达不了它，读回来只会在"库里多出一条索引"那一格里多一条
/// 没有列的假名，而开了删索引那一档时它会被删掉——所以整条挡在外面，与 PG 家挡 <c>indkey = 0</c>
/// 同一取向；② 外键的<em>支撑索引</em>在目录里与真索引并列且带着约束名，而 <c>DROP INDEX</c>
/// 删它会当场报 <c>Cannot drop index …: needed in a foreign key constraint</c>（活库 R-10 那一行量到的正是这句），
/// 所以它带 <c>IsConstraint = true</c> 交回，由计划层归进"要人工"那一格。</para>
///
/// <para>与另两族的一处结构差：这一家<em>两把尺子都在同一张视图里</em>。<c>CHARACTER_MAXIMUM_LENGTH</c>
/// 给声明的<b>字符</b>数，<c>CHARACTER_OCTET_LENGTH</c> 给这一列最多占的<b>字节</b>数（= 字符数 × 该字符集的
/// 最大字节宽），于是 §10.4 那句"库里问得出/问不出"在这一家有答案：<b>问得出，而且不用建临时表</b>
/// （金仓那一族要走 <c>pg_attribute.atttypmod</c>，而它只给声明宽度不给字节上限；T-SQL 那一族走的是
/// 真库实测那条路）。<b>第二家把两把尺子都读回来的是这一家，第一家是 Oracle 血统</b>（本条上一版写作"四家里
/// 唯一一份"，交账时按 <c>OracleSchemaReader</c> 推翻：它那一份读 <c>ALL_TAB_COLS</c> 的字符数与
/// <c>DATA_LENGTH</c> 的字节数，也是两把）—— 这一家真正独一份的是<b>不需要第三颗字段来判哪一把是声明的</b>：
/// 那一族要 <c>CHAR_USED</c>（同一颗 <c>DATA_LENGTH</c> 在 <c>BYTE</c> 语义下就是声明本身），
/// 而这里两颗字段的身份由字段名定死。【v0.31】这句"独一份"到五家还成立，但它量的范围要收窄成
/// <b>"两颗都从目录里摆着的这两家里，谁还需要第三颗字"</b>：第五家压根不在这个对比里，它的目录里只有
/// 一颗宽度、而且那颗还要拆包（<c>XuguSchemaReader</c> 类注释第三条），所以它落在金仓那一边而不是任何一边。
/// 而这里两颗字段的身份由字段名定死。所以 <see cref="ProbeLengthSemanticsAsync"/> 在这一家不是"退化实现"，
/// 它与 <c>MySqlTypes.ApplyStorageDefaults</c> 里那个 <c>MaxBytes = chars × 4</c>
/// 是同一个量的两种来源：这里问库，那里是假设 <c>utf8mb4</c>。两处不同以谁为准由 <c>doctor</c> 说，
/// 而它今天还没有读这一格的口（§14）。</para>
///
/// <para>模式为空时这里给的是 <c>DATABASE()</c> 而不是一个假名字，也不是"省略限定"：这一家的目录视图
/// <em>必须</em>按 schema 过滤（<c>information_schema.columns</c> 是全库并列的，省略过滤会把别的库里
/// 的同名表一起读回来，那是静默串表而不是报错）。判据写在 SQL 里而不是在 C# 里补一个 <c>dbo</c>/<c>public</c>
/// 式的默认，因为那一颗名字在这家根本不存在。</para>
///
/// <para>一处已知不读的：<c>column_type</c> 那一列写的是 <c>tinyint(1)</c> 还是 <c>tinyint(4)</c>，
/// 那是这一家区分"布尔"与"8 位整数"的唯一位置，而 <see cref="SchemaColumn"/> 只有 <c>StoreType</c> 一格能装它，
/// 装法要与 <see cref="MySqlTypes.TryGetClrType"/> 一起定才有意义（今天那里按 <c>data_type</c> 折，
/// 两家都会把 <c>tinyint(1)</c> 认成 <see cref="sbyte"/>）。<b>这一档在活库上量过而量不出可用的读数</b>：
/// 同一张表上那两列的 <c>data_type</c> 都是 <c>tinyint</c>，只有 <c>column_type</c> 分出
/// <c>tinyint(1)</c> 与 <c>tinyint(4)</c>（<c>obj/logs/v38-my-recon-1.log</c> 的 R-02），
/// 而本层的型名是从 <c>data_type</c> 拼的。缺口记 §14，不在这里靠猜。</para></summary>
public sealed class MySqlSchemaReader : ISchemaReader
{
    /// <summary>比从前多读四颗：<c>datetime_precision</c>（时刻那一族的位数，与 PG 家同一处境——
    /// 类型名里没有它）、<c>column_comment</c>（这一家的列注释是列定义的一部分，目录里恒交一行，
    /// 没写过就是<b>空串</b>而不是 NULL——所以"库里备注被删掉"在这一家读得回来）、<c>extra</c>
    /// （自增位在<b><em>这一家没有一颗布尔字段</em></b>：它写在这颗文本里，取值是
    /// <c>auto_increment</c>、<c>on update CURRENT_TIMESTAMP</c> 与它们的组合，所以判"是不是自增"
    /// 只能<b>包含</b>着判而不能相等着判）、<c>character_octet_length</c>。
    ///
    /// <para>第四颗是这一笔顺手改对的一处旧伤：<see cref="MaxBytes"/> 的注释从 v0.29 起就写着
    /// "字节上限在这一家是目录里现成的那一颗"，而它读的序号是 <c>CHARACTER_MAXIMUM_LENGTH</c>
    /// （声明的<b>字符</b>数）—— 注释与读数不一致，而两把尺子在 <c>utf8mb4</c> 下差四倍，
    /// 于是那一格交回的数一直比真值小一个数量级。这里<em>不是</em>改判据而是把读法改成注释说的那一颗：
    /// 同一张视图里那颗字段本来就摆着（<see cref="ProbeSql"/> 从第一天就在读它）。
    /// 顺带一处后果：<c>SchemaColumn.MaxBytes</c> 今天在 Core 一侧<em>没有消费者</em>（<c>StructureDiff</c>
    /// 只看型名、可空性、备注与那颗主键位，而读那一格的 <c>doctor</c> 口还没开——§14），
    /// 所以这一改不动任何一句同步判据，改的是"交回的那一颗数不能再是假的"。</para>
    ///
    /// <para><c>numeric_scale</c> 在这里<em>不能无条件读</em>：整数列在这一家交回的是 <c>0</c> 而不是 NULL
    /// （实测·X-02 那一颗 int 列的读数），所以"这一列的标度"要看 <c>data_type</c> 才定得了，
    /// 见 <see cref="Scale"/>。</para></summary>
    const string ColumnsSql = """
        SELECT c.ordinal_position, c.column_name, c.data_type, c.character_maximum_length, c.character_octet_length,
               c.numeric_precision, c.numeric_scale, c.datetime_precision, c.is_nullable, c.column_comment, c.extra
        FROM information_schema.columns c
        WHERE c.table_schema = COALESCE(NULLIF(@schema, ''), DATABASE()) AND c.table_name = @table
        ORDER BY c.ordinal_position
        """;

    /// <summary>表备注：<c>information_schema.tables</c> 的那一颗字段同样是"没写过 = 空串"。
    /// 表不存在时这一句交零行，而调用它的地方<em>已经先确认有列</em>（列零行整张表就返回 null），
    /// 所以零行在这里读作"这一家问不出"而不是"这张表不存在"。</summary>
    const string TableCommentSql = """
        SELECT t.table_comment
        FROM information_schema.tables t
        WHERE t.table_schema = COALESCE(NULLIF(@schema, ''), DATABASE()) AND t.table_name = @table
        """;

    /// <summary><c>statistics</c> 是"每索引每列一行"，所以 <c>ORDER BY index_name, seq_in_index</c>
    /// 让同一条索引的行<em>相邻且按列序</em>回来，C# 一侧顺着聚合即可（与 T-SQL 那一家同一手法）。
    /// 主键在<b><em>这一家没有名字</em></b>：建表时写的 <c>CONSTRAINT PK_x PRIMARY KEY</c> 里那颗名字被库
    /// 丢掉，目录里那一行永远叫 <c>PRIMARY</c>（这是本族厂商文档明写的一处，与 PG 家"支撑索引与约束同名"
    /// 正相反），所以 <see cref="SchemaIndex.Name"/> 交回的是 <c>PRIMARY</c> 而主键那一半走列回写、
    /// 不走名字。函数索引那一档见类注释第二条。</summary>
    const string IndexesSql = """
        SELECT s.index_name, s.non_unique, s.column_name,
               CASE WHEN fc.constraint_name IS NULL THEN 0 ELSE 1 END AS is_constraint
        FROM information_schema.statistics s
        LEFT JOIN information_schema.table_constraints fc
            ON fc.constraint_name = s.index_name AND fc.table_schema = s.table_schema AND fc.table_name = s.table_name
            AND fc.constraint_type = 'FOREIGN KEY'
        WHERE s.table_schema = COALESCE(NULLIF(@schema, ''), DATABASE()) AND s.table_name = @table AND s.column_name IS NOT NULL
        ORDER BY s.index_name, s.seq_in_index
        """;

    /// <summary>§10.4 的量纲探测，只读目录表（§9.5 那条"生产库常禁 DDL 权限"在这里天然成立：
    /// <c>information_schema</c> 是视图，不需要任何写权限）。
    /// 与 <see cref="ColumnsSql"/> 分两条而不是并成一条，是因为要读的列不同：这一条要字符集与字节上限，
    /// 那一条要全部列的顺序 —— 并成一条会让列表那一趟也带上永远用不上的三列。</summary>
    const string ProbeSql = """
        SELECT c.data_type, c.character_maximum_length, c.character_octet_length, c.collation_name, c.character_set_name
        FROM information_schema.columns c
        WHERE c.table_schema = COALESCE(NULLIF(@schema, ''), DATABASE()) AND c.table_name = @table AND c.column_name = @column
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        var columns = new List<SchemaColumn>();
        string? tableComment = null;

        await using (var command = Command(connection, ColumnsSql))
        {
            command.Parameters.Add(Parameter(command, "schema", table.Schema ?? string.Empty));
            command.Parameters.Add(Parameter(command, "table", table.Name));

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                var dataType = reader.GetString(2);
                var chars = CatalogLength(reader, 3);

                columns.Add(new SchemaColumn(
                    reader.GetString(1),
                    StoreType(dataType, chars, reader),
                    chars,
                    MaxBytes(reader),
                    reader.GetString(8) is "YES",
                    reader.GetInt32(0))
                {
                    // 这一家没有"这一列没写注释"与"这一家没有注释这一档"两种读数：column_comment 恒交一行，
                    // 没写过就是空串。交 null 才是把后者写进目录，而它会让"库里备注被删掉"判不出差别。
                    Comment = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    // 自增位是 extra 里的一颗子串，不是相等：那一格同时装着 on update CURRENT_TIMESTAMP，
                    // 两半都写时它是 "auto_increment" 加上那一串。
                    IsIdentity = reader.GetString(10).Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                    IsPrimaryKey = false,
                    Scale = Scale(dataType, reader),
                });
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        await using (var command = Command(connection, TableCommentSql))
        {
            command.Parameters.Add(Parameter(command, "schema", table.Schema ?? string.Empty));
            command.Parameters.Add(Parameter(command, "table", table.Name));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            tableComment = await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0) ? reader.GetString(0) : string.Empty;
        }

        var indexes = await ReadIndexesAsync(connection, table, columns, ct).ConfigureAwait(false);

        return new SchemaTable(table, [.. columns]) { Comment = tableComment, Indexes = indexes };
    }

    /// <summary>索引那一趟读两件事：非主键的那几条原样交回，主键（目录里那颗 <c>PRIMARY</c>）的列
    /// 回写进 <see cref="SchemaColumn.IsPrimaryKey"/>。名字在这里<em>不能</em>用来认主键：这一家把建表时
    /// 写的 <c>PK_&lt;表名&gt;</c> 丢掉而换成 <c>PRIMARY</c>，所以认它只认 <c>index_name = 'PRIMARY'</c>
    /// 这一颗常量；比较器也因此是 <c>Ordinal</c>（这一家的索引名按建它时那份拼法存着，
    /// <c>lower_case_table_names</c> 只动表名不动索引名【实测·Y-07】）。</summary>
    static async Task<IReadOnlyList<SchemaIndex>> ReadIndexesAsync(
        DbConnection connection,
        TableInfo table,
        List<SchemaColumn> columns,
        CancellationToken ct)
    {
        var read = new List<(string Name, bool Unique, bool Primary, bool Constraint, List<string> Columns)>();

        await using (var command = Command(connection, IndexesSql))
        {
            command.Parameters.Add(Parameter(command, "schema", table.Schema ?? string.Empty));
            command.Parameters.Add(Parameter(command, "table", table.Name));

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0);

                // 一条索引的每一行都带着同三个事实，而聚合只看"名字换没换"，所以这里逐行读不回判。
                var unique = reader.GetInt32(1) == 0;
                var columnName = reader.IsDBNull(2) ? null : reader.GetString(2);
                var constraint = reader.GetInt32(3) == 1;

                if (columnName is null)
                    continue;

                if (read.Count > 0 && read[^1].Name == name)
                    read[^1].Columns.Add(columnName);
                else
                    read.Add((name, unique, name == PrimaryKeyName, constraint, [columnName]));
            }
        }

        var keys = new HashSet<string>(read.Where(static x => x.Primary).SelectMany(static x => x.Columns), StringComparer.Ordinal);

        for (var i = 0; i < columns.Count; i++)
            if (keys.Contains(columns[i].Name))
                columns[i] = columns[i] with { IsPrimaryKey = true };

        return [.. read.Where(static x => !x.Primary)
            .Select(x => new SchemaIndex(x.Name, x.Unique, [.. x.Columns]) { IsConstraint = x.Constraint })];
    }

    /// <summary>目录里主键那一条索引的固定名——这一家不给它建表时写的那颗名字。</summary>
    const string PrimaryKeyName = "PRIMARY";

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        using var command = Command(connection, ProbeSql);

        command.Parameters.Add(Parameter(command, "schema", table.Schema ?? string.Empty));
        command.Parameters.Add(Parameter(command, "table", table.Name));
        command.Parameters.Add(Parameter(command, "column", columnName));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var dataType = reader.GetString(0);
        var chars = CatalogLength(reader, 1) ?? -1;
        var bytes = CatalogLength(reader, 2) ?? -1;
        var collation = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        var charset = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

        // 第二把尺子在这一家问得出来，所以不学金仓那一族填同一个数两遍：填出来的差（4 倍或 1 倍）
        // 本身就是那一条字符集事实。只有问不出的型（数值列、binary 之外没有 CHARACTER_OCTET_LENGTH 的那些）
        // 才退回"答案是没有"的写法 —— 那与另三族（金仓、T-SQL，v0.31 起再加第五家）的"这一家没有第二把尺子"
        // 是两种不同的退化，必须能分辨。
        var definitionByteLength = bytes >= 0 ? bytes : chars;

        // CharSemanticsProven 在这里的判据比另三家强：不是"没有反证"，是目录里同时躺着字符数与字节数，
        // 而前者按定义就是字符数（4.1 起）。空串字符集 = 这一列不是文本型，那一档不"proven"而是无关。
        return new LengthSemanticsReading(collation, chars, definitionByteLength,
            charset.Length > 0 && IsText(dataType));

        static bool IsText(string type) => type is "varchar" or "char" or "tinytext" or "text" or "mediumtext" or "longtext";
    }

    /// <summary>型名文本在这一家是从 <c>data_type</c> 拼出来的，而那一颗字段<em>不带任何宽度</em>：
    /// 所以字符族的宽度从 <c>character_maximum_length</c> 接回来、小数从 <c>numeric_precision</c> 与
    /// <c>numeric_scale</c>、时刻族从 <c>datetime_precision</c>（与 PG 家同一处境、同一写法）。
    /// 整数族这里<em>什么都不接</em>：<c>int(11)</c> 那一颗是显示宽度，8.0.17 起已废弃、
    /// <c>SHOW CREATE TABLE</c> 不再打印它，而 <c>data_type</c> 从来不含它 —— 折法见 <see cref="Normalize"/>。</summary>
    static string StoreType(string dataType, int? chars, DbDataReader reader) => dataType switch
    {
        "varchar" => chars is { } n ? $"varchar({n})" : "varchar",
        "char" => chars is { } n ? $"char({n})" : "char",
        "decimal" => reader.IsDBNull(5) ? "decimal" : $"decimal({reader.GetInt32(5)},{reader.GetInt32(6)})",
        "datetime" or "timestamp" or "time" => Timed(dataType, reader),
        _ => dataType,
    };

    /// <summary>时刻族的位数：<c>datetime</c>/<c>timestamp</c>/<c>time</c> 三型共用 <c>datetime_precision</c>，
    /// 而<em>裸写那一形在这一家读回 0</em>（不是 NULL），所以 <c>datetime</c> 与 <c>datetime(0)</c>
    /// 在这一家本来就是同一个型。</summary>
    static string Timed(string name, DbDataReader reader)
        => reader.IsDBNull(7) ? name : $"{name}({reader.GetInt32(7)})";

    /// <summary>标度在两族上是两颗字段：小数族是 <c>numeric_scale</c>，时刻族是
    /// <c>datetime_precision</c>（契约侧同一个词，与 T-SQL、PG 两家的折法同形）。
    /// <b>整数族在这里必须按 <c>data_type</c> 挡</b>：那一档的 <c>numeric_scale</c> 交回的是 <c>0</c>
    /// 而不是 NULL（实测·X-02 那颗 int 列的读数），不设闸门就等于把"这一列标度 0"写成一条库的事实，
    /// 而契约侧的整数压根没有标度那一格。</summary>
    static int? Scale(string dataType, DbDataReader reader) => dataType switch
    {
        "decimal" => reader.IsDBNull(6) ? null : reader.GetInt32(6),
        "datetime" or "timestamp" or "time" => reader.IsDBNull(7) ? null : reader.GetInt32(7),
        _ => null,
    };

    /// <summary>字节上限在这一家是目录里现成的那一颗：<c>CHARACTER_OCTET_LENGTH</c> 对文本列给
    /// "声明字符数 × 该字符集最大字节宽"、对 <c>binary</c>/<c>varbinary</c> 与各档 <c>BLOB</c> 给真实字节数，
    /// 所以这是落地四家里<em>第二次</em>有一格常规非 null 的答案（第一家是 Oracle 血统那一份的 <c>DATA_LENGTH</c>；
    /// 金仓那一族问不出、T-SQL 那一族要走实测，v0.31 的第五家也问不出 —— 它的宽度与标度共用一颗字段，
    /// 带进字节上限会读出一颗假数，见 <c>XuguSchemaReader</c> 那条纪律）。
    /// 数值列两颗都是 NULL → null，与金仓、达梦两族"问不出就是 null"同一条纪律。
    ///
    /// <para><b>一处这一家特有的折法：装得下才交回</b>。那一颗字段在目录里是 <c>bigint unsigned</c>，
    /// 而 <c>LONGBLOB</c>/<c>LONGTEXT</c> 交回 4294967295（4 GB − 1，本家给 <c>byte[]</c> 默认发的就是
    /// <c>LONGBLOB</c>），它装不进 <see cref="SchemaColumn.MaxBytes"/> 那颗 <c>int?</c>。这里填 null
    /// 而不是截断成一个假数 —— 与"问不出就是 null"共用一格是对的，把 4 GB 折成 <c>int.MaxValue</c>
    /// 是把一个库里没写过的数写进目录。同一把尺也量 <c>CHARACTER_MAXIMUM_LENGTH</c>（那颗对
    /// <c>LONGTEXT</c> 交回同一个 4 GB 数，见 <see cref="CatalogLength"/>）。</para></summary>
    static int? MaxBytes(DbDataReader reader) => CatalogLength(reader, 4);

    /// <summary>目录里那两颗长度字段的共用量法：<b>先按 64 位取，装得下才折成 <c>int?</c>，装不下交回 null</b>。
    /// 这一档不是可选项而是必读纪律：<c>DbDataReader.GetInt32</c> 在 4294967295 那一格直接抛
    /// <c>OverflowException</c>（驱动器 <c>TextSignedInt64ColumnReader</c> 抛的，8.4.8 活库上量出来的，
    /// 读数记 <c>obj/logs/v38-my-recon-1.log</c>），
    /// 也就是说一张有 <c>LONGTEXT</c> 列的表会让整趟 <c>ReadTableAsync</c> 红掉，而不是那一列读数缺格。
    /// null 在这一格够用：长文本族的型名从 <c>data_type</c> 拼、从来不带括号，而结构同步的判据就是那条形名。</summary>
    static int? CatalogLength(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var wide = Convert.ToInt64(reader.GetValue(ordinal));

        return wide > int.MaxValue ? null : (int) wide;
    }

    /// <summary>形状对得上吗——比的是<em>归一后的存储类型文本</em>，而归一只有本家做得起（§9.5 的 diff）。
    /// 本家三处不同源：① 发射侧大写而目录侧小写；② <b>整数的显示宽度只在发射侧存在</b>——契约侧的
    /// <c>TINYINT(1)</c>（布尔那一档）与目录侧的 <c>tinyint</c> 是同一个型，不摘掉它就成了"每一趟都想
    /// <c>MODIFY COLUMN</c> 一次而永远配不平"，而幂等裁判第一个抓红的就是这一处；③ 同义词：
    /// <c>numeric</c>/<c>decimal</c>、<c>integer</c>/<c>int</c>、<c>real</c>/<c>double</c>、
    /// <c>bool</c>/<c>tinyint</c> 在这一家各是同一个型的两个名字（厂商文档口径的 synonym 那一档）。
    ///
    /// <para><b>摘宽度只摘整数族</b>：<c>decimal(18,4)</c> 的两半、<c>datetime(3)</c> 的位数、
    /// <c>varchar(32)</c> 的宽度都是<em>语义</em>而不是排版，摘掉任何一颗都会把"真的改了精度"读成"没改"。</para>
    ///
    /// <para>一处已知代价与它换来的东西：<c>tinyint(1)</c>（这一家给布尔起的名字）与 <c>tinyint(4)</c>
    /// （一颗 8 位整数）在这里被折成同型，而那是目录层就分不开的两颗（真判据在 <c>column_type</c>，
    /// 见类注释末段那一档 §14 缺口）。它换来的是"§9.5 的同步在布尔列上不空转"——
    /// 两边都判不了与只保住那一个已知分不开的差别，取前者：后者每次同步都在改一列没改的东西。</para></summary>
    public bool? ColumnShapeMatches(ColumnContract expected, SchemaColumn actual)
        => string.Equals(Normalize(MySqlTypes.Instance.GetStoreType(expected)), Normalize(actual.StoreType), StringComparison.Ordinal);

    static string Normalize(string storeType)
    {
        var text = storeType.Trim().ToLowerInvariant();
        var open = text.IndexOf('(');

        if (open < 0)
            return Fold(text);

        var name = text[..open];

        if (IsIntegerWidth(name))
            return Fold(name);

        return Fold(name) + text[open..].Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>整数那一族的名字。<see cref="Normalize"/> 靠它决定"括号里那一颗是排版还是语义"。</summary>
    static bool IsIntegerWidth(string name)
        => name is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint";

    /// <summary>本家目录与发射形之间那几颗同义词，一张表收完（与 PG 家那张同位而条目不同：
    /// 这一家不需要拆 <c>pg_type</c> 那层的别名，<c>data_type</c> 交的就是 SQL 标准名，
    /// 所以这里收的四颗都是"手工建表时人能写的那个名字"）。</summary>
    static string Fold(string name) => name switch
    {
        "integer" => "int",
        "bool" or "boolean" => "tinyint",
        "dec" or "numeric" => "decimal",
        "real" => "double",
        _ => name,
    };

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    static DbParameter Parameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = '@' + name;
        parameter.Value = value;

        return parameter;
    }
}
