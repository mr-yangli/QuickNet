using System.Data.Common;
using FastSql.Metadata;
using FastSql.Providers;

namespace FastSql.Providers.MySqlBase;

/// <summary>从 <c>information_schema</c> 读结构（§4）。这里只有 ADO.NET 抽象层的虚调用，
/// 本文件不认识任何驱动类型（§1.2 原则 3）。
///
/// <para><b>整份读法没有在一台活库上跑过</b>（授权实例里没有一家是 MySQL 血统），所以它的定位是
/// "§9.5 那个 <c>doctor</c> 的只读探测按厂商文档写出来的样子"，而不是"验证过的读结构实现"，
/// 出处与重开条件记在 §13.5 那本台账里。</para>
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
/// 两家都会把 <c>tinyint(1)</c> 认成 <see cref="sbyte"/>）。缺口记 §14，不在这里靠猜。</para></summary>
public sealed class MySqlSchemaReader : ISchemaReader
{
    const string ColumnsSql = """
        SELECT c.column_name, c.data_type, c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.is_nullable, c.ordinal_position
        FROM information_schema.columns c
        WHERE c.table_schema = COALESCE(NULLIF(@schema, ''), DATABASE()) AND c.table_name = @table
        ORDER BY c.ordinal_position
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
        using var command = Command(connection, ColumnsSql);

        command.Parameters.Add(Parameter(command, "schema", table.Schema ?? string.Empty));
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

                columns.Add(new SchemaColumn(name, StoreType(dataType, chars, reader), chars, MaxBytes(reader),
                    reader.GetString(5) is "YES", reader.GetInt32(6)));
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        return new SchemaTable(table, columns);
    }

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
        var chars = reader.IsDBNull(1) ? -1 : reader.GetInt32(1);
        var bytes = reader.IsDBNull(2) ? -1 : reader.GetInt32(2);
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

    static string StoreType(string dataType, int? chars, DbDataReader reader) => dataType switch
    {
        "varchar" => chars is { } n ? $"varchar({n})" : "text",
        "char" => chars is { } n ? $"char({n})" : "char",
        "decimal" => $"decimal({reader.GetInt32(3)},{reader.GetInt32(4)})",
        _ => dataType,
    };

    /// <summary>字节上限在这一家是目录里现成的那一颗：<c>CHARACTER_OCTET_LENGTH</c> 对文本列给
    /// "声明字符数 × 该字符集最大字节宽"、对 <c>binary</c>/<c>varbinary</c> 与各档 <c>BLOB</c> 给真实字节数，
    /// 所以这是落地四家里<em>第二次</em>有一格常规非 null 的答案（第一家是 Oracle 血统那一份的 <c>DATA_LENGTH</c>；
    /// 金仓那一族问不出、T-SQL 那一族要走实测，v0.31 的第五家也问不出 —— 它的宽度与标度共用一颗字段，
    /// 带进字节上限会读出一颗假数，见 <c>XuguSchemaReader</c> 那条纪律）。
    /// 数值列两颗都是 NULL → null，与金仓、达梦两族"问不出就是 null"同一条纪律。</summary>
    static int? MaxBytes(DbDataReader reader)
        => reader.IsDBNull(2) ? null : reader.GetInt32(2);

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
