using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.SqlServer;

/// <summary>CLR ↔ T-SQL 类型名（§9.3）。三处偏保守的默认值都是实测换来的，不要"顺手改回去"：
/// <list type="bullet">
/// <item><c>string</c> 默认 <c>nvarchar</c>：本机 LocalDB 的 <c>SQL_Latin1_General_CP1_CI_AS</c> 下
/// <c>varchar</c> 存中文是<b>静默变 '?'</b>，不是报错（§10.4 实测第一行）。</item>
/// <item><c>DateTime</c> 用 <c>datetime2(3)</c> 而不是遗留 <c>datetime</c>：后者按 3.33ms 取整且只到 1753 年。</item>
/// <item><c>Guid</c> 用原生 <c>uniqueidentifier</c>：代价是 <see cref="SqlServerProvider"/> 上写明的
/// 聚集索引热点与 <c>Guid.CompareTo</c> 排序不一致，跨库对齐要显式改成 <c>char(36)</c>。</item>
/// </list></summary>
public sealed class SqlServerTypes : ITypeMap
{
    public static SqlServerTypes Instance { get; } = new();

    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["bigint"] = typeof(long),
        ["binary"] = typeof(byte[]),
        ["bit"] = typeof(bool),
        ["char"] = typeof(string),
        ["date"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["datetime2"] = typeof(DateTime),
        ["datetimeoffset"] = typeof(DateTimeOffset),
        ["decimal"] = typeof(decimal),
        ["float"] = typeof(double),
        ["image"] = typeof(byte[]),
        ["int"] = typeof(int),
        ["money"] = typeof(decimal),
        ["nchar"] = typeof(string),
        ["ntext"] = typeof(string),
        ["numeric"] = typeof(decimal),
        ["nvarchar"] = typeof(string),
        ["real"] = typeof(float),
        ["rowversion"] = typeof(byte[]),
        ["smalldatetime"] = typeof(DateTime),
        ["smallint"] = typeof(short),
        ["smallmoney"] = typeof(decimal),
        ["sql_variant"] = typeof(object),
        ["text"] = typeof(string),
        ["time"] = typeof(TimeSpan),
        ["tinyint"] = typeof(byte),
        ["uniqueidentifier"] = typeof(Guid),
        ["varbinary"] = typeof(byte[]),
        ["varchar"] = typeof(string),
    }.ToFrozenDictionary();

    /// <summary>存储类型名一律大写，与 §9.3 那张表逐字符对齐：表里写 <c>NVARCHAR(n)</c>、
    /// 产出写 <c>nvarchar(n)</c>，黄金样本就成了表的错别字而不是方言的。大小写对 T-SQL 无差别，
    /// 反向查表 <see cref="StoreToClr"/> 本来就是 <c>OrdinalIgnoreCase</c>。</summary>
    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return String(column);

        if (clrType == typeof(byte[]))
            return Bytes(column);

        // enum → INT 是 §9.3 的既定映射（T-SQL 没有原生枚举类型）。绑定侧 <see cref="SqlServerValues"/>
        // 同样按 Int32 落地：两头不一致就会出现"建表是 int、写值报转换错"。
        if (clrType.IsEnum)
            return "INT";

        return clrType switch
        {
            var t when t == typeof(bool) => "BIT",
            var t when t == typeof(byte) => "TINYINT",
            var t when t == typeof(sbyte) => "SMALLINT",
            var t when t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) => "INT",
            var t when t == typeof(int) => "INT",
            var t when t == typeof(uint) => "BIGINT",
            var t when t == typeof(long) => "BIGINT",
            var t when t == typeof(ulong) => "NUMERIC(20,0)",
            var t when t == typeof(float) => "REAL",
            var t when t == typeof(double) => "FLOAT",
            var t when t == typeof(decimal) => $"DECIMAL({column.Precision ?? 18},{column.Scale ?? 4})",
            var t when t == typeof(DateTime) => "DATETIME2(3)",
            var t when t == typeof(DateTimeOffset) => "DATETIMEOFFSET(3)",
            var t when t == typeof(DateOnly) => "DATE",
            var t when t == typeof(TimeOnly) => "TIME(3)",
            var t when t == typeof(TimeSpan) => "TIME(3)",
            var t when t == typeof(Guid) => "UNIQUEIDENTIFIER",
            var t when t == typeof(char) => "NCHAR(1)",
            _ => throw new QuickSqlException($"SQL Server 未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    static string String(ColumnContract column)
    {
        // 无上限 → MAX。有上限时 n 就是字符数（nvarchar 是 UTF-16 code unit，§10.4 第三行）。
        if (column.MaxChars is not { } chars)
            return column.Unicode ? "NVARCHAR(MAX)" : "VARCHAR(MAX)";

        return column.Unicode ? $"NVARCHAR({chars})" : $"VARCHAR({chars})";
    }

    static string Bytes(ColumnContract column)
        => column.MaxBytes is { } bytes && bytes <= 8000 ? $"VARBINARY({bytes})" : "VARBINARY(MAX)";

    /// <summary>两次调用得同一个结果（<c>SqlServerCodeFirst</c> 会对已经算过的契约再调一次），所以这里的
    /// 规则只看<b>算完之后</b>的那几格，不看"这一格是不是我第一次见"。</summary>
    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var resolved = Resolve(column);

        // 「值归库给」是<em>存储类型</em>的事实，只有本家认得（§10.6 的第三档）：共用层猜不到哪一型算
        // "库自己递增"，而猜得到的那一半（整型主键）已经另有 <see cref="ColumnContract.IsIdentity"/>。
        // 判据取 StoreTypeName 而不是 ClrType：byte[] 在本家默认是 VARBINARY，那里头值归调用方给。
        //
        // 遗留同义词 timestamp 刻意不列：它在建表时确实等价于 rowversion，但契约里那一格的名字归两侧
        // （列注解写 timestamp、库里 sys.columns 报 timestamp），今天没有任何现场需要它，
        // 写进来就是一格没有对拍的猜测 —— 触发条件见 §14 那一格的末尾。
        if (!resolved.IsConcurrencyToken || !string.Equals(resolved.StoreTypeName, "rowversion", StringComparison.OrdinalIgnoreCase))
            return resolved;

        return resolved with { StoreFillsValue = true };
    }

    ColumnContract Resolve(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        // 量纲由库决定：nvarchar 的 n 是字符（UTF-16，一字符 2 字节），varchar 的 n 是字节。
        // varchar 在 UTF-8 排序规则下"字节够而字符不够"，所以非 Unicode 列按最坏 3 字节/字符算（§10.3）。
        var unicode = column.Unicode || clrType == typeof(string);
        var width = unicode ? 2 : 3;

        return column with
        {
            Unicode = unicode,
            LengthSemantics = unicode ? LengthSemantics.Char : LengthSemantics.Byte,
            MaxBytes = column.MaxBytes ?? column.MaxChars * width,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>SQL Server 是"看类型不看库"的那一家（§10.4）：<c>nvarchar</c> 是字符、<c>varchar</c> 是字节，
    /// 所以量纲直接跟着 <see cref="ColumnContract.Unicode"/> 走，而不是给一个库级常量。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column)
        => column.Unicode ? LengthSemantics.Char : LengthSemantics.Byte;
}
