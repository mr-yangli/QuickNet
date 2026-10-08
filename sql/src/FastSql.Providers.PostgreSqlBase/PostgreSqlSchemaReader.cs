using System.Data.Common;
using FastSql.Metadata;
using FastSql.Providers;

namespace FastSql.Providers.PostgreSqlBase;

/// <summary>从 <c>information_schema</c> 与 <c>pg_*</c> 目录读结构（§4）。
/// 这里只有 ADO.NET 抽象层的虚调用，本文件不认识任何驱动类型（§1.2 原则 3）。
///
/// <para>两条目录视图的选法：列清单走 <c>information_schema</c>（SQL 标准、跨这一族的各家最稳），
/// 量纲探测走 <c>pg_attribute</c>（<c>atttypmod</c> 只在那里）。<b>而 §9.5 结构同步欠的三样今天一次补齐</b>：
/// 备注（<c>pg_description</c>，表中 <c>objsubid = 0</c>、列中等于 <c>attnum</c>）、索引清单
/// （<c>pg_index</c> 的 <c>indkey</c> 逐位展开）、以及自增位（<c>is_identity</c> 或 <c>SERIAL</c> 那颗
/// <c>nextval</c> 默认值）——从前这三样一颗都不读，于是"改了备注、加了索引"在这一家永远判不出差别。</para>
///
/// <para><b>为什么索引走 <c>pg_index</c> 而不是 <c>pg_indexes</c></b>：<c>pg_indexes</c> 是一张"每索引一行、
/// 附带完整 CREATE 语句文本"的便利视图，那一格的列序要再把文本解析一遍才拿得出来，而列序在这里是索引内容的
/// 一部分（<c>(a,b)</c> 与 <c>(b,a)</c> 不是同一条）。基表 <c>pg_index</c> 直接把 <c>indkey</c> 交出来。</para>
///
/// <para><b>表达式索引整条挡在读侧之外</b>（<c>indkey</c> 里那一格是 0）：契约表达不了它，把它读回来只会在
/// "库里多出一条索引"那一格里多一条名字对不上任何列的假名，而开了删索引那一档时它会被删掉。这与 T-SQL
/// 那一家排掉 XML/全文索引同一取向。</para></summary>
public sealed class PostgreSqlSchemaReader : ISchemaReader
{
    /// <summary>备注那颗 <c>LEFT JOIN</c> 不成的意思是"这一列没写备注"，交回空串而不是 null：null 在
    /// <see cref="SchemaColumn.Comment"/> 那一格里说的是"这一家没有列注释这个概念"，那会把整档备注同步关掉，
    /// 于是"库里备注被删掉"这件事永远读不回来。自增位读两半：<c>GENERATED … AS IDENTITY</c> 落在
    /// <c>is_identity</c>，而 <c>SERIAL</c> 那一支在目录里没有身份、只有一颗 <c>nextval(…)</c> 默认值。</summary>
    const string ColumnsSql = """
        SELECT c.ordinal_position, c.column_name, c.data_type, c.character_maximum_length,
               c.numeric_precision, c.numeric_scale, c.datetime_precision, c.is_nullable,
               CASE WHEN c.is_identity = 'YES' OR c.column_default LIKE 'nextval%' THEN true ELSE false END,
               COALESCE((SELECT d.description
                         FROM pg_description d
                         JOIN pg_class dc ON dc.oid = d.objoid
                         JOIN pg_namespace dn ON dn.oid = dc.relnamespace
                         WHERE dn.nspname = c.table_schema AND dc.relname = c.table_name AND d.objsubid = c.ordinal_position), '')
        FROM information_schema.columns c
        WHERE c.table_schema = @schema AND c.table_name = @table
        ORDER BY c.ordinal_position
        """;

    /// <summary>表备注同一次 JOIN 的三个条件，只是 <c>objsubid = 0</c> 那一格换成表本身。
    /// <c>COALESCE</c> 让这一句恒交一行：交回空串说的是"有这概念而库里没写"，零行会被读成"这一家没这概念"。</summary>
    const string TableCommentSql = """
        SELECT COALESCE((SELECT d.description
                         FROM pg_description d
                         JOIN pg_class dc ON dc.oid = d.objoid
                         JOIN pg_namespace dn ON dn.oid = dc.relnamespace
                         WHERE dn.nspname = @schema AND dc.relname = @table AND d.objsubid = 0), '')
        """;

    /// <summary><c>indkey</c> 那颗 <c>int2vector</c> 逐位展开成"（列号, 序号）"两列，再经 <c>pg_attribute</c>
    /// 换成列名——<c>pg_get_indexdef</c> 交回的是带引号的发射形，与契约侧那个裸名差一层折法，而名字匹配
    /// 用的必须两侧同一个字形。<c>0 &lt;&gt; ALL (…)</c> 排掉表达式索引（见类注释）。
    /// 唯一<em>约束</em>那颗位是"名字对名字"判的（本家具名约束的支撑索引与约束同名），而<em>不是</em>
    /// <c>conindid</c>：那一颗要 PG 12.1+，而这一族的叶子有 9.2 内核的（openGauss 那一档）。
    /// <b>同一颗理由下这一句仍留一处 9.4+ 的依赖</b>：<c>WITH ORDINALITY</c> 要 9.4，所以
    /// <c>unnest(i.indkey::int[]) WITH ORDINALITY</c> 在 9.2 内核的那一档<em>可能</em>是语法错——那是
    /// 本仓库按内核代数推的【推断】（§9.1 厂商行没写这一句），而这一族那一档今天没有授权实例可问，
    /// 所以它记在这里而不是当场改：换成 <c>generate_series</c> 那一种写法要连累列序那颗序号一起重写，
    /// 在没有读数的地方换写法就是把一种猜换成另一种。</summary>
    const string IndexesSql = """
        SELECT ic.relname, i.indisunique, i.indisprimary,
               CASE WHEN con.conname IS NULL THEN false ELSE true END AS is_constraint,
               a.attname
        FROM pg_class t
        JOIN pg_namespace ns ON ns.oid = t.relnamespace
        JOIN pg_index i ON i.indrelid = t.oid
        JOIN pg_class ic ON ic.oid = i.indexrelid
        CROSS JOIN LATERAL unnest(i.indkey::int[]) WITH ORDINALITY AS k(attnum, ord)
        JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
        LEFT JOIN pg_constraint con ON con.conrelid = t.oid AND con.conname = ic.relname AND con.contype IN ('u', 'x')
        WHERE ns.nspname = @schema AND t.relname = @table AND 0 <> ALL (i.indkey::int[])
        ORDER BY ic.relname, k.ord
        """;

    /// <summary>§10.4 的量纲探测，只读目录表：不建临时表、不写数据（§9.5 那条"生产库常禁 DDL 权限"）。
    /// <c>atttypmod</c> 是这一家唯一存"声明宽度"的地方，它的编码是 <c>n + 4</c>（那 4 是变长头的历史遗留），
    /// 而 <c>-1</c> 表示没写宽度。</summary>
    const string ProbeSql = """
        SELECT ty.typname, a.atttypmod, COALESCE(co.collname, ''), a.attnotnull
        FROM pg_attribute a
        JOIN pg_class cl ON cl.oid = a.attrelid
        JOIN pg_namespace ns ON ns.oid = cl.relnamespace
        JOIN pg_type ty ON ty.oid = a.atttypid
        LEFT JOIN pg_collation co ON co.oid = a.attcollation
        WHERE ns.nspname = @schema AND cl.relname = @table AND a.attname = @column AND a.attnum > 0 AND NOT a.attisdropped
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        var schema = table.Schema ?? "public";
        var columns = new List<SchemaColumn>();
        string? tableComment = null;

        await using (var command = Command(connection, ColumnsSql))
        {
            command.Parameters.Add(Parameter(command, "schema", schema));
            command.Parameters.Add(Parameter(command, "table", table.Name));

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                var dataType = reader.GetString(2);
                var chars = reader.IsDBNull(3) ? (int?) null : reader.GetInt32(3);

                columns.Add(new SchemaColumn(
                    reader.GetString(1),
                    StoreType(dataType, chars, reader),
                    chars,
                    MaxBytes(dataType, chars),
                    reader.GetString(7) is "YES",
                    reader.GetInt32(0))
                {
                    Comment = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    IsIdentity = reader.GetBoolean(8),
                    IsPrimaryKey = false,
                    Scale = Scale(dataType, reader),
                });
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        await using (var command = Command(connection, TableCommentSql))
        {
            command.Parameters.Add(Parameter(command, "schema", schema));
            command.Parameters.Add(Parameter(command, "table", table.Name));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            tableComment = await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0) ? reader.GetString(0) : string.Empty;
        }

        var indexes = await ReadIndexesAsync(connection, schema, table, columns, ct).ConfigureAwait(false);

        return new SchemaTable(table, [.. columns]) { Comment = tableComment, Indexes = indexes };
    }

    /// <summary>索引一趟读两件事：非主键的那几条原样交回，主键那一条的列回写进
    /// <see cref="SchemaColumn.IsPrimaryKey"/>（那一格从前恒为"读不出"，于是"库里的主键换了一列"这件事
    /// 在本家连报都报不出来）。列名按 <c>indkey</c> 的原序聚合——列序是索引内容的一部分。</summary>
    static async Task<IReadOnlyList<SchemaIndex>> ReadIndexesAsync(
        DbConnection connection,
        string schema,
        TableInfo table,
        List<SchemaColumn> columns,
        CancellationToken ct)
    {
        var read = new List<(string Name, bool Unique, bool Primary, bool Constraint, List<string> Columns)>();

        await using (var command = Command(connection, IndexesSql))
        {
            command.Parameters.Add(Parameter(command, "schema", schema));
            command.Parameters.Add(Parameter(command, "table", table.Name));

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var unique = reader.GetBoolean(1);
                var primary = reader.GetBoolean(2);
                var constraint = reader.GetBoolean(3);
                var columnName = reader.IsDBNull(4) ? null : reader.GetString(4);

                if (columnName is null)
                    continue;

                if (read.Count > 0 && read[^1].Name == name)
                    read[^1].Columns.Add(columnName);
                else
                    read.Add((name, unique, primary, constraint, [columnName]));
            }
        }

        var keys = new HashSet<string>(read.Where(static x => x.Primary).SelectMany(static x => x.Columns), StringComparer.Ordinal);

        for (var i = 0; i < columns.Count; i++)
            if (keys.Contains(columns[i].Name))
                columns[i] = columns[i] with { IsPrimaryKey = true };

        return [.. read.Where(static x => !x.Primary)
            .Select(x => new SchemaIndex(x.Name, x.Unique, [.. x.Columns]) { IsConstraint = x.Constraint })];
    }

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        using var command = Command(connection, ProbeSql);

        command.Parameters.Add(Parameter(command, "schema", table.Schema ?? "public"));
        command.Parameters.Add(Parameter(command, "table", table.Name));
        command.Parameters.Add(Parameter(command, "column", columnName));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var typeName = reader.GetString(0);
        var typmod = reader.GetInt32(1);
        var collation = reader.GetString(2);

        var declared = typmod < 4 ? -1 : typmod - 4;

        // 这一家没有第二把尺子：varchar(n) 的 n 恒为字符数，库里没有一个字段记着"这一列最多几字节"。
        // 于是 DefinitionByteLength 填同一个数而不是 0 —— 填 0 会让 §9.5 的 doctor 报成"这一列长度 0"，
        // 那是把"问不出答案"写成"答案是没有"。（§10.4 三家对照里，这一家就是这一行。
        // v0.31 起这一行有两个人：第五家的目录里同样只有一个宽度，它填的是同一个数两遍，
        // 所以"问不出第二把"这一档从 1 家变成 2 家 —— 分布见 <c>XuguSchemaReader</c> 类注释末段。）
        return new LengthSemanticsReading(collation, declared, declared, typeName is "varchar" or "bpchar" or "text");
    }

    /// <summary>形状对得上吗——比的是<em>归一后的存储类型文本</em>，而归一只有本家做得起（§9.5 的 diff）：
    /// 发射侧一律大写（<c>VARCHAR(32)</c>）而 <c>information_schema</c> 一律小写长名
    /// （<c>character varying</c>），手工建的表上 <c>NUMERIC</c> 不带位数而契约侧永远带，
    /// 别名 <c>int4</c>／<c>bool</c> 与正名同型。<b>本家交回的是 true/false 而不是 null</b>：
    /// 交 null 说的是"这一家判不了"，而这一家把位数、标度与时间精度三格都读得回来。
    /// <c>MaxChars</c> 与 <c>Scale</c> 两格因此是<em>加料</em>（给 doctor 与"缺列就补"那两档用），
    /// 判形状只看这一个文本口。</summary>
    public bool? ColumnShapeMatches(ColumnContract expected, SchemaColumn actual)
        => string.Equals(Normalize(PostgreSqlTypes.Instance.GetStoreType(expected)), Normalize(actual.StoreType), StringComparison.Ordinal);

    static string Normalize(string storeType)
    {
        var text = storeType.Trim().ToLowerInvariant();
        var open = text.IndexOf('(');

        return open < 0 ? Fold(text) : Fold(text[..open]) + text[open..].Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>本家目录与发射形之间那几颗同义词，一张表收完（<c>pg_type</c> 那一侧的别名与
    /// <c>information_schema</c> 那一侧的 SQL 标准长名都指进来）。</summary>
    static string Fold(string name) => name switch
    {
        "int4" or "int" or "serial" => "integer",
        "int2" or "smallserial" => "smallint",
        "int8" or "bigserial" => "bigint",
        "float4" => "real",
        "float8" => "double precision",
        "bool" => "boolean",
        "dec" or "decimal" => "numeric",
        "character varying" => "varchar",
        "character" or "bpchar" => "char",
        "timestamp without time zone" => "timestamp",
        "timestamp with time zone" => "timestamptz",
        "time without time zone" => "time",
        _ => name,
    };

    static string StoreType(string dataType, int? chars, DbDataReader reader) => dataType switch
    {
        "character varying" => chars is { } n ? $"varchar({n})" : "text",
        "character" => chars is { } n ? $"char({n})" : "char",
        // 裸 NUMERIC 与手工建的表在这一支交回 NULL 位数（那是"不限定"，不是"0"），
        // 从前这里无条件 GetInt32，读到一颗 numeric 不带括号就抛 —— 本家 reader 的一处旧伤，今天补上。
        "numeric" => reader.IsDBNull(4) ? "numeric" : $"numeric({reader.GetInt32(4)},{reader.GetInt32(5)})",
        "timestamp without time zone" => Timed("timestamp", reader),
        "timestamp with time zone" => Timed("timestamptz", reader),
        "time without time zone" => Timed("time", reader),
        _ => dataType,
    };

    static string Timed(string name, DbDataReader reader)
        => reader.IsDBNull(6) ? name : $"{name}({reader.GetInt32(6)})";

    /// <summary>标度在小数那一族是 <c>numeric_scale</c>，在时间那一族是 <c>datetime_precision</c>：
    /// 两格在契约侧是同一个词（T-SQL 那一家把 <c>datetime2(3)</c> 的 3 也存在 <c>sys.columns.scale</c>），
    /// 所以这里也折成一颗。</summary>
    static int? Scale(string dataType, DbDataReader reader) => dataType switch
    {
        "numeric" => reader.IsDBNull(5) ? null : reader.GetInt32(5),
        "timestamp without time zone" or "timestamp with time zone" or "time without time zone" =>
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
        _ => null,
    };

    /// <summary>字节上限在这家问不出来（见 <see cref="ProbeLengthSemanticsAsync"/> 那句注释），
    /// 所以只在 <c>bytea</c> 上给一个"按字节量"的事实，其余留 null。</summary>
    static int? MaxBytes(string dataType, int? chars)
        => dataType is "bytea" ? chars : null;

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
