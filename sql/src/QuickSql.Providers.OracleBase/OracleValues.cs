using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.OracleBase;

/// <summary>Oracle 血统的参数绑定与字面量内联（§4 的差异面收口，第三家）。四处与已落地的两族不同：
/// <list type="bullet">
/// <item><b>不带 <c>N</c> 前缀</b>：<c>N'…'</c> 在这一家是 <c>NVARCHAR2</c> 的字面量，而映射走的是
/// <c>VARCHAR2(n CHAR)</c>（<see cref="OracleTypes"/>）—— 加前缀等于给一次比较塞进一次隐式转换。
/// 与金仓那一族同结论、不同理由（那一家是"前缀会被当成不认识的语法"）。</item>
/// <item><b>布尔内联成 <c>1</c>/<c>0</c></b>：这一族没有原生布尔（§9.3 那一列），与 T-SQL 同形、与金仓分岔。
/// 这一格今天有两票要结算：v0.30 的第四家落在 <c>TRUE</c>/<c>FALSE</c> 那一边（它的列是 <c>TINYINT(1)</c>
/// 而内联仍发字面量），v0.31 的第五家落进<b>这一边</b>，于是五家是 <c>3 ∶ 2</c>，
/// 而第五家给出的理由与这一族"库没有那一型"不同一条 —— 它<em>有</em>真 <c>BOOLEAN</c> 型而不取，
/// 为的是列/谓词/字面量三处同形（<c>XuguValues</c> 类注释第二条）。</item>
/// <item><b>二进制可以内联：<c>HEXTORAW('CAFE')</c></b>。金仓那一族拒内联的理由是
/// <c>bytea_output</c> 那一颗会话参数（同一个字面量在两个设置下读出两个值），而 <c>HEXTORAW</c>
/// 的入参只有十六进制这一种解释 —— 会话里没有能改它含义的开关。这就是"能不能内联"为什么按形状判而不是按族判。
/// <b>那句"按形状判"到 v0.31 有了四条证据</b>：拒内联的从金仓一家变成三家（金仓 = 会话参数、
/// MySQL 血统 = 包体上限管不着内联、第五家 = 那颗 <c>'\x…'</c> 前缀有两种解释），
/// 内联的两家（T-SQL 的 <c>0x…</c> 与这里的 <c>HEXTORAW</c>）共同点也都是"入参只有一种解释" ——
/// 三 ∶ 二，而<em>没有一家</em>的理由是"对面那一家拒所以我拒"。</item>
/// <item><b>日期时间必须写成函数调用</b>：<c>TIMESTAMP '…'</c> 那种 ANSI 字面量吃会话的
/// <c>NLS_TIMESTAMP_FORMAT</c> 与不吃的写法在这一族里是同一条语句的两种命运，所以内联一律走
/// <c>TO_TIMESTAMP(文本, 格式)</c>：格式串写死在语句里，会话说什么都不影响它读出什么。
/// 这是本族第一处"同一颗槽位、三家三样形状"的落点（T-SQL 用 <c>'…'</c> 加 <c>{dt'…'}</c>、金仓用带引号的 ISO 串）。
/// <b>"三家三样"这个数字到 v0.31 没涨，涨的是每样几家</b>：五家三样 = 带 <c>T</c> 的裸串两家
/// （T-SQL、金仓）、<c>TO_TIMESTAMP</c> 一家（这一族）、空格分隔的裸串两家（v0.30 的第四家与 v0.31 的第五家）。
/// 第五家与这一族<em>同格式串</em>而<em>不同包装</em>（它不套函数），那一格读法见 <c>XuguValues</c> 类注释末段。</item>
/// </list>
/// 枚举转整数走 <see cref="IConvertible"/> 而不是 <c>Convert.ChangeType</c>：与两族同一条纪律
/// （§12.8 第 8 行那档要显式记账的热路径形状），少一处就多一处不必解释的落点。
/// 这句"两族"今天读作<b>三家</b>而不是四家：v0.30 的第四家与 v0.31 的第五家都在这一边（两家的
/// <c>EnumAsIntegral</c> 与这里逐字同形），而<b>最早那一族不在</b> —— <c>SqlServerValues.cs:34</c>
/// 用的正是 <c>Convert.ChangeType</c>，它是 §12.8 第 8 行那颗 <c>Convert.</c> 计数里的一处。
/// 所以这一格的分布是 <b>4 ∶ 1</b>，而"谁定的判据"与"谁守判据"不同一条 —— 记这条不是因为
/// 第五家带来了新形状，是因为它<em>抄的是第二家</em>，顺手把第一族那处反例照了出来。</summary>
public sealed class OracleValues : IValueBinder
{
    public static OracleValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        // 与两族同一形状：Size 在这一家由驱动自己算，带上只是为了参数对拍能比出"两边各要了几颗参数"。
        return new AdoParameter(name, Normalize(value), DbTypeOf(value, hint), SizeOf(value), hint);
    }

    static object? Normalize(object? value) => value switch
    {
        null => DBNull.Value,

        // 列上是 NUMBER(10)（§9.3），传枚举实例过去会被当成字符串或报错，两族同一处理。
        { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),

        var typed => typed,
    };

    /// <summary>枚举的底层类型由声明决定，所以按它取对应宽度 —— 与两族逐字同形的那一句：
    /// 绑宽的尺子不许是"我觉得 int 够了"，<c>byte</c> 底被 widen 成 <c>int</c> 之后是
    /// <c>DbType.Byte</c> 与实参类型对不上，报错的是驱动而不是这里。</summary>
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
        Guid => 36,
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

            // 与两族同一格理由的第三家版本：DbType.DateTime 上线的是 ADO 那个带毫秒的旧型，
            // 而列是 TIMESTAMP(3)。TIMESTAMP(3) WITH TIME ZONE 那一列要的是 DateTimeOffset，
            // 所以这一格没有第三种选择 —— 前缀与 Type 都不许现场猜，都由值自己的型决定。
            var t when t == typeof(DateTime) => DbType.DateTime2,
            var t when t == typeof(DateTimeOffset) => DbType.DateTimeOffset,
            var t when t == typeof(DateOnly) => DbType.Date,
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => DbType.Time,
            var t when t == typeof(Guid) => DbType.String,
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
                sql = flag ? "1" : "0";
                return true;

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sql = Invariant(value);
                return true;

            case float or double:
                sql = InlineNumber(Invariant(value));

                // 空串 = 这一格拒了（非有限值）。这一族的 NaN 与无穷只有"带引号再 cast"那一种写法
                // （TO_BINARY_DOUBLE('NaN', ...) 之类），而它依赖会话的 NLS_NUMERIC_CHARACTERS ——
                // 与金仓那一族拒 bytea 同一条判据：文本的含义由会话决定时，宁可留回占位符。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            // CHAR(36)：文本形态就是这一列的物理形态，所以内联写字符串而不是写什么原生字面量。
            case Guid id:
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                sql = $"TO_TIMESTAMP({Literal(moment.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))},'YYYY-MM-DD HH24:MI:SS.FF3')";
                return true;

            case DateTimeOffset withOffset:
                // 文本里的 "+08:00" 与格式串里的 TZH:TZM 是一对：这一族读带时区的时间戳没有"按会话
                // 时区解释一个裸串"那一条退路（那一条正是金仓那一族裸串的风险），所以偏移量必须写在文本里。
                sql = $"TO_TIMESTAMP_TZ({Literal(withOffset.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))},'YYYY-MM-DD HH24:MI:SS.FF3 TZH:TZM')";
                return true;

            case DateOnly day:
                sql = $"TO_DATE({Literal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))},'YYYY-MM-DD')";
                return true;

            case byte[] bytes:
                sql = $"HEXTORAW({Literal(Convert.ToHexString(bytes))})";
                return true;

            // 其余类型（enum、TimeSpan、char、未知对象）交回参数路径：
            // TimeSpan 的字面量要写成 INTERVAL '0 0:00:01.000' DAY TO SECOND，间隔文本的引号里
            // 是"带不带小数秒、几位"两个会话相关的事，不值当为一条日志文本赌一次。
            default:
                sql = string.Empty;
                return false;
        }
    }

    static string Invariant(object? value)
        => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    /// <summary>浮点内联在这一族<b>只有一件事要做：拒掉非有限值</b>。三家三样形状的第三份：
    /// <list type="bullet">
    /// <item>T-SQL 与金仓要"纯整数形式补小数点"（<c>3</c> 会被当成整数常量，而 <c>real</c>/<c>float8</c>
    /// 列上等值比较要走隐式转换，小数位那一档读不出来 —— T-SQL 那一格是 2026-09-22 真库实测的）。</item>
    /// <item>这一族<b>不需要</b>：NUMBER 没有整数与浮点之分，<c>3</c> 与 <c>3.0</c> 是同一个值的两种写法，
    /// 补那一个 <c>.0</c> 只会让黄金样本比出一次没有语义变化的改动。</item>
    /// <item>两族共用的"不许给指数式补小数点"这一判在这里没有对象 —— 但字符集校验留着，
    /// 它挡的是 <c>NaN</c> 与 <c>∞</c> 那些非 ASCII 形状，与补点这一判是两件事。</item>
    /// </list>
    /// <b>【v0.31】那句"三家三样"塌成了两样，而且这笔账本该在 v0.30 就结</b>：五家的分布是
    /// <b>补点四家（T-SQL、金仓、MySQL 血统、第五家）∶ 不补这一族一家</b>，判据可重数 ——
    /// <c>grep -rln 'text += "' src/QuickSql.Providers.*/</c> 里 <c>Values.cs</c> 那五份命中四份，
    /// 没命中的正是这一族。方向要说清：第四家当时就在补点那一侧（它抄的是被抄者修完 <c>1E-08.0</c>
    /// 之后的那一版），所以"三样"从 v0.30 起就已经是"两样"，本版只是把漏数的那一格按 grep 钉回来；
    /// 第五家带来的不是新形状，是<b>第一例"新家族没有增加样数、只增加多数派家数"的格</b> ——
    /// 而这一格能这么读，恰恰因为上面第三条那句"这一判在这里没有对象"是这一族独有的语义，
    /// 样数少一家并不把它变成多数派。</summary>
    static string InlineNumber(string text)
    {
        foreach (var c in text)
            if (!char.IsAsciiDigit(c) && c is not ('-' or '+' or '.' or 'E' or 'e'))
                return string.Empty;

        return text;
    }

    static string Literal(string value)
    {
        var writer = new SqlWriter(new StringBuilder(), [], ':');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
