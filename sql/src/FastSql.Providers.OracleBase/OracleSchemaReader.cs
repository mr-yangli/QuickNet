using System.Data.Common;
using FastSql.Metadata;
using FastSql.Providers;

namespace FastSql.Providers.OracleBase;

/// <summary>从 <c>ALL_TAB_COLUMNS</c> 读结构（§4 里差异最大的那一格，第三家）。
/// 与两族同一纪律：这里只有 ADO.NET 抽象层的虚调用，本文件不认识任何驱动类型（§1.2 原则 3）。
///
/// <para><b>整份读法没有在一台活库上跑过</b>（现场没有授权的 Oracle 血统实例），所以它的定位是
/// "§9.5 那个 <c>doctor</c> 的只读探测按厂商文档写出来的样子"，出处与重开条件记在 §13.5。</para>
///
/// <para>两处与两族的目录视图不同形，都不是换个表名（<b>【v0.31】那两族今天是三族，而这两处没有一处是靠"多一家"站住的</b>：
/// 第五家把这两样<em>都</em>抄了去 —— 它的目录 SQL 里同样有 <c>NVL(:owner, USER)</c>、绑进去的名字同样先过一颗
/// <c>Fold</c>（见 <c>XuguSchemaReader</c> 的 <c>ColumnsSql</c> 与它自家的 <c>Fold</c>），所以这一档的分布不是
/// "一家对三家"而是 <b>2 ∶ 2</b>（Oracle 血统与虚谷同侧，T-SQL 与 PG 系同侧）。判据可重数：
/// <c>grep -rln "NVL(:owner, USER)" src/ --include=*.cs</c> 交出两份文件，正是这两族的 <c>SchemaReader</c>；
/// 而这一句在两族各只出现一次 —— Oracle 这份的 <c>CHAR_USED</c> 那条 SQL 里另有两颗 <c>NVL</c>，
/// 它们兜的是别的 null，所以"整词的 <c>NVL(</c> 命中 7 处（剔 <c>///</c> 之后）"与"这一句命中两份文件"
/// 是两个数，别互相搬）：
/// <list type="bullet">
/// <item><b>目录里存的是折叠后的大写</b>：<c>OWNER</c>/<c>TABLE_NAME</c>/<c>COLUMN_NAME</c> 三颗都是。
/// 所以绑进去的值要<em>自己折</em>（<see cref="Fold"/>）—— <c>EmitIdentifier</c> 那一条路在这里够不着，
/// 因为参数值不进 SQL 文本。漏掉这一步的后果与 §9.2 那句"查询 100% 报表不存在"同一格，只是发生在
/// <c>doctor</c> 侧：读成"这张表没有列"。</item>
/// <item><b>模式为 null 说的是"当前会话用户"</b>，与 <see cref="OracleCodeFirst"/> 那句"什么都不写"是同一件事：
/// 这里绑一颗 <c>NULL</c> 而 SQL 里用 <c>NVL(:owner, USER)</c> 接住，于是读侧与建侧指向同一个默认。
/// 补一个假模式名（<c>dbo</c>/<c>public</c> 那两家的写法）在这一族会得到 <c>ORA-00942</c>。
/// <b>v0.31 的第五家把这一格整条搬走了</b>：它的目录 SQL 里同样是 <c>NVL(:owner, USER)</c>
/// （<c>XuguSchemaReader.ColumnsSql</c>），于是"什么都不写 + 绑一颗 NULL 让库接住"从这一族的
/// 一处判断变成两族的同一份形状，而<em>补假名字</em>那一边仍然是两家（T-SQL 的 <c>dbo</c>、PG 系的 <c>public</c>）。</item>
/// </list></para>
///
/// <para>第三处是这一族的<em>好消息</em>：§10.4 那个"这一列的长度到底是字节还是字符"的问题在这里有直接答案 ——
/// <c>CHAR_USED</c> 那一列写的就是 <c>'C'</c>（字符）或 <c>'B'</c>（字节），<c>VARCHAR2(200 CHAR)</c> 的
/// <c>CHAR_LENGTH</c> 是 200 而 <c>DATA_LENGTH</c> 是 800。两族里 T-SQL 要看类型名与排序规则推断、
/// 金仓那一族压根问不出来（库里只有一个答案），这一族是三者中唯一"列上写着"的 ——
/// <b>那句"三者中唯一"到 v0.31 已经数不出唯一了，而且它是被 v0.30 那一家打破的（本版复核时才看见，
/// 上一版的漏账记在这里）</b>：MySQL 血统那一家把两颗数放在<em>同一行</em>
/// （<c>CHARACTER_MAXIMUM_LENGTH</c> 与 <c>CHARACTER_OCTET_LENGTH</c>，见 <c>MySqlSchemaReader</c>），
/// 所以"目录里读得到两个宽度"这一档现在是<b>两家</b>；仍然<em>只</em>属于这一族的是那颗
/// <c>CHAR_USED</c> —— 它说的是"这一列<em>当初按什么声明</em>"，而 MySQL 那一行给的是"这一列现在最多几字节"，
/// 后者算不出前者（同一颗 <c>VARCHAR(50)</c> 在 <c>utf8mb4</c> 与 <c>latin1</c> 表上给出两个八度）。
/// 分布 2 ∶ 1 ∶ 2 与逐家出处见 <c>XuguSchemaReader</c> 类注释末段。
/// 而它之所以仍然是好消息，正因为 <see cref="OracleTypes"/> 把量纲写进了列定义而不是赌会话默认值。</para></summary>
public sealed class OracleSchemaReader : ISchemaReader
{
    const string ColumnsSql = """
        SELECT c.column_id, c.column_name, c.data_type, c.char_length, c.data_length, c.char_used, c.data_precision, c.data_scale, c.nullable
        FROM all_tab_columns c
        WHERE c.owner = NVL(:owner, USER) AND c.table_name = :table
        ORDER BY c.column_id
        """;

    /// <summary>§10.4 的量纲探测，只读目录视图与会话参数：不建临时表、不写数据（§9.5 那句"生产库常禁 DDL 权限"）。
    /// 尾巴那颗子查询取的是 <c>NLS_LENGTH_SEMANTICS</c> —— 它填进 <c>CollationName</c> 那一格是有意的错位：
    /// 这一族决定"裸 <c>VARCHAR2(n)</c> 里的 n 是什么"的不是排序规则而是这一颗会话参数，
    /// 而 <c>doctor</c> 要报的是"这一列的量纲从哪来"，所以这里带的是出处而不是一个同名不同义的字。</summary>
    const string ProbeSql = """
        SELECT c.data_type, c.char_length, c.data_length, NVL(c.char_used, '?'),
               (SELECT NVL(p.value, '?') FROM nls_session_parameters p WHERE p.parameter = 'NLS_LENGTH_SEMANTICS')
        FROM all_tab_columns c
        WHERE c.owner = NVL(:owner, USER) AND c.table_name = :table AND c.column_name = :column
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        using var command = Command(connection, ColumnsSql);

        command.Parameters.Add(Parameter(command, "owner", Fold(table.Schema)));
        command.Parameters.Add(Parameter(command, "table", Fold(table.Name)));

        var columns = new List<SchemaColumn>();

        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                // 序号在前（与 T-SQL 那一份同一个列序习惯），列名在第二格 —— 而它必须是折叠过的那一份：
                // 契约侧比对的是 C# 成员映射出的列名，读回来一个 "name" 就对不上。
                var name = reader.GetString(1);
                var dataType = reader.GetString(2);
                var chars = reader.GetInt32(3);
                var bytes = reader.GetInt32(4);

                columns.Add(new SchemaColumn(name, StoreType(dataType, chars, bytes, reader),
                    MaxChars(dataType, chars), MaxBytes(dataType, bytes), reader.GetString(8) is "Y", reader.GetInt32(0)));
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        return new SchemaTable(table, columns);
    }

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        using var command = Command(connection, ProbeSql);

        command.Parameters.Add(Parameter(command, "owner", Fold(table.Schema)));
        command.Parameters.Add(Parameter(command, "table", Fold(table.Name)));
        command.Parameters.Add(Parameter(command, "column", Fold(columnName)));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var dataType = reader.GetString(0);
        var chars = reader.GetInt32(1);
        var bytes = reader.GetInt32(2);
        var used = reader.GetString(3);

        // 'C' 是字符、'B' 是字节、'?' 是这一列压根没有长度语义（NUMBER/DATE）。
        // 与金仓那一族的"两侧填同一个数"不同：这一族的 DATA_LENGTH 真的是字节，所以这里填出来的两个数
        // 是两把尺子，而 doctor 报"声明 200 字符 / 800 字节"在这一家是有意义的而不是编出来的。
        return new LengthSemanticsReading(reader.GetString(4), chars, bytes, used is "C");
    }

    static string StoreType(string dataType, int chars, int bytes, DbDataReader reader) => dataType.ToUpperInvariant() switch
    {
        // 量纲限定词一起还原：读回来的类型名要能与建出去的列定义逐字对上，doctor 的比对才是同一份字形。
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" when chars > 0 && chars != bytes => $"{dataType}({chars} CHAR)",
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" when chars > 0 => $"{dataType}({chars})",
        "NUMBER" => reader.IsDBNull(6)
            ? dataType
            : $"NUMBER({reader.GetInt32(6)},{reader.GetInt32(7)})",

        // TIMESTAMP(3) 的 3 在 DATA_SCALE 里；DATE 那一型在这一族本来就带时分秒（§9.3 那句坑），
        // 所以还原时不给它加限定词，留给类型表按名字认。
        "TIMESTAMP" or "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMP WITH LOCAL TIME ZONE"
            => reader.IsDBNull(7) ? dataType : $"{dataType}({reader.GetInt32(7)})",
        _ => dataType,
    };

    /// <summary>字符上限只对"有长度语义"的那几型有意义；<c>NUMBER</c> 的 precision 不是字符数，
    /// 带进去会让 <see cref="ColumnContract.FitsBytes"/> 那类判据读出一个假上限。</summary>
    static int? MaxChars(string dataType, int chars)
        => chars > 0 && IsText(dataType) ? chars : null;

    static int? MaxBytes(string dataType, int bytes)
        => bytes > 0 && IsText(dataType) || dataType.Equals("BLOB", StringComparison.OrdinalIgnoreCase) || dataType.Equals("RAW", StringComparison.OrdinalIgnoreCase)
            ? bytes
            : null;

    static bool IsText(string dataType)
        => dataType.Equals("VARCHAR2", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("NVARCHAR2", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("CHAR", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("NCHAR", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("CLOB", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("NCLOB", StringComparison.OrdinalIgnoreCase);

    /// <summary>目录里的名字是大写形（见类注释第二条），所以绑进去的也必须是大写形。
    /// null 原样留 null：<see cref="OracleCodeFirst"/> 那一句"模式为空就不限定"在这里对应 <c>NVL(:owner, USER)</c>。</summary>
    static object? Fold(string? identifier)
        => identifier is { Length: > 0 } name ? name.ToUpperInvariant() : DBNull.Value;

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    /// <summary>参数名前缀是冒号而不是 <c>@</c>（§9.4 那颗 <c>ParameterPrefix</c> 在目录查询这一侧的同一件事）：
    /// 这一族的驱动认 <c>:name</c>，带 <c>@</c> 的写法会被当成一个名字里含 <c>@</c> 的绑定变量。</summary>
    static DbParameter Parameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = ':' + name;
        parameter.Value = value ?? DBNull.Value;

        return parameter;
    }
}
