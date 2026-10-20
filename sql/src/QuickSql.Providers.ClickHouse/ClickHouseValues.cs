using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.ClickHouse;

/// <summary>CLR 值 → 参数与字面量（§4 的 IValueBinder）。形状与 <c>Gbase8sValues</c> 同一家族的
/// "参数对象照 ADO 抽象面造"那条路，而本家的分岔只在<b>型号怎么报</b>：
///
/// <para><b>参数在文本里带名字</b>：本家占位符是 <c>@p0</c> 那一形（<see cref="ClickHouseDialect.ParameterPrefix"/>
/// 取 '@'），与另几家里带名字的那几家同档 —— 名字作为键、"参数名不带前缀"的那颗摘前缀纪律
///（<c>AdoExecutor.Bind</c> 那一格）在本家照常活着。内联那一支于是与 GBase 8s 那家"名字进不了文本"的
/// 退化形正相反：<see cref="TryInlineLiteral"/> 产出的字面量与 <c>SqlLogging.ToLogSql</c> 的参数替换
/// 共用同一次发声 —— 该内联的档能内联就给值，不能内联的档保留占位符（<c>XuguValues</c> 那句话的判据）。</para>
///
/// <para>不内联的档：<c>byte[]</c>（类型侧都没有落点，<see cref="ClickHouseTypes"/> 已经抛）、
/// <c>DateTimeOffset</c>/<c>TimeOnly</c>/<c>TimeSpan</c>（同一句）。<b>布尔内联取 true/false 字面量</b>，
/// 与谓词那侧 <c>= true</c>/<c>= false</c> 同一字形、同一次发声（<see cref="ClickHouseDialect.EmitBooleanEquality"/>）。</para>
///
/// <para><b>第一台引擎的读数到过场（2026-10-20，server 26.9.21.9 ∶ 驱动 ClickHouse.Client 7.14.0，
/// 一次活库对账记 §13.5 第十七本台账的本笔回写）</b>：golden 十份里九份逐字执行过；参数形、
/// 保真与分页三式、UUID 往返、布尔三值语义的读数进各格注释；本文件因那次对账改出来的只有
/// Normalize 的 DateTime 一格（参数形换字符串那一档，见那里）。</para></summary>
public sealed class ClickHouseValues : IValueBinder
{
    public static ClickHouseValues Instance { get; } = new();

    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        // 型号与尺寸都跟着归一化后的形状走：Normalize 把 DateTime 化成字符串（写侧毫秒保真那一格），
        // 参数就该以那一串的型号（String）与尺寸过网 —— 用原始值判会把 DbType 报成 DateTime2、
        // 尺寸报成 0，与实际载荷对着干。null 的归一化产物是 DBNull.Value，型号不跟它走
        //（DBNull 不是任何列型的形状），仍按"真 null + hint"同一判（见 DbTypeOf 顶部）。
        var normalized = Normalize(value);

        return new(name, normalized, DbTypeOf(normalized, hint), SizeOf(normalized, hint), hint);
    }

    static object? Normalize(object? value) => value switch
    {
        null => DBNull.Value,

        // 驱动不会替我们转：直接传枚举实例会被当成字符串或报错，而列上是 Int32（§9.3）。
        { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),

        // 【实测·2026-10-20 活库 26.9.21.9 ∶ 驱动 ClickHouse.Client 7.14.0】DateTime 参数在驱动的
        // 序列化里一律不带小数秒：DbType.DateTime2 / DateTime / DateTimeOffset 三种声明都试过，
        // 绑 .123（Id=2）进 DateTime64(3) 落库 .000，重排 .456（Id=3）落库仍 .000 —— 毫秒在写侧
        // 静默丢失。字符串形（"yyyy-MM-dd HH:mm:ss.fff" 不变式）毫秒保真：.222/.555 两手都按原样
        // 落库、读回也原样（DateTime Kind=Utc）。于是参数对齐内联那一支（TryInlineLiteral 的
        // DateTime 臂同一形状）：跨进驱动前化成那一串。DbType 由发型侧对 string 自动落
        // DbType.String、SizeOf 取该串的字符数 —— 驱动侧对两者都不做二次解释。
        DateTime moment => moment.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),

        var typed => typed,
    };

    /// <summary>枚举的底层类型由声明决定，所以按它取对应宽度：byte 底不许被 widen 成 int，
    /// 否则 DbType.Byte 与实参类型对不上，报错的是驱动而不是这里。这一份是五家第六次同形拷贝
    ///（§2 那条禁令造成的重复，与连接器那一格同一判据）。</summary>
    static object EnumAsIntegral(object value)
    {
        var convertible = (IConvertible)value;

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

    /// <summary>按值的形状取宽度（字符数或字节数）。本家没有"参数最长几字节要跟 DDL 同数"那一档
    ///（String 不限长度、<c>ClickHouseTypes</c> 不发带宽度的型名），所以 hint 不参与本家这里的尺寸：
    /// 尺子是仓库侧的（<c>ClickHouseTypes.ApplyStorageDefaults</c>），驱动侧只需要值自己的形状。</summary>
    static int SizeOf(object? value, ColumnContract? hint) => value switch
    {
        null => 0,
        string text => text.Length,
        byte[] bytes => bytes.Length,
        char => 1,
        _ => 0,
    };

    static DbType DbTypeOf(object? value, ColumnContract? hint)
    {
        // null 经 Normalize 归一化成 DBNull.Value 进来：型号不跟 DBNull 走，按"真 null + hint"判
        //（hint?.ClrType 那条路），与归一化之前的读法逐格等价。
        if (value is DBNull)
            value = null;

        var clrType = value?.GetType() ?? hint?.ClrType;

        if (clrType is null)
            return DbType.Object;

        clrType = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (clrType == typeof(string))
            return DbType.String;

        if (clrType.IsEnum)
            return DbType.Int32;

        return clrType switch
        {
            var t when t == typeof(sbyte) => DbType.SByte,
            var t when t == typeof(byte) => DbType.Byte,
            var t when t == typeof(short) => DbType.Int16,
            var t when t == typeof(ushort) => DbType.UInt16,
            var t when t == typeof(int) => DbType.Int32,
            var t when t == typeof(uint) => DbType.UInt32,
            var t when t == typeof(long) => DbType.Int64,
            var t when t == typeof(ulong) => DbType.UInt64,
            var t when t == typeof(bool) => DbType.Boolean,
            var t when t == typeof(float) => DbType.Single,
            var t when t == typeof(double) => DbType.Double,
            var t when t == typeof(decimal) => DbType.Decimal,

            // 列型是 DateTime64(3)。原稿把 DateTime 绑成 DbType.DateTime2【推断·未实测】——
            // 【实测·2026-10-20 活库】三种声明（DbType.DateTime / DateTime2 / DateTimeOffset）在
            // 驱动 7.14.0 的序列化下都不带小数秒，毫秒只在字符串形里保真；Normalize 那一格已把
            // DateTime 化成那一串，所以本臂在正常路径上不可达，服务的只剩
            // "null 值带 DateTime hint"那一条路（型号随 hint 的 ClrType 走）。
            var t when t == typeof(DateTime) => DbType.DateTime2,
            var t when t == typeof(DateOnly) => DbType.Date,
            var t when t == typeof(Guid) => DbType.Guid,
            var t when t == typeof(char) => DbType.String,

            // 到不了：列那一侧在 ClickHouseTypes 就抛了。仍然写出来而不是留 _ => DbType.Object，
            // 让"这一型在绑定层是被认得的、缺的是列"这件事在读代码时看得见（与另几族同一条纪律）。
            var t when t == typeof(DateTimeOffset) => DbType.DateTimeOffset,
            var t when t == typeof(TimeOnly) => DbType.Time,
            var t when t == typeof(TimeSpan) => DbType.Time,
            var t when t == typeof(byte[]) => DbType.Binary,
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
                // 与谓词那侧同一字形（ClickHouseDialect.EmitBooleanEquality 落 = true/ = false）。
                sql = flag ? "true" : "false";
                return true;

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sql = Invariant(value);
                return true;

            case float or double:
                sql = InlineNumber(Invariant(value));

                // 空串 = 这一格拒了（非有限值，NaN/Infinity 裸写会被解析成列名）。
                // ClickHouse 有没有 'NaN' 那种带引号的写法【文档没逐页回读】—— 照 GBase 8s 同格判断，
                // 没有活库能证之前不发。
                return sql.Length > 0;

            case decimal money:
                sql = money.ToString(CultureInfo.InvariantCulture);
                return true;

            case Guid id:
                // 'D' 形只有十六进制与短横，与 UUID 那一列同形（ClickHouseTypes 里那一格的取舍在那段注释）。
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

    /// <summary>浮点内联的那一条判据几族同源，这里是又一份：<b>纯整数形式要补小数点，指数式不许补</b>。
    /// 来源仍是 <c>SqlServerValues.InlineNumber</c> 那份 2026-09-22 真库实测之后的形状
    ///（<c>1E-08.0</c> 是语法错）。本家的 Float32/Float64 列上 <c>3</c> 与 <c>3.0</c> 是不是同一档
    /// 隐式转换未实测，取"与另几族一致"。</summary>
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

    /// <summary>单引号成对转义。前缀那颗 N 上的档位：本家没有 <c>N'</c> 宽字符字面量那一形
    ///（<see cref="ClickHouseDialect.PrefixUnicodeLiterals"/> 保持接口默认 false 与这里是同一句事实的两个出口，
    /// 字符串的编码由库按 UTF-8 存取【文档·ClickHouse：String 数据类型页】）。</summary>
    static string Literal(string value)
    {
        var writer = new SqlWriter(new StringBuilder(), [], '@');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
