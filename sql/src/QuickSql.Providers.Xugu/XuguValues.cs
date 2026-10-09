using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的参数绑定与字面量内联（§4 的差异面收口，第五家）。三处与四族对照，每处都落在
/// <c>TryInlineLiteral</c> 那一档而不是执行路径上 —— 先说清这一格的<em>代价边界</em>：
/// <c>TryInlineLiteral</c> 在 <c>src/</c> 里只有 <c>SqlLogging</c> 一个消费方，所以本文件所有取舍
/// 改变的是"日志里那条能直接复制去跑的 SQL"，执行侧一个字节都不走这条路（它走参数）。
/// 这句话在 MySQL 血统那一版是第一次被需要（那边关掉字符串内联是一笔看得见的代价），到这里变成第五家
/// 同一格的<em>读法前提</em>，所以写在最前面而不是藏在某一条里。
///
/// <list type="bullet">
/// <item><b>字符串<em>内联</em></b>，与 T-SQL / PG 系 / Oracle 血统三家同形，而<em>不</em>跟第四家：
/// 那一家拒的理由是"默认把 <c>\</c> 当转义符、且这件事由会话模式决定，两种模式下没有一种统一的写法"
/// （<c>MySqlValues</c> 类注释第一条）。这一家两边都有出处：厂商那一页给的写法是"单引号翻倍 <c>''</c>，
/// 或用转义字符"【文档】—— 而<em>没写那颗转义字符是哪一颗</em>；一份 shipped 适配器只做了翻倍那一半
/// （<c>Replace("'", "''")</c>）【产物】。两条合起来读作：<b>翻倍是两边共同认可的那一支，而"还有另一支"这件事
/// 只在文档里留了一句没有指代的话</b>。所以这里取翻倍那一支，并把那一句悬空的话记 §14：
/// 与 <c>MySqlValues</c> 那一格同一重开条件（谁接了会话模式建模，两边一起重看）。
/// 上面那句"只影响日志"仍然是这一格可以取多数派的原因。</item>
/// <item><b>布尔内联成 <c>1</c>/<c>0</c></b>，与 T-SQL、Oracle 血统两家同形，与金仓、MySQL 两家的
/// <c>TRUE</c>/<c>FALSE</c> 分岔。这一格上一版设计稿取的是 <c>TRUE</c>（理由是"驱动登记了 <c>bool</c> 这一型"），
/// 交账前按<em>三处一致性</em>改判：<see cref="XuguTypes.GetStoreType"/> 把布尔落成 <c>TINYINT</c>、
/// <c>DialectBase.EmitBooleanEquality</c> 这一家发的是 <c> = 1</c>、而内联若发 <c>TRUE</c>，
/// 就得到"列是整数、谓词比整数、字面量是布尔"三处各说一句话 —— 猜错的那一半是 INSERT 直接类型不匹配。
/// 取 <c>1</c>/<c>0</c> 让三处落在同一个形状上，而它同时是五家分布从 2∶2 变成 <b>3∶2</b> 的那一票。
/// <b>这一家确实有 <c>TRUE</c>/<c>FALSE</c> 两颗字面量</b>（<c>BOOLEAN</c> 那一型取值 <c>TRUE|FALSE|NULL</c>，
/// 且厂商另给了一句"数字非零即真"【文档】），所以这里不是"库里没有那两颗"而是"那两颗与我们的列不同型" ——
/// 与 <c>MySqlValues</c> 那条"有字面量而仍不声明 Boolean_Type"是同一条判据，而这一家的强度更高一档：
/// 那边是 <c>TINYINT(1)</c> 的同义字，这边是<em>真布尔型</em>。三处一致的取舍与它的改判条件都写在
/// <c>XuguTypes</c> 那一格的注释里。</item>
/// <item><b>二进制<em>不</em>内联</b>，与 Oracle 血统那一家分岔（那边发 <c>HEXTORAW('CAFE')</c>）。
/// 判据仍然按形状而不按族：<c>HEXTORAW</c> 之所以能内联，是因为它的入参只有十六进制这一种解释。
/// 这一家有一份产物发的是 <c>'\x…'</c> 那一形【产物】，而"这一颗 <c>\x</c> 前缀在会话参数
/// <c>EMPTY_STR_AS_NULL</c> 与那颗没指明的转义字符之下<em>各</em>读成什么"两边出处都没说 ——
/// 字面量猜错的方向是语法错或改值，两种都比"日志里留一颗 <c>:p3</c>"糟。与 MySQL 那一档同一后果、
/// 不同理由（那边是包体上限管不着内联，这边是写法有两种解释）。</item>
/// </list>
/// 另两处与两家同形而值得点出来：<b>时刻字面量的分隔符是空格不是 <c>T</c></b>（格式串
/// <c>yyyy-MM-dd HH:mm:ss.fff</c> 是这一家<em>两处产物各写一遍</em>的同一个串【产物】，
/// 与 MySQL 血统那条"文档里的规范形"不同档 —— 那边是文档，这里是两个独立实现），
/// 以及枚举转整数走 <see cref="IConvertible"/> 而不是 <c>Convert.ChangeType</c>（§12.8 第 8 行的记账纪律）。
/// <b>这一句初稿写的是"五家同源"，交账前按 <c>grep</c> 改成四家</b>：<c>SqlServerValues.cs:34</c>
/// 用的正是 <c>Convert.ChangeType</c>，也就是最早那一族是这一判据的<em>反例</em>，
/// 而抄了这条判据的是 PG 系、Oracle 血统、MySQL 血统与这一家（4 ∶ 1）。
/// 抄来源时把上一家的形状连它的例外一起抄了，是这一版自己撞出来的第二处口径错，记 §13.5。</summary>
public sealed class XuguValues : IValueBinder
{
    public static XuguValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        // Size 与 PG 系、MySQL 血统同一格：驱动按值自己算。这里仍然填它，是为了让五家参数清单对拍时
        // "每颗参数带了什么"是可比的，而不是每家各带各的形状。【v0.31】那句"五家…对拍"要说窄一点才立得住：
        // 逐家写出 <c>AdoParameter</c> 清单断言的测试今天只有两份（<c>grep -rln "new AdoParameter("
        // tests/QuickSql.Tests.Dialects</c> 数得到），其余三家比的是 <c>ToLogSql</c> 那一行，
        // 所以这一格的收益落在人工对读而不是自动化对拍；同一句在 <c>MySqlValues.CreateParameter</c> 里一起重数。
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
    /// 否则 <c>DbType.Byte</c> 与实参类型对不上，报错的是驱动而不是这里。
    /// <b>与另三家同一份代码形状</b>（本条初稿写作"另四族"，交账时按 <c>grep -rln "static object
    /// EnumAsIntegral" src/</c> 推翻：那颗只在这四份文件里存在，最早那一族把 <c>Convert.ChangeType</c>
    /// 内联在 <c>Normalize</c> 里，所以它是这一判据的反例而不是第四份 —— 与类注释末段那条 4 ∶ 1 同一格）。
    /// 五家取 <c>DbType.DateTime2</c> 的倒是全五家（同一条取值、两条来路，见下方 <c>DbTypeOf</c> 那一臂）。</summary>
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

            // 与另四族同一格理由：DbType.DateTime 上线的是驱动里的旧型，而列是 DATETIME(3)。
            // 【v0.31 重数】这一臂五家取的都是 DateTime2，但"同一格理由"只有四家：最早那一族的
            // 理由是实测出来的"遗留 datetime 先取整到 1/300 秒"（<c>SqlServerValues.cs:76</c>），
            // 同取值而不同来路，与 <c>MySqlValues</c> 同一处一起重数。
            var t when t == typeof(DateTime) => DbType.DateTime2,

            // 这一行今天到不了：列那一侧在 XuguTypes 就抛了。仍然写出来而不是留 _ => DbType.Object，
            // 是为了让"这一型在绑定层是被认得的、缺的是列"这件事在读代码时看得见 ——
            // 那一格真正的缺口在驱动的 CLR 表里（XuguTypes 类注释第三条），不在这一行。
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

            // 字符串内联，与第四家相反：理由与重开条件见类注释第一条。
            case string text:
                sql = Literal(text);
                return true;

            case bool flag:
                sql = flag ? "1" : "0";
                return true;

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sql = Invariant(value);
                return true;

            case float or double:
                sql = InlineNumber(Invariant(value));

                // 空串 = 这一格拒了（非有限值）。这一家有没有 'NaN' 那种带引号的写法【没有出处】，
                // 而裸 NaN 会被解析成列名 —— 与金仓那一族同一格判断，同一句"没有一台库能证"。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            case Guid id:
                // 'D' 形只有十六进制与短横，与 CHAR(36) 那一列同形（XuguTypes 里那一格的取舍在那段注释）。
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                sql = Literal(moment.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                return true;

            case DateOnly day:
                sql = Literal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;

            // 不内联的三类与理由：byte[]（没有可信的十六进制函数，类注释第三条）、DateTimeOffset
            // （列那一侧根本不存在，把它写进文本是"内联比参数更积极"的那种错）、
            // 以及 TimeSpan / 枚举 / 未知对象（一律交回参数路径，与四族同形）。
            default:
                sql = string.Empty;
                return false;
        }
    }

    static string Invariant(object? value)
        => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    /// <summary>浮点内联的那一条判据<em>四</em>家同源，这里是第五份：<b>纯整数形式要补小数点，指数式不许补</b>。
    /// 抄的仍然是 <c>SqlServerValues.InlineNumber</c> 那一份 2026-09-22 真库实测<em>之后</em>的形状
    /// （<c>1E-08.0</c> 是语法错，而修复前后的两版在另两族各撞出来一次 —— 那两族要点名：<b>T-SQL 与金仓</b>，
    /// 也就是"抄的人撞上被抄的人在修的那个错"，见 §13.5 那一格"第二个方言族
    /// 才把第一族的错照抄出来"）。这一家的 <c>FLOAT</c>/<c>DOUBLE</c> 列上 <c>3</c> 与 <c>3.0</c>
    /// 是不是同一档隐式转换<em>未实测</em>，取的是"与另四族一致"而不是"这一家更松"。</summary>
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
        var writer = new SqlWriter(new StringBuilder(), [], ':');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
