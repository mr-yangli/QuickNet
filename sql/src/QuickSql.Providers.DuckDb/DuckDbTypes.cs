using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.DuckDb;

/// <summary>CLR ↔ DuckDB 类型名（§9.3 那一列的第八份；血统那份 <c>PostgreSqlTypes</c> 的克隆，
/// 只换 DuckDB 与 PG 那几处<b>厂商文档明写的</b>类型分岔，其余照抄 —— 与"文档明写的分岔才配覆写"
/// 那条判据同源。marked【文档·DuckDB】的每一格出处都在对应行）。
///
/// <para>血统与它的关系是克隆而<b>不是继承</b>：<c>DuckDbTypes</c> 不挂到 <see cref="PostgreSqlTypes"/>
/// 下面，那份血统类型表的产出（<c>TIMESTAMP(3)</c>/<c>DOUBLE PRECISION</c>/<c>TIME(3)</c>）在本家
/// 一格都不成立（类型分岔见 <see cref="GetStoreType"/> 各分支）—— 继承会把血统的缺省型名偷渡进本家。</para>
///
/// <para><b>与 PG 的分岔逐条对照（【文档·DuckDB】全清单见 §13.5 台账）</b>：
/// <list type="bullet">
/// <item><b><c>double precision</c> 这个名字没有</b>：DuckDB 的浮点双精度的现行型名是 <c>DOUBLE</c>
/// （别名 <c>FLOAT8</c>；PG 的 <c>float8</c> 别名在本家成立，而 <c>double precision</c> 那一行文档的
/// 兼容清单里没有数它 —— <see href="https://duckdb.org/docs/current/sql/dialect/postgresql_compatibility.html"/>）。</item>
/// <item><b>时间戳不带精度后缀</b>：血统发的 <c>TIMESTAMP(3)</c>/<c>TIMESTAMPTZ(3)</c> 在本家
/// 读成 <c>TIMESTAMP</c>/<c>TIMESTAMPTZ</c>（<c>TIMESTAMP</c> 的别名是 <c>DATETIME</c> ——
/// <see href="https://duckdb.org/docs/current/sql/data_types/overview.html"/>）。</item>
/// <item><b>TIME 不吃精度</b>：血统的 <c>TIME(3)</c> 在本家读成 <c>TIME</c>，没有精度后缀这一档
/// （<see href="https://duckdb.org/docs/current/sql/data_types/time.html"/>）。</item>
/// <item><b>VARCHAR(n)/TEXT 的宽度在本家不生效</b>（长度限制交由引擎忽略）：血统把它当字符数的尺子发出去，
/// 本家照发同样那几个名字 —— 尺子是仓库侧的（<see cref="ColumnContract.FitsChars"/> 照用），不是库侧的约束
/// （<see href="https://duckdb.org/docs/current/sql/data_types/text.html"/>）。</item>
/// <item><c>bool</c> 是原生类型、枚举仍落 <c>INTEGER</c>、<c>Guid</c> 落 <c>UUID</c>、字节落 <c>BYTEA</c>
/// —— 这四格与血统逐字同形（前两格在 PG 兼容清单里、后两格在类型总表里，都是【文档·DuckDB】）。</item>
/// </list></para>
///
/// <para><b>本类与血统那份表一样没有在一台真库上验过</b>（本机没有任何一台 DuckDB，也没有加载过驱动），
/// 逐格口径记 §13.5 那本台账，那里每一条挂着出处 URL。</para></summary>
public sealed class DuckDbTypes : ITypeMap
{
    public static DuckDbTypes Instance { get; } = new();

    /// <summary>血统那份 <c>StoreToClr</c> 的<b>原样克隆</b>（读侧口径：information_schema 那一族照
    /// PG 的行数与列名报【文档·DuckDB】，深度目录以外怎么报、报不大写还是小写，本仓库没有读数 ——
    /// 所以这一份索引反向映射按"血统认什么"的最大集保留，不往两边删）。PG 的 <c>double precision</c>、
    /// <c>name</c> 那几个名字里 DuckDB 文档没数的，留在索引里<b>不</b>等于声明它们会出现在本家产出里 ——
    /// <see cref="GetStoreType"/> 是写侧的出口，出哪些名字由它说了算。</summary>
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

    /// <summary>UTF-8 下一个字符最多 4 字节（代理对）。与血统同一格：这一家没有"字节上限"这扇闸门，
    /// 这个乘数只是给 <see cref="ColumnContract.FitsBytes"/> 一个永不误拒的宽松尺子</summary>
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

        // 枚举存 INTEGER，与血统同一决定（PG 的 CREATE TYPE … AS ENUM 是"每库一套类型名"的路子，
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
            var t when t == typeof(double) => "DOUBLE",
            var t when t == typeof(decimal) => $"NUMERIC({column.Precision ?? 18},{column.Scale ?? 4})",
            var t when t == typeof(DateTime) => "TIMESTAMP",
            var t when t == typeof(DateTimeOffset) => "TIMESTAMPTZ",
            var t when t == typeof(DateOnly) => "DATE",
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "TIME",
            var t when t == typeof(Guid) => "UUID",
            var t when t == typeof(char) => "CHAR(1)",
            _ => throw new QuickSqlException($"DuckDB 未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? "BYTEA" };

        // 与血统同一格：这一家的字符串只有一种量纲：字符。字节数由编码决定、由内容决定，
        // MaxBytes 只能给一个"永不误拒"的宽松上界（§10.4）。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * Utf8WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 <see cref="LengthSemantics.Char"/>：与血统同一句形状 —— <c>varchar(n)</c> 与 <c>char(n)</c>
    /// 的 <c>n</c> 都是字符数；而 DuckDB 对这个 n 本身不设约束（宽度声明在引擎侧被忽略，
    /// 上面 <see cref="GetStoreType"/> 那一串名照发、只是别把它读成库侧的约束）。【文档·DuckDB】。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;
}
