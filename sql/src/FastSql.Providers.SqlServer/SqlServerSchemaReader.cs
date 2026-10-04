using System.Data.Common;
using FastSql.Metadata;
using FastSql.Providers;

namespace FastSql.Providers.SqlServer;

/// <summary>从 <c>sys.columns</c> 读结构（§4）。这里只有 ADO.NET 抽象层的虚调用：
/// 驱动类型由 <c>DriverRegistry</c> 在运行期解析，本文件不认识 <c>SqlConnection</c>。</summary>
public sealed class SqlServerSchemaReader : ISchemaReader
{
    const string ColumnsSql = """
        SELECT c.column_id, c.name, ty.name AS type_name, c.max_length, c.precision, c.scale, c.is_nullable
        FROM sys.columns c
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(@table)
        ORDER BY c.column_id
        """;

    /// <summary>§10.4 的量纲探测。只读目录视图与会话属性，不建临时表、不写数据：
    /// 信创生产库常禁 DDL 权限，探测工具不能反过来要求它（§9.5）。</summary>
    const string ProbeSql = """
        SELECT ty.name AS type_name, c.max_length, c.collation_name
        FROM sys.columns c
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(@table) AND c.name = @column
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        using var command = Command(connection, ColumnsSql);

        command.Parameters.Add(Parameter(command, "table", Qualified(table)));

        var columns = new List<SchemaColumn>();

        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                var name = reader.GetString(1);
                var storeType = reader.GetString(2);
                var byteLength = reader.GetInt16(3);
                var isNullable = reader.GetBoolean(6);

                columns.Add(new SchemaColumn(name, StoreType(storeType, byteLength, reader),
                    MaxChars(storeType, byteLength), MaxBytes(storeType, byteLength), isNullable, reader.GetInt32(0)));
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        return new SchemaTable(table, columns);
    }

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        using var command = Command(connection, ProbeSql);

        command.Parameters.Add(Parameter(command, "table", Qualified(table)));
        command.Parameters.Add(Parameter(command, "column", columnName));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var storeType = reader.GetString(0);
        var definitionBytes = reader.GetInt16(1);
        var collation = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);

        // nvarchar/nchar 的 max_length 是字节数而 n 是字符数（UTF-16，恒 2 倍）；
        // varchar/char 的 n 就是字节数，排序规则只决定"一个字节能装哪个字符"——
        // cp1252 下中文塌成 '?' 是静默损坏，那一条由 UnsafeLengthSemanticsException 在启动期拦。
        // 所以量纲只看类型名，不看排序规则；UTF-8 与否从 collation_name 自己的后缀读得出。
        var charSemantics = storeType is "nvarchar" or "nchar";
        var declared = definitionBytes < 0 ? -1 : charSemantics ? definitionBytes / 2 : definitionBytes;

        return new LengthSemanticsReading(collation, declared, definitionBytes, charSemantics);
    }

    static string StoreType(string storeType, int byteLength, DbDataReader reader) => storeType switch
    {
        "nvarchar" or "nchar" or "varchar" or "char" or "varbinary" or "binary" => byteLength < 0
            ? $"{storeType}(max)"
            : $"{storeType}({(storeType.StartsWith('n') ? byteLength / 2 : byteLength)})",
        "decimal" or "numeric" => $"{storeType}({reader.GetByte(4)},{reader.GetByte(5)})",
        _ => storeType,
    };

    static int? MaxChars(string storeType, int byteLength)
        => byteLength < 0 ? null : storeType is "nvarchar" or "nchar" ? byteLength / 2 : byteLength;

    static int? MaxBytes(string storeType, int byteLength)
        => byteLength < 0 ? null : storeType is "nvarchar" or "nchar" ? byteLength : byteLength;

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

    /// <summary><c>OBJECT_ID</c> 吃的是带引号的两段名，所以这里必须与查询层用同一套标识符策略，
    /// 否则混合大小写的表在 <c>doctor</c> 里会读成"表不存在"。</summary>
    static string Qualified(TableInfo table)
        => table.Schema is { Length: > 0 } schema
            ? $"{IdentifierPolicy.SqlServer.Quote(schema)}.{IdentifierPolicy.SqlServer.Quote(table.Name)}"
            : IdentifierPolicy.SqlServer.Quote(table.Name);
}
