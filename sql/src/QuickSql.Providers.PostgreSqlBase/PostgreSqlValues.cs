using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>PG 系的参数绑定与字面量内联（§4 的差异面收口）。三处与 T-SQL 不同，都不是"换个符号"那么轻：
/// <list type="bullet">
/// <item><b>字符串字面量绝不带 <c>N</c> 前缀</b>：那一家的编码在集群级定死，前缀只会被当成不认识的语法
/// （§10.4 的 <c>N''</c> 是 cp1252 那一族的补丁，不是 SQL 的通用写法）。</item>
/// <item><c>bool</c> 内联成 <c>TRUE</c>/<c>FALSE</c> 而不是 <c>1</c>/<c>0</c>：<c>boolean = integer</c>
/// 在这一家是类型错误，不是隐式转换。（v0.31 的第五家落在 <c>1</c>/<c>0</c> 那一边，所以这一格在五家是
/// <b>2 ∶ 3</b>，而"这一家确实有那两颗字面量"没有替它挡掉这一票 —— 判据是列型与谓词同形，
/// 见 <c>XuguValues</c> 类注释第二条。）</item>
/// <item><c>byte[]</c> <b>拒绝内联</b>：<c>bytea</c> 的字面量写法（<c>'\xcafe'::bytea</c> 与转义式两种）
/// 取决于 <c>bytea_output</c> 这个会话参数，猜错的比"留回占位符"更糟（§3 的 <c>ToLogSql()</c> 允许留占位）。
/// v0.30 的第四家同边而理由不同（包体上限管不着内联），v0.31 的第五家也同边、理由又是第三条
/// （那颗没指明的转义字符 × <c>EMPTY_STR_AS_NULL</c>，见 <c>XuguValues</c> 第三条）——
/// 于是"拒绝内联二进制"从 v0.22 的孤例变成今天的 <b>3 ∶ 2</b>（拒的是这一家、第四、第五家；内联的是
/// T-SQL 的 <c>0x…</c> 与 Oracle 血统的 <c>HEXTORAW('…')</c>），而三家三种理由。</item>
/// </list>
/// 枚举转整数走 <see cref="IConvertible"/> 而不是 <c>Convert.ChangeType</c>：结果一样，而后者在
/// §12.8 第 8 行那一档是要显式记账的热路径形状 —— 少一处就多一处不必解释的落点。</summary>
public sealed class PostgreSqlValues : IValueBinder
{
    public static PostgreSqlValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        // Size 在这一家没有意义（驱动按值自己算），但 AdoParameter 的形状要与 T-SQL 那一侧一致，
        // 否则参数对拍会比不出"两边各要了几颗参数"。hint 原样带上：值级钩子要按列改写参数。
        //
        // DbType 跟着<em>换形之后</em>那一颗走，但只在那一形真的换了的时候：时刻那一格可能把
        // DateTime 换成 DateTimeOffset（带时区的列），型别必须跟着换 —— 否则交出去的是偏移而对
        // 驱动说 DateTime2，撞回同一句拒收。其余情形仍按原值取型，null 那一格才不会被折成 DBNull 的型。
        var shaped = Normalize(value, hint);

        return new AdoParameter(
            name,
            shaped,
            DbTypeOf(value is DateTime ? shaped : value, hint),
            SizeOf(value),
            hint);
    }

    static object? Normalize(object? value, ColumnContract? hint) => value switch
    {
        null => DBNull.Value,

        // 驱动不会替我们转：直接传枚举实例会被当成字符串或报错，而列上是 INTEGER（§9.3）。
        { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),

        // 带偏移的时刻要与列同型（下面那一句），裸时刻要与列同型（同一句的返）。
        DateTime moment => BareOrOffset(moment, hint),

        var typed => typed,
    };

    /// <summary><b>这一句只改交出去的那一形的说法，不改它表示的时刻。</b>
    ///
    /// <para>本家给 <c>DateTime</c> 选的存储型是 <c>TIMESTAMP(3)</c>（不带时区，<c>GetStoreType</c> 那一格），
    /// 而 Npgsql 6 起按 <c>DateTime.Kind</c> 分型：<c>Kind=Utc</c> 只肯进带时区那一型，写进裸
    /// <c>timestamp</c> 就在绑定当场抛 <c>ArgumentException: Cannot write DateTime with Kind=UTC to
    /// PostgreSQL type 'timestamp without time zone'</c>。现场是一颗真业务库：那一族的列全是
    /// <c>timestamp without time zone</c>，而 ABP 的时钟交回的是 <c>Kind=Utc</c> 的钟面 ——
    /// 症状是宿主启动就死、一次登录写一条安全日志就 500，而库里一个字节都没进去。
    /// 旧底座（SqlSugar）发的是它自己那一份参数对象，这一格从来量不到。</para>
    ///
    /// <para><b>裸列</b>收"一串钟面，无所谓时区"，所以把 <c>Utc</c> 的说法摘掉
    /// （<c>SpecifyKind(Unspecified)</c>）是逐字节同文的改写；换成 <c>Local</c> 才是数据改写 ——
    /// 钟面会被挪掉本机那几个小时。<b>带时区的列</b>反过来：那一列存的是瞬时，而驱动按
    /// <c>DbType</c> 认型（<c>DbType.DateTime2</c> 就是裸那一型，钟面再怎么说也换不了它），
    /// 所以那一列要的是换成 <c>DateTimeOffset</c> 的同一瞬时 —— 偏移按 <c>Kind</c> 说：
    /// <c>Utc</c> 与没表过态的都记成零偏移（后者那一格与读侧 <c>TypedReadTests</c> 钉住的
    /// "不猜成 UTC"是<em>另一句话</em>：写侧这一列本来就只收瞬时，猜与不猜都得到一个瞬时），
    /// <c>Local</c> 保留本机偏移。两形的分界只看列声明出来的那个型，不看 CLR 型自己怎么说。</para>
    ///
    /// <para>读侧对应的两臂在 <c>TypedReadTests</c>（<c>ValueConverter</c> 那一族）。钉在
    /// <c>PostgreSqlTimestampBindingTests</c>（离线五臂：两形各一臂、已经对型的两形不许被动、
    /// <c>DateTimeOffset</c> 原样递、文本逐字节同形、别家不受影响）与
    /// <c>PostgreSqlTimestampPathTests</c>（真 PG 一行进去、库里读回同一串钟面）。</para></summary>
    static object? BareOrOffset(DateTime moment, ColumnContract? hint)
    {
        var store = hint?.StoreTypeName;

        var wantsOffset = store is not null
                          && (store.Contains("timestamptz", StringComparison.OrdinalIgnoreCase)
                              || store.Contains("with time zone", StringComparison.OrdinalIgnoreCase));

        if (!wantsOffset)
            return moment.Kind == DateTimeKind.Utc ? DateTime.SpecifyKind(moment, DateTimeKind.Unspecified) : moment;

        return moment.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(moment.Ticks, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(moment.Ticks, TimeZoneInfo.Local.GetUtcOffset(moment)),
            _ => new DateTimeOffset(DateTime.SpecifyKind(moment, DateTimeKind.Utc).Ticks, TimeSpan.Zero),
        };
    }

    /// <summary>枚举的底层类型由声明决定，所以按它取对应宽度：<c>byte</c> 底不许被 widen 成 <c>int</c>，
    /// 否则 <c>DbType.Byte</c> 与实参类型对不上，报错的是驱动而不是这里。</summary>
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

            // 与 T-SQL 同一格理由：DbType.DateTime 上线的是那一家的旧型，而列是 TIMESTAMP(3)。
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

            case string text:
                sql = Literal(text);
                return true;

            case bool flag:
                sql = flag ? "TRUE" : "FALSE";
                return true;

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sql = Invariant(value);
                return true;

            case float or double:
                sql = InlineNumber(Invariant(value));

                // 空串 = 这一格拒了（非有限值）。留回 @p 而不是猜一种字面量写法，理由与 T-SQL 那一族
                // 同一格但不同：这一家**有** NaN 与无穷的写法（<c>'NaN'::double precision</c>，厂商文档口径），
                // 可它是"带引号再 cast"的形状，而裸 <c>NaN</c> 会被解析成列名 —— 现场没有一台库能证这次改写
                // 落在哪种设置下都成立（§13.5 的出处台账），所以宁可不内联。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            case Guid id:
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                sql = Literal(moment.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
                return true;

            case DateTimeOffset withOffset:
                sql = Literal(withOffset.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
                return true;

            case DateOnly day:
                sql = Literal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;

            // 其余类型（enum、TimeSpan、byte[]、未知对象）交回参数路径。
            default:
                sql = string.Empty;
                return false;
        }
    }

    static string Invariant(object? value)
        => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    /// <summary>浮点内联只有一件事要做对：<b>纯整数形式要补小数点，指数式不许补</b>。
    /// 前者是 <c>3</c> 会被当成 integer 常量（<c>real</c> 列上等值比较要走隐式转换，小数位这一档读不出来）；
    /// 后者 <c>1E-08</c> 本来就是合法的 float8 常量，补成 <c>1E-08.0</c> 是语法错。
    /// 这一式在 T-SQL 那一族是 2026-09-22 真库实测出来的（<c>SELECT 1E-08</c> 回读 1E-08，
    /// 而补出来的那一串报「.0 附近有语法错误」，见 <c>SqlServerValues.InlineNumber</c> 的注释）——
    /// 抄过来的时候方向抄对了，抄到的就是<em>修复后</em>那一份形状（下面的判据带着 <c>'E'</c> 那颗档）：
    /// "<c>1E-08.0</c> 两家同源同错"记的是故障史 —— 本家在 E 判据未落地的时候出过同一形，那笔已被修掉，
    /// 不是这份代码的现状；当年撞出它的第二个方言族仍是 §13.5。
    /// <b>这一格今天仍然是"两家"，而它不涨的原因值得记一句</b>：v0.30 的第四家与 v0.31 的第五家抄的都是
    /// <em>修复后</em>那一份（<c>MySqlValues</c> 与 <c>XuguValues</c> 的 <c>InlineNumber</c> 注释各自写明了来源），
    /// 所以"同源同错"那一档的位数停在 2，而这条判据的<em>实现</em>位数是 5 —— 两个数不是一回事，
    /// 前一个数说的是故障史，后一个数说的是副本数。
    /// 非有限值（<c>NaN</c> / <c>∞</c> / <c>-∞</c>）交回空串：这一家有 <c>'NaN'::double precision</c>
    /// 那种"带引号再 cast"的写法，裸 <c>NaN</c> 会被解析成列名，而现场没有一台库能证它在哪种设置下都成立。</summary>
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
