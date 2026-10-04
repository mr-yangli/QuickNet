using System.Data.Common;
using FastSql.Metadata;
using FastSql.Providers;

namespace FastSql.Providers.PostgreSqlBase;

/// <summary>从 <c>information_schema</c> 与 <c>pg_attribute</c> 读结构（§4）。
/// 这里只有 ADO.NET 抽象层的虚调用，本文件不认识任何驱动类型（§1.2 原则 3）。
///
/// <b>整份读法都还没有在一台活库上跑过</b>（现场没有授权的 PG 系实例），所以它的定位是
/// "§9.5 那个 <c>doctor</c> 的只读探测按厂商文档写出来的样子"，而不是"验证过的读结构实现"——
/// 出处与重开条件记在 §13.5 那本台账里。两条目录视图的选法：列清单走 <c>information_schema</c>
/// （SQL 标准、跨这一族的各家最稳），量纲探测走 <c>pg_attribute</c>（<c>atttypmod</c> 只在那里）。</summary>
public sealed class PostgreSqlSchemaReader : ISchemaReader
{
    const string ColumnsSql = """
        SELECT c.column_name, c.data_type, c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.is_nullable, c.ordinal_position
        FROM information_schema.columns c
        WHERE c.table_schema = @schema AND c.table_name = @table
        ORDER BY c.ordinal_position
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
        using var command = Command(connection, ColumnsSql);

        command.Parameters.Add(Parameter(command, "schema", table.Schema ?? "public"));
        command.Parameters.Add(Parameter(command, "table", table.Name));

        var columns = new List<SchemaColumn>();

        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                var name = reader.GetString(0);
                var dataType = reader.GetString(1);
                var chars = reader.IsDBNull(2) ? (int?) null : reader.GetInt32(2);

                columns.Add(new SchemaColumn(name, StoreType(dataType, chars, reader), chars, MaxBytes(dataType, chars),
                    reader.GetString(5) is "YES", reader.GetInt32(6)));
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        return new SchemaTable(table, columns);
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

    static string StoreType(string dataType, int? chars, DbDataReader reader) => dataType switch
    {
        "character varying" => chars is { } n ? $"varchar({n})" : "text",
        "character" => chars is { } n ? $"char({n})" : "char",
        "numeric" => $"numeric({reader.GetInt32(3)},{reader.GetInt32(4)})",
        "timestamp without time zone" => "timestamp",
        "timestamp with time zone" => "timestamptz",
        "time without time zone" => "time",
        _ => dataType,
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
