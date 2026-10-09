using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.MySqlBase;

/// <summary>MySQL 系的参数绑定与字面量内联（§4 的差异面收口，第四家）。与 T-SQL / PG 系逐条对照，
/// 三处分岔里有一处是<b>五家里第一家把字符串内联关掉</b>（第五家站在内联那一边，理由见 <c>XuguValues</c> 类注释第一条）：
/// <list type="bullet">
/// <item><b>字符串一律留回占位符</b>：那一家默认把 <c>\</c> 当成字符串里的转义符（除非会话开了
/// <c>NO_BACKSLASH_ESCAPES</c>），而 <see cref="SqlWriter.AppendStringLiteral"/> 只做标准那一半 ——
/// 把单引号翻倍。于是 <c>C:\temp</c> 这类值会在"日志里的 SQL"那一档静默换形（§10.2 说的"三个月后才现形"）。
/// 两种 <c>sql_mode</c> 下没有一种统一的转义写法，而<em>猜错的这一半是改值不是报错</em>，所以拒。
/// 代价是可算的且只在一条路上：<c>TryInlineLiteral</c> 在 <c>src/</c> 里只有 <c>SqlLogging</c> 一个消费方，
/// 也就是说 <c>ToLogSql()</c> 里的字符串参数会留成 <c>@p3</c> 而不是人眼可读的字面量 —— 执行侧一个字节都没变
/// （执行走的是参数，从来不走内联）。这条代价与形状记 §13.5。</item>
/// <item><c>bool</c> 内联成 <c>TRUE</c>/<c>FALSE</c>：这一家确实有这两颗字面量（它们是 1/0 的同义词），
/// 与金仓那一族同一形，而与 T-SQL 的 <c>1</c>/<c>0</c> 分岔。</item>
/// <item><c>byte[]</c> 同样拒绝内联，但理由与 PG 那一族<em>不是</em>同一格：那里是
/// <c>bytea_output</c> 决定字面量写法（两种），这里是内联长度不受参数预算管，只受
/// <c>max_allowed_packet</c> 管 —— 那是会话与部署两侧的数，一张 <c>LONGBLOB</c> 表的内联语句可以
/// 把"预算内"的批量变成"包外"的失败。</item>
/// </list>
/// 另有一处与两族同形而不同字：<b>时刻字面量的分隔符是空格不是 <c>T</c></b>，那是这一家文档里的规范形
/// （它确实也认 <c>T</c>，可"认不认"取决于解析器版本，而日志里的 SQL 是给人复制到客户端里跑的）。
/// <b>v0.31 的第五家同形，所以这一格的"同形"从三族变成四族</b>（带 <c>T</c> 的只剩 T-SQL 与金仓两家），
/// 而两家<em>拿到同一个形状的路</em>不同：这一家来自文档的规范形，那一家来自两份独立产物各写一遍
/// （<c>XuguValues</c> 类注释末段把这一档差写清了 —— 同一颗串在两侧的强度不一样）。
/// 枚举转整数走 <see cref="IConvertible"/> 而不是 <c>Convert.ChangeType</c>：与另两族同一条理由
/// （§12.8 第 8 行那一档要显式记账）。<b>"另两族"要点名，因为它不是"另外那三家"</b>：
/// 走 <c>IConvertible</c> 的是 PG 系、Oracle 血统与 v0.31 的第五家（连同这一家共四份同形代码），
/// 而<b>最早那一族是反例</b> —— <c>SqlServerValues.cs:34</c> 用的就是 <c>Convert.ChangeType</c>，
/// 所以这一格是 <b>4 ∶ 1</b>，判据来自第二族而不是第一族。</summary>
public sealed class MySqlValues : IValueBinder
{
    public static MySqlValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        // Size 在这一家与 PG 系同一格：驱动按值自己算。这里仍然填它，是为了让三家参数清单对拍时
        // "每颗参数带了什么"是可比的，而不是每家各带各的形状。
        // 【v0.31】那句"三家"要按可重数的方式改口：五家今天在断言里逐颗写出 <c>AdoParameter</c> 清单的
        // 只有两份（<c>grep -rln "new AdoParameter(" tests/QuickSql.Tests.Dialects</c> 数得到：
        // <c>SqlServerValueTests.cs</c> 与 <c>KingbaseDialectTests.cs</c>），其余三家比的是
        // <c>ToLogSql</c> 渲染出来的那一行人名与字面量。所以填 <c>Size</c> 的收益今天落在人工对读，
        // 不在自动化对拍 —— 这一格本来就是"多填一颗不要紧的参数"，不是闸门。
        return new AdoParameter(name, Normalize(value), DbTypeOf(value, hint), SizeOf(value), hint);
    }

    static object? Normalize(object? value) => value switch
    {
        null => DBNull.Value,

        // 驱动不会替我们转：直接传枚举实例会被当成字符串或报错，而列上是 INT（§9.3）。
        { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),

        var typed => typed,
    };

    /// <summary>枚举的底层类型由声明决定，所以按它取对应宽度：<c>byte</c> 底不许被 widen 成 <c>int</c>，
    /// 否则 <c>DbType.Byte</c> 与实参类型对不上，报错的是驱动而不是这里。与另两族同一份代码形状
    /// （【v0.31】"另两族"要点名：金仓与 Oracle 血统，加上这一家与第五家就是
    /// <c>grep -rln "static object EnumAsIntegral" src/</c> 的那四份，而最早那一族没有这颗 ——
    /// 它把 <c>Convert.ChangeType</c> 内联在 <c>Normalize</c> 里，见类注释末段那一条 4 ∶ 1）。</summary>
    static object EnumAsIntegral(object value)
    {
        var convertible = (IConvertible) value;

        return Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) switch
        {
            TypeCode.Byte => convertible.ToByte(null),
            TypeCode.SByte => convertible.ToSByte(null),
            TypeCode.Int16 => convertible.ToInt16(null),
            TypeCode.UInt16 => convertible.ToUInt16(null),
            TypeCode.UInt32 => convertible.ToUInt32(null),
            TypeCode.Int64 => convertible.ToInt64(null),
            TypeCode.UInt64 => convertible.ToUInt64(null),
            _ => convertible.ToInt32(null),
        };
    }

    static int SizeOf(object? value) => value switch
    {
        string text => text.Length,
        byte[] bytes => bytes.Length,
        char => 1,
        _ => 0,
    };

    static DbType DbTypeOf(object? value, ColumnContract? hint)
    {
        var clrType = value?.GetType() ?? hint?.ClrType;

        if (clrType is null)
            return DbType.Object;

        clrType = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (clrType == typeof(string))
            return DbType.String;

        if (clrType == typeof(byte[]))
            return DbType.Binary;

        if (clrType.IsEnum)
            return DbType.Int32;

        return clrType switch
        {
            var t when t == typeof(bool) => DbType.Boolean,
            var t when t == typeof(byte) || t == typeof(sbyte) => DbType.Byte,
            var t when t == typeof(short) || t == typeof(ushort) => DbType.Int16,
            var t when t == typeof(int) || t == typeof(uint) => DbType.Int32,
            var t when t == typeof(long) || t == typeof(ulong) => DbType.Int64,
            var t when t == typeof(float) => DbType.Single,
            var t when t == typeof(double) => DbType.Double,
            var t when t == typeof(decimal) => DbType.Decimal,

            // 与另两族同一格理由：DbType.DateTime 上线的是驱动里的旧型，而列是 DATETIME(3)。
            // 【v0.31】那一格的四份同理由副本 = 金仓、Oracle 血统、这一家、第五家；五家取的都是
            // DbType.DateTime2（grep 五处），但最早那一族的理由不是这一条而是实测出来的
            // "遗留 datetime 先把值取整到 1/300 秒"（<c>SqlServerValues.cs:76</c>），同取值而不同来路。
            var t when t == typeof(DateTime) => DbType.DateTime2,
            var t when t == typeof(DateTimeOffset) => DbType.DateTimeOffset,
            var t when t == typeof(DateOnly) => DbType.Date,
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => DbType.Time,
            var t when t == typeof(Guid) => DbType.Guid,
            var t when t == typeof(char) => DbType.String,
            _ => DbType.Object,
        };
    }

    public bool TryInlineLiteral(object? value, ColumnContract? hint, out string sql)
    {
        switch (value)
        {
            case null:
                sql = "NULL";
                return true;

            // 字符串不内联：见类注释第一条（反斜杠与 sql_mode）。这是五家里第一家把这一档关掉。
            case string:
                sql = string.Empty;
                return false;

            case bool flag:
                sql = flag ? "TRUE" : "FALSE";
                return true;

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sql = Invariant(value);
                return true;

            case float or double:
                sql = InlineNumber(Invariant(value));

                // 空串 = 这一格拒了（非有限值）。这一家有 'NaN' 那种"带引号再 +0"的写法，
                // 而裸 NaN 会被解析成列名 —— 与金仓那一族同一格判断，同一句"没有一台库能证"。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            case Guid id:
                // Guid 的 'D' 形只有十六进制与短横，不含反斜杠，所以它可以走 AppendStringLiteral 那一支。
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                sql = Literal(moment.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                return true;

            case DateOnly day:
                sql = Literal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;

            // DateTimeOffset 不内联：这一家根本没有那一型可落（MySqlTypes 在那一格是抛的），
            // 把偏移量写进字面量再等库把它按会话时区折掉，是"内联比参数更积极"的那种错。
            // 其余类型（enum、TimeSpan、byte[]、未知对象）同样交回参数路径。
            default:
                sql = string.Empty;
                return false;
        }
    }

    static string Invariant(object? value)
        => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    /// <summary>浮点内联的那一条判据四家同源（v0.31 的第五家同抄那一份）：<b>纯整数形式要补小数点，指数式不许补</b>。
    /// 抄的是 <c>SqlServerValues.InlineNumber</c> 那一份 2026-09-22 真库实测<em>之后</em>的形状
    /// （<c>1E-08.0</c> 是语法错，而修复前后的两版在另两族各撞出来一次 —— 见 §13.5 那一格"第二个方言族
    /// 才把第一族的错照抄出来"的记录）。这一家的 <c>FLOAT</c>/<c>DOUBLE</c> 列上 <c>3</c> 与 <c>3.0</c>
    /// 是不是同一档隐式转换<em>未实测</em>，取的是"与另两族一致"而不是"这一家更松"。</summary>
    static string InlineNumber(string text)
    {
        // 判整串字符集而不是判首字符：`-∞` 打头那颗 `-` 是合法的浮点首字符，只判首字符会放它过来。
        foreach (var c in text)
            if (!char.IsAsciiDigit(c) && c is not ('-' or '+' or '.' or 'E' or 'e'))
                return string.Empty;

        if (!text.Contains('.') && !text.Contains('E', StringComparison.OrdinalIgnoreCase))
            text += ".0";

        return text;
    }

    static string Literal(string value)
    {
        var writer = new SqlWriter(new StringBuilder(), [], '@');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
