using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.OracleBase;

/// <summary>CLR ↔ Oracle 血统类型名（§9.3 那一列，第三家）。与已落地的两族逐条对照，每一格都写清"谁同形、谁分岔"：
/// <list type="bullet">
/// <item><b>字符串的量纲要靠写法自己钉死</b>：<c>VARCHAR2(n)</c> 的 <c>n</c> 在 Oracle 里由
/// <c>NLS_LENGTH_SEMANTICS</c> 决定（默认 BYTE），在达梦里由建库参数决定 —— 同一份契约在两个会话设置下
/// 能装下不同长度的中文（§10.4 的第三家就是这个形状）。所以这里发的是 <c>VARCHAR2(n CHAR)</c>：
/// 把量纲写在列上，不赌会话默认值。达梦认不认这个 <c>CHAR</c> 限定词没有活库可证，覆写点与缺口记在 §14。</item>
/// <item><b>没有原生布尔</b>：<c>bool</c> 落 <c>NUMBER(1)</c>，于是 <c>WHERE flag = 1</c> 是这一族的正常写法
/// （与 T-SQL 同形、与金仓那一族分岔），可空布尔列上 <c>= 0</c> 匹配不到 NULL 那一档 §10.5 的事故也跟着回来了。
/// <b>这一格今天不是"两家同形"而是"四家同形、一家分岔"</b>：MySQL 血统落 <c>TINYINT(1)</c>、
/// v0.31 的第五家落 <c>TINYINT</c>，于是"整数型上比整数"是四家的共同形状，只有金仓那一族在真布尔列上比真值。
/// 那句 §10.5 的事故范围也跟着从"两家"变成"四家"——<b>而它不是一句好消息</b>：
/// 同一条"可空布尔列写 <c>= 0</c>"的错法，今天能在四家上静默漏行、只在一家报错，
/// 所以 §13.5 那条"换写法换不掉语义"的判据要在四家上各钉一次（<c>XuguTypes</c> 第三条给了它的形状）。</item>
/// <item><b>没有原生 uuid</b>：<c>Guid</c> 落 <c>CHAR(36)</c> —— 能力位 <c>Guid_Char36</c> 从 v0.10 立项到现在
/// 第一次有 provider 真的声明它（金仓那一族用原生 <c>uuid</c>，见 §4 那条被改掉的覆写清单）。</item>
/// <item><b>大文本与二进制是 LOB，而 LOB 不能用等号比</b>：这一条不是"实现没写完"而是那一家的规则，
/// 所以它落在 <see cref="OracleChangeGuard.Compares"/> 的 <see langword="false"/> 上，
/// 也是本仓库第一个真的把守卫整批退回逐条的类型映射。</item>
/// <item><b>时间跨度没有 <c>TIME</c> 型</b>：<c>TimeSpan</c> 落 <c>INTERVAL DAY(0) TO SECOND(3)</c>，
/// 与 T-SQL 的 <c>time(3)</c>、金仓的 <c>TIME(3)</c> 都不同形。默认精度取 3 位小数秒，与两族对齐。
/// <b>【v0.31】那句"与两族对齐"是五家对齐，而它对齐的是那颗数不是那个型名</b>：五份 <c>Types</c>
/// 里 <c>typeof(DateTime)</c> 那一臂的产出各带一颗 <c>(3)</c>（判据：<c>grep -rn 'typeof(DateTime) => "' src/QuickSql.Providers.*/ --include=*.cs</c>
/// 交出五行，形名分别是 <c>DATETIME2(3)</c> / <c>TIMESTAMP(3)</c> / <c>TIMESTAMP(3)</c> / <c>DATETIME(3)</c> / <c>DATETIME(3)</c>；
/// 那颗右引号不能省 —— 省了它数到的不是 5 而是 12，多出来的七行是五家 <c>Values</c> 里的 <c>DbType.DateTime2</c>
/// 那一臂再加上文档自撞，与这一格量不是一件事），
/// 所以这一档今天不是"两族与两族"的分岔，而是<b>五份产出、三样型名、一颗数五家一样</b>：
/// <c>DATETIME2(3)</c> 一家、<c>TIMESTAMP(3)</c> 两家（本族与金仓那一族）、<c>DATETIME(3)</c> 两家（MySQL 血统与虚谷）。
/// 后两家同形而不同源：那两个型名各自出自两份不同的厂商文档。换句话说这一格里"对齐"的部分一个都没动，
/// 动的只是"几家用它"那个数 —— 而本注释上一版在这里写的是"形状五样"，那是拿五份产出当成了五种形状，
/// 被自己同一格列出的那五行推翻，交账时按 <c>grep</c> 的产出改口（记 §13.5 的自撞清单）。</item>
/// </list>
/// <b>本类的产出没有在一台真库上跑过</b>：本机没有授权的 Oracle 血统实例，逐位出处记在 §13.5 那本台账里。
///
/// 类不是 <c>sealed</c>：唯一的可变点是 <see cref="VarCharType"/> 那一颗字符串类型名，它要留给
/// 第一个现场点验它的库种（§9.1 那一列写的就是 <c>Oracle</c>+，血统基类下面的两家在这一格上不必同形）。</summary>
public class OracleTypes : ITypeMap
{
    public static OracleTypes Instance { get; } = new();

    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["varchar2"] = typeof(string),
        ["nvarchar2"] = typeof(string),
        ["char"] = typeof(string),
        ["nchar"] = typeof(string),
        ["clob"] = typeof(string),
        ["nclob"] = typeof(string),
        ["number"] = typeof(decimal),
        ["float"] = typeof(double),
        ["binary_float"] = typeof(float),
        ["binary_double"] = typeof(double),
        ["long"] = typeof(string),
        ["raw"] = typeof(byte[]),
        ["blob"] = typeof(byte[]),
        ["bfile"] = typeof(string),
        ["date"] = typeof(DateTime),
        ["timestamp"] = typeof(DateTime),
        ["interval day(0) to second(3)"] = typeof(TimeSpan),
        ["rowid"] = typeof(string),
        ["urowid"] = typeof(string),
        ["binary_integer"] = typeof(int),
        ["pls_integer"] = typeof(int),
    }.ToFrozenDictionary();

    /// <summary>AL32UTF8 下一个字符最多 4 字节（代理对）。与金仓那一族同一个宽松尺子：这一族的
    /// <c>VARCHAR2(n CHAR)</c> 里 <c>n</c> 是字符数，字节上限由编码决定，列上没有一个字节能问出来，
    /// 所以 <see cref="ColumnContract.FitsBytes"/> 拿到的是一条永不误拒的边界而不是库里的约束（§10.4）。</summary>
    const int Utf8WorstCaseBytesPerChar = 4;

    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return column.MaxChars is { } chars ? VarCharType(chars) : "CLOB";

        if (clrType == typeof(byte[]))
            return "BLOB";

        // 枚举存整数，与两族同一决定（这一族的 CREATE TYPE … AS ENUM 不存在，`CHECK (col IN (…))` 又是
        // 另一套跨库更难迁的东西）；绑定侧 OracleValues 同样按整数落地。
        if (clrType.IsEnum)
            return "NUMBER(10)";

        return clrType switch
        {
            var t when t == typeof(bool) => "NUMBER(1)",
            var t when t == typeof(byte) || t == typeof(sbyte) => "NUMBER(3)",
            var t when t == typeof(short) || t == typeof(ushort) => "NUMBER(5)",
            var t when t == typeof(int) || t == typeof(uint) => "NUMBER(10)",
            var t when t == typeof(long) || t == typeof(ulong) => "NUMBER(19)",
            var t when t == typeof(float) => "BINARY_FLOAT",
            var t when t == typeof(double) => "BINARY_DOUBLE",
            var t when t == typeof(decimal) => $"NUMBER({column.Precision ?? 18},{column.Scale ?? 4})",
            var t when t == typeof(DateTime) => "TIMESTAMP(3)",
            var t when t == typeof(DateTimeOffset) => "TIMESTAMP(3) WITH TIME ZONE",
            var t when t == typeof(DateOnly) => "DATE",
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "INTERVAL DAY(0) TO SECOND(3)",
            var t when t == typeof(Guid) => "CHAR(36)",
            var t when t == typeof(char) => "CHAR(1)",
            _ => throw new QuickSqlException($"Oracle 血统（达梦/神通）未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(DbType=...)]"),
        };
    }

    /// <summary>字符串的类型名。<b>这一颗是覆写点</b>：把量纲写成 <c>CHAR</c> 是 Oracle 的语法，而 §9.1 记的
    /// 达梦那一行只说"<c>VARCHAR</c>/<c>VARCHAR2</c>"，没有证过它认这个限定词。若现场报语法错，
    /// 覆写成 <c>VARCHAR2(n)</c> 就等于把量纲交回给会话默认值 —— 那时 §10.4 那一档在这一家重新变成两个答案，
    /// 不是这里的退路能顺手抹平的事（缺口写在 §14，触发条件是第一次真建表）。</summary>
    protected virtual string VarCharType(int chars) => $"VARCHAR2({chars} CHAR)";

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? "BLOB" };

        // 与金仓那一族同一形状的结论、不同的来路：那一家是"编码在集群级定死所以只有一个答案"，
        // 这一家是"我们把答案写在了列上"。两者都让 MaxBytes 只剩一个永不误拒的宽松上界（§10.4）。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * Utf8WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType)
    {
        // 带限定的写法先剥到基名：TIMESTAMP(3) WITH TIME ZONE / INTERVAL DAY(0) TO SECOND(3) 这类
        // 名字在 ALL_TAB_COLUMNS 里是带精度与括号的，按整串查表永远查不到（与两族同一处理）。
        var baseName = TrimBaseType(storeTypeName);

        if (TryLookup(baseName, out clrType))
            return true;

        // NUMBER 的标度与精度决定它是不是整数：契约侧存的是 CLR 型的形状，所以这里只把"整数档"认出来。
        if (baseName == "number")
        {
            var scale = ScaleOf(storeTypeName);

            clrType = scale switch
            {
                null => typeof(decimal),
                0 => typeof(long),
                _ => typeof(decimal),
            };

            return true;
        }

        clrType = null;
        return false;
    }

    bool TryLookup(string baseName, out Type? clrType) => StoreToClr.TryGetValue(baseName, out clrType);

    /// <summary>§10.4 的量纲：这一族<b>有两个答案</b>（<c>VARCHAR2(n)</c> 看会话、<c>VARCHAR2(n CHAR)</c> 看写法），
    /// 而 <see cref="VarCharType"/> 选了把答案写在列上那一种，所以映射侧的读法与金仓那一族同形。
    /// 真正的判据不在这颗槽位上，在 <see cref="ISchemaReader.ProbeLengthSemanticsAsync"/> 的读数里 ——
    /// 那里量的才是库里那一列实际是字节还是字符。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;

    static string TrimBaseType(string storeTypeName)
    {
        var name = storeTypeName.Trim();
        var paren = name.IndexOf('(');
        var space = name.IndexOf(' ');

        var cut = paren < 0 ? space : space < 0 ? paren : Math.Min(paren, space);

        // INTERVAL DAY(0) TO SECOND(3) 的基名跨两个词，所以它单独立一条而不是按第一个空格切。
        if (name.StartsWith("interval ", StringComparison.OrdinalIgnoreCase))
            return name.ToLowerInvariant();

        return (cut < 0 ? name : name[..cut]).Trim().ToLowerInvariant();
    }

    static int? ScaleOf(string storeTypeName)
    {
        var open = storeTypeName.IndexOf('(');
        var comma = storeTypeName.IndexOf(',', open < 0 ? 0 : open);

        if (open < 0 || comma < 0)
            return null;

        var close = storeTypeName.IndexOf(')', comma);

        return close > comma + 1 && int.TryParse(storeTypeName.AsSpan(comma + 1, close - comma - 1), out var scale)
            ? scale
            : null;
    }
}
