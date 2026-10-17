using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>CLR ↔ PG 系类型名（§9.3 那一列）。三家共同的分岔点，与 T-SQL 逐条对照：
/// （"三家"是 v0.23 的口径，这一列今天有五行。新进来的两家<em>没有</em>改变下面第二条那一格：
/// 第四家布尔落 <c>TINYINT(1)</c>、v0.31 的第五家落 <c>TINYINT</c>（厂商另有真 <c>BOOLEAN</c> 型，
/// 而它<em>不取</em>，理由在 <c>XuguTypes</c> 与 <c>XuguValues</c> 类注释），所以"列上是真布尔型"
/// 在五家里仍然是这一族独一份 —— 分岔没有变多，只是站在对侧的人从两家变成四家。）
/// <list type="bullet">
/// <item><b>没有 <c>nvarchar</c> 这一型</b>：PG 的 <c>varchar(n)</c>/<c>text</c> 存的就是库编码（现场一律 UTF-8），
/// 所以 <c>Unicode</c> 恒真、而量纲恒为字符 —— §10.4 那句「<c>n</c> 是字节还是字符」在这一家只有一个答案。</item>
/// <item><c>bool</c> 是原生类型，不是 <c>bit</c> 也不是 <c>smallint</c>，所以 <c>WHERE flag = 1</c> 在这里是类型错误，
/// 分岔点在 <see cref="PostgreSqlDialect"/> 的布尔比较。</item>
/// <item><c>Guid</c> 用原生 <c>uuid</c>（16 字节二进制），与 <c>char(36)</c> 那一档的取舍见
/// <see cref="PostgreSqlProviderBase"/> 的能力位注释。</item>
/// <item><c>timestamp</c> 不带时区、<c>timestamptz</c> 带 —— 与 T-SQL 的 <c>datetime2</c>/<c>datetimeoffset</c> 同构。</item>
/// </list></summary>
public sealed class PostgreSqlTypes : ITypeMap
{
    public static PostgreSqlTypes Instance { get; } = new();

    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["smallint"] = typeof(short),
        ["int2"] = typeof(short),
        ["integer"] = typeof(int),
        ["int4"] = typeof(int),
        ["bigint"] = typeof(long),
        ["int8"] = typeof(long),
        ["boolean"] = typeof(bool),
        ["bool"] = typeof(bool),
        ["real"] = typeof(float),
        ["float4"] = typeof(float),
        ["double precision"] = typeof(double),
        ["float8"] = typeof(double),
        ["numeric"] = typeof(decimal),
        ["decimal"] = typeof(decimal),
        ["money"] = typeof(decimal),
        ["character varying"] = typeof(string),
        ["varchar"] = typeof(string),
        ["character"] = typeof(string),
        ["bpchar"] = typeof(string),
        ["text"] = typeof(string),
        ["name"] = typeof(string),
        ["uuid"] = typeof(Guid),
        ["timestamp"] = typeof(DateTime),
        ["timestamp without time zone"] = typeof(DateTime),
        ["timestamptz"] = typeof(DateTimeOffset),
        ["timestamp with time zone"] = typeof(DateTimeOffset),
        ["date"] = typeof(DateOnly),
        ["time"] = typeof(TimeSpan),
        ["time without time zone"] = typeof(TimeSpan),
        ["bytea"] = typeof(byte[]),
        ["json"] = typeof(string),
        ["jsonb"] = typeof(string),
    }.ToFrozenDictionary();

    /// <summary>UTF-8 下一个字符最多 4 字节（代理对）。这一家没有"字节上限"这扇闸门 —— <c>varchar(n)</c> 的
    /// <paramref name="n"/> 恒为字符数 —— 所以这个乘数只是给 <see cref="ColumnContract.FitsBytes"/> 一个
    /// 永不误拒的宽松尺子，不是库里存在的约束。</summary>
    const int Utf8WorstCaseBytesPerChar = 4;

    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return column.MaxChars is { } chars ? $"VARCHAR({chars})" : "TEXT";

        if (clrType == typeof(byte[]))
            return "BYTEA";

        // 枚举存 INTEGER，与 T-SQL 那一族同一决定（PG 的 CREATE TYPE … AS ENUM 是"每库一套类型名"的路子，
        // 跨库对齐时它比 int 更难迁），绑定侧 PostgreSqlValues 同样按整数落地。
        if (clrType.IsEnum)
            return "INTEGER";

        return clrType switch
        {
            var t when t == typeof(bool) => "BOOLEAN",
            var t when t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) || t == typeof(int) => "INTEGER",
            var t when t == typeof(uint) || t == typeof(long) => "BIGINT",
            var t when t == typeof(ulong) => "NUMERIC(20,0)",
            var t when t == typeof(float) => "REAL",
            var t when t == typeof(double) => "DOUBLE PRECISION",
            var t when t == typeof(decimal) => $"NUMERIC({column.Precision ?? 18},{column.Scale ?? 4})",
            var t when t == typeof(DateTime) => "TIMESTAMP(3)",
            var t when t == typeof(DateTimeOffset) => "TIMESTAMPTZ(3)",
            var t when t == typeof(DateOnly) => "DATE",
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "TIME(3)",
            var t when t == typeof(Guid) => "UUID",
            var t when t == typeof(char) => "CHAR(1)",
            _ => throw new QuickSqlException($"PG 系（金仓/openGauss/瀚高）未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? "BYTEA" };

        // 这一家的字符串只有一种量纲：字符。字节数由编码决定、由内容决定，列上没有一个字节能问出来，
        // 所以 MaxBytes 只能给一个"永不误拒"的宽松上界（§10.4 的第三家就是这个形状）。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * Utf8WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 <see cref="LengthSemantics.Char"/>：<c>varchar(n)</c> 与 <c>char(n)</c> 的 <c>n</c> 都是字符数，
    /// 这一家没有"同一类型名在有的排序规则下是字节、在下一个是字符"那种分岔（§10.4）。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;
}
