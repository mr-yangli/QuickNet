using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.Gbase8s;

/// <summary>CLR 值 → 参数与字面量（§4 的 IValueBinder，第六家）。<b>这一份是六家里唯一一处"参数没有名字
/// 可带"的实现</b>：本家占位符是位置形 <c>?</c>（见 <c>Gbase8sDialect.ParameterPrefix</c>），
/// 文本里那一格永远只有一个字符，而 <see cref="AdoParameter.Name"/> 仍然要填 —— 它是内核侧的编号、
/// 是 <see cref="ColumnContract"/> 那颗 hint 的载体，也是 <c>SqlLogging.ToLogSql</c> 找参数的键。
/// 于是本家第一次出现<b>"名字不参与文本"这一档</b>，它的三处后果都记在这里而不是散在别处：
/// <list type="bullet">
/// <item><b>按名回填那条路在这里断了</b>：共用层写侧的 <c>ParameterBinding</c> 钩子按列对上号靠的正是那颗
/// 名字与 hint（§10.1），名字与文本脱钩之后<em>序</em>成为唯一的对得上依据，而序由方言层保证
/// （<c>DialectBase.EmitFunction</c> 那颗核对就是为这一格立的，本家是它第一个消费者）。</item>
/// <item><b><c>ToLogSql</c> 在本家恒等</b>：那颗替换按 <c>prefix + 字母</c> 的形状找占位符，而本家文本里
/// 只有裸 <c>?</c>、后面不跟名字 —— 所以"参数内联给 DBA 看"这一档在本家<em>发不出带值的文本</em>。
/// <c>XuguValues</c> 类注释第一条为这一档立的判据（能内联就给、不能就保留占位符）在这里不适用：
/// 保留的是<em>一颗不带名字的字符</em>，比保留 <c>:p0</c> 更少一格信息。这一格今天不改代码、也不声明
/// 任何"不支持"位，它记 §14 与 <c>QuickSqlDrivers.json</c> 那条本家注。</item>
/// <item><b><c>Size</c> 在这一家不是装饰</b>：量纲恒字节（<c>Gbase8sTypes</c> 第一条）意味着
/// "这一颗参数最长几字节"是<em>库侧</em>的一件真事，而另五家它只在 MySQL 那一条上有过含义。
/// 下面 <see cref="SizeOf"/> 因此比另五家多带一支：hint 有 <c>MaxBytes</c> 时按它报，
/// 让驱动拿到与本家 DDL 同一个数。</item>
/// </list>
///
/// <para>不内联的档与另五家同判：<c>byte[]</c>（没有可证的十六进制函数）、<c>DateTimeOffset</c>
/// （列那一侧根本不存在，<c>Gbase8sTypes</c> 第五条）、<c>TimeSpan</c> 与枚举与未知对象。
/// <b>字符串内联这一支本家取"发"，而它是六家里的第三派</b>：T-SQL 一家不发（那一家有 <c>N'</c> 前缀与
/// 参数化共享计划的纠缠），PG 血统与 Oracle 血统不发（§5.8 的共享计划），MySQL 血统与虚谷发。
/// 本家取发的理由与虚谷同一条而来路不同：<b>位置形占位符本来就不带名字，"文本里出现字面量"与
/// "文本里出现 <c>?</c>"在这一家对服务端计划缓存是同一件事的两端</b>，而 §5.8 那句"共享计划"
/// 在这一家没有可共享的名字。取向记在这里，等一台活库改判。</para></summary>
public sealed class Gbase8sValues : IValueBinder
{
    public static Gbase8sValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
        => new(name, Normalize(value), DbTypeOf(value, hint), SizeOf(value, hint), hint);

    static object? Normalize(object? value) => value switch
    {
        null => DBNull.Value,

        // 驱动不会替我们转：直接传枚举实例会被当成字符串或报错，而列上是 INTEGER（§9.3）。
        { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),

        var typed => typed,
    };

    /// <summary>枚举的底层类型由声明决定，所以按它取对应宽度：<c>byte</c> 底不许被 widen 成 <c>int</c>，
    /// 否则 <c>DbType.Byte</c> 与实参类型对不上，报错的是驱动而不是这里。
    /// 这一份是六家第六次同形拷贝（§2 那条禁令造成的重复，与连接器那一格同一判据），
    /// 而<em>最早那一族是这一判据的反例</em>（它把 <c>Convert.ChangeType</c> 内联在 <c>Normalize</c> 里，
    /// 5 ∶ 1 —— 数法与 <c>XuguValues</c> 类注释末段那条 4 ∶ 1 同一格，本家到场把它长成 5 ∶ 1）。</summary>
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

    /// <summary>与另五家<em>不同</em>的一颗：文本形参数没有名字可查，所以尺寸要按<em>字节</em>报，
    /// 而 hint 上的 <c>MaxBytes</c> 在本家与 DDL 里的 n 是同一个数（<c>Gbase8sTypes.ApplyStorageDefaults</c>）。
    /// 没有 hint 时退回值自己的长度（字符数）—— 那是<em>偏小</em>的一档，本家在这里刻意不猜乘数：
    /// 报小最多让驱动自己量，报大会让驱动按一颗不存在的预算截值。</summary>
    static int SizeOf(object? value, ColumnContract? hint) => value switch
    {
        null => 0,
        string text => hint?.MaxBytes ?? text.Length,
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

            // 与六家同数的一臂。这一家的来路<em>不是</em>"遗留 datetime 先取整到 1/300 秒"那一条
            // （那是 T-SQL 的实测，<c>SqlServerValues.cs</c> 里记着），也不是"列是 DATETIME(3)"那一条
            // （那是虚谷）。本家的理由是：<b>列型是 <c>DATETIME YEAR TO FRACTION(3)</c>，
            // 而 <c>DbType.DateTime</c> 在 ADO.NET 里是<em>不带精度声明</em>的那一颗</b> ——
            // 在一家的字段范围型上，带不带这颗声明不影响文本，所以本家这一位是"与六家同形而没有自己的理由"。
            // 这一句要留着：它是"抄来源时连它的理由一起抄"那一格的反面教材（§13.5 立过两次）。
            var t when t == typeof(DateTime) => DbType.DateTime2,
            var t when t == typeof(DateOnly) => DbType.Date,
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => DbType.Time,
            var t when t == typeof(Guid) => DbType.Guid,
            var t when t == typeof(char) => DbType.String,

            // 到不了：列那一侧在 Gbase8sTypes 就抛了。仍然写出来而不是留 _ => DbType.Object，
            // 让"这一型在绑定层是被认得的、缺的是列"这件事在读代码时看得见（与另五家同一条纪律）。
            var t when t == typeof(DateTimeOffset) => DbType.DateTimeOffset,
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

                // 空串 = 这一格拒了（非有限值）。这一家有没有 'NaN' 那种带引号的写法【文档·IBM 未读到】，
                // 而裸 NaN 会被解析成列名 —— 与金仓、虚谷同一格判断，同一句"没有一台库能证"。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            case Guid id:
                // 'D' 形只有十六进制与短横，与 CHAR(36) 那一列同形（Gbase8sTypes 里那一格的取舍在那段注释）。
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                sql = Literal(moment.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                return true;

            case DateOnly day:
                sql = Literal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;

            default:
                sql = string.Empty;
                return false;
        }
    }

    static string Invariant(object? value)
        => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    /// <summary>浮点内联的那一条判据<em>五</em>家同源，这里是第六份：<b>纯整数形式要补小数点，指数式不许补</b>。
    /// 抄的仍然是 <c>SqlServerValues.InlineNumber</c> 那一份 2026-09-22 真库实测<em>之后</em>的形状
    /// （<c>1E-08.0</c> 是语法错；那一次修复前后的两版在另两族各撞出来一次，见 §13.5 那一格
    /// "第二个方言族才把第一族的错照抄出来"）。本家的 <c>FLOAT</c>/<c>SMALLFLOAT</c> 列上
    /// <c>3</c> 与 <c>3.0</c> 是不是同一档隐式转换<em>未实测</em>，取的是"与另五家一致"。</summary>
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

    /// <summary>单引号成对转义。<b>第三家在这一格里 <c>unicode: false</c></b>（前两家与 T-SQL 各按自己的
    /// <c>N'</c> 档位），而本家不只是"不需要前缀"：<b>这一家没有带前缀的宽字符字面量那一形</b>【文档·IBM】，
    /// 所以 <c>Gbase8sDialect.PrefixUnicodeLiterals</c> 恒 false 与这里是同一句事实的两个出口，
    /// 两处的判据都由"codeset 是建库时定的"那条撑着（<c>Gbase8sTypes</c> 第二条）。</summary>
    static string Literal(string value)
    {
        var writer = new SqlWriter(new StringBuilder(), [], '?');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
