using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.MySqlBase;

/// <summary>CLR ↔ MySQL 系类型名（§9.3 那一列的第四家）。与已落地三族逐条对照，分岔的四处没有一处是换个型名：
/// <list type="bullet">
/// <item><b>量纲是字符，但字节闸门是真的</b>：<c>VARCHAR(n)</c> 的 <c>n</c> 从 4.1 起就是<b>字符数</b>（§10.4
/// 那一行写的"与 PG 一致"就是这一格），所以 <see cref="GetLengthSemantics"/> 恒 <see cref="LengthSemantics.Char"/>。
/// 与 PG 不同的是闸门确实存在：<c>utf8mb4</c> 下一颗字符最多 4 字节，于是单列上限是
/// 65535 / 4 = 16383 字符，而它是<em>行</em>级预算的一部分而不是列级 —— 今天的
/// <see cref="ColumnContract"/> 只有一个"这一列多宽"的槽位，没有"这张表还剩多少行预算"那一格（§14）。</item>
/// <item><b>没有带时区的时刻类型</b>：<c>TIMESTAMP</c> 那一家存的是 UTC 而显示时按会话时区换算，
/// 它带的是<em>会话</em>偏移不是<em>值</em>的偏移，所以它装不下 <c>DateTimeOffset</c> 的那三颗分钟。
/// §9.3 原稿给这一档写的退路是"降级 <c>DateTime</c> UTC + 列标记，读取端还原"，而那两半今天都不存在
/// （绑定侧没有任何时区换算，契约上也没有"这一列存的是 UTC"那颗标记）—— 于是这里<em>抛</em>而不是
/// 悄悄按本地时间写进去：后者是把 <c>+08:00</c> 变成 <c>08:00</c> 再读回 <c>00:00</c> 的那种静默改值（§5.8 同一条纪律）。</item>
/// <item><b>大文本与大二进制是分档的，不是一档</b>：<c>TEXT</c> 家族与 <c>BLOB</c> 家族各四档，
/// 选小了是<em>静默截断</em>。所以不设上限的两个槽位都取各自最大的一档（<c>TEXT</c> 与 <c>LONGBLOB</c>）。</item>
/// <item><b>Guid 有两种存法而默认取文本那种</b>（<c>CHAR(36)</c>，与 §9.3 那句"跨库默认 CHAR(36) 以便手工查询"
/// 同一条判断）。另一条 <c>BINARY(16)</c> 不是 provider 单方能决定的：它要连接串上 <c>GuidFormat=BinaryUuid</c>
/// 那一颗参数与字节置换一致，而 §4 的 SPI 里<em>没有</em>"连接串参数会改变方言假设"这一格 ——
/// 与金仓那一家记的"Oracle 兼容模式开关"是同一颗缺口（§14）。<b>【v0.31】那一格今天已有四个实例</b>，
/// 按 <c>IdentifierPolicy.Xugu</c> 那一条的数法：金仓的兼容模式、这一家的 <c>lower_case_table_names</c>
/// （<c>ANSI_QUOTES</c> 与它同一格，见 <c>IdentifierPolicy.MySql</c> 末段）、第五家的 <c>CHAR_SET</c>、
/// 第五家的 <c>COMPATIBLE_MODE</c>，而槽位仍然一颗也没有 —— 户数从两颗涨到四颗，缺口原地不动。</item>
/// </list>
/// <b>本文件的产出文本没有在一台真库上跑过</b>：授权的实例里没有一家是 MySQL 血统（§13.1 第 4 项），
/// 所以每一处"这一家有 / 没有"都是厂商文档口径，逐位记在 §13.5 那本出处台账里。</summary>
public sealed class MySqlTypes : ITypeMap
{
    public static MySqlTypes Instance { get; } = new();

    /// <summary>读侧的类型名表。<b>只按 <c>information_schema.columns.data_type</c> 那一列折</b>，
    /// 而那一列把 <c>tinyint(1)</c> 与 <c>tinyint(4)</c> 报成同一个 <c>tinyint</c> —— 于是"这颗布尔"与
    /// "这颗 8 位整数"在目录视图这一层是分不开的，这里保守地认成 <see cref="sbyte"/>。
    /// 真正的判据在 <c>column_type</c> 那一列，而 §9.5 的 <c>doctor</c> 今天读的是前者：缺口记 §14，
    /// 不在这里靠"看到 tinyint 就当 bool"猜一个（猜错的方向是把 <c>255</c> 装进 <c>bool</c>）。</summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["tinyint"] = typeof(sbyte),
        ["tinyint unsigned"] = typeof(byte),
        ["smallint"] = typeof(short),
        ["smallint unsigned"] = typeof(ushort),
        ["mediumint"] = typeof(int),
        ["int"] = typeof(int),
        ["integer"] = typeof(int),
        ["int unsigned"] = typeof(uint),
        ["bigint"] = typeof(long),
        ["bigint unsigned"] = typeof(ulong),
        ["decimal"] = typeof(decimal),
        ["numeric"] = typeof(decimal),
        ["float"] = typeof(float),
        ["double"] = typeof(double),
        ["bit"] = typeof(ulong),
        ["char"] = typeof(string),
        ["varchar"] = typeof(string),
        ["tinytext"] = typeof(string),
        ["text"] = typeof(string),
        ["mediumtext"] = typeof(string),
        ["longtext"] = typeof(string),
        ["enum"] = typeof(string),
        ["set"] = typeof(string),
        ["json"] = typeof(string),
        ["binary"] = typeof(byte[]),
        ["varbinary"] = typeof(byte[]),
        ["tinyblob"] = typeof(byte[]),
        ["blob"] = typeof(byte[]),
        ["mediumblob"] = typeof(byte[]),
        ["longblob"] = typeof(byte[]),
        ["date"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["timestamp"] = typeof(DateTime),
        ["time"] = typeof(TimeSpan),
        ["year"] = typeof(short),
    }.ToFrozenDictionary();

    /// <summary><c>utf8mb4</c> 下一颗字符最多 4 字节（与 PG 那一族的乘数同一个来源，但这一家的
    /// <b>行</b>预算真的按字节算，所以它不只是给 <see cref="ColumnContract.FitsBytes"/> 一把宽松尺子）。
    /// 由此得出单列的字符上限 <see cref="MaxVarcharChars"/>。</summary>
    const int Utf8mb4WorstCaseBytesPerChar = 4;

    /// <summary>单列 <c>VARCHAR</c> 装得下的字符数上限：<c>65535</c>（<c>utf8mb4</c> 一字符 4 字节，行内长度前缀另算，
    /// 这里取文档给的那个数）。超过它就不是"这一列写宽一点"而是<b>换一档型</b>。</summary>
    const int MaxVarcharChars = 16383;

    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return column.MaxChars is { } chars
                ? chars > MaxVarcharChars ? "TEXT" : $"VARCHAR({chars})"
                : "TEXT";

        if (clrType == typeof(byte[]))
            return "LONGBLOB";

        // 枚举存整数，与另两族同一决定（这一家也有原生 ENUM 型，但那是"每库一套取值清单"的路子，
        // 跨库对齐时它比 int 更难迁，而 §9.3 那一格写的正是"存 INT 或 STRING"）。绑定侧 MySqlValues 同样按整数落地。
        // 【v0.31】那句"另两族"是 v0.30 的口径（当时落地三家）；今天这一格是<b>五家同一决定</b>，
        // 判据可重数：<c>grep -rn "clrType.IsEnum" src/QuickSql.Providers*/*Types.cs</c> 五处命中，五家各一处。
        if (clrType.IsEnum)
            return "INT";

        return clrType switch
        {
            // 布尔落 TINYINT(1)：那是这一家自己给 BOOL 起的名字（厂商文档口径：BOOL 是 TINYINT(1) 的同义词）。
            // 它不是只能装 0/1 的那一档（1..127 也放得下），所以谓词侧的 = 1 与 IS TRUE 在这里不等价 ——
            // 见 <see cref="MySqlDialect"/> 那一格为什么仍取共用的 = 1。
            var t when t == typeof(bool) => "TINYINT(1)",

            // 窄整数一律往宽里放，与另两族同一条纪律（T-SQL 的 tinyint 是 0..255 而无符号档，金仓走 SMALLINT）：
            // 这里不用 UNSIGNED，因为那会把"读回来该配哪种 CLR 型"变成 provider 的事而不是 CLR 型的事。
            // 【v0.31】第五家同条（<c>XuguTypes.cs:162</c> 与这里逐字同臂），所以那句"另两族"今天是"另三家"，
            // 而<b>分档没有变多</b>：五家在这一臂上是 <c>SMALLINT</c> 三家（金仓、这一家、虚谷）∶
            // <c>NUMBER(3)</c> 一家（Oracle 血统）∶ <c>TINYINT</c> 一家（T-SQL 的 byte），仍是那一家站在"不放"的一边。
            var t when t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) || t == typeof(int) => "INT",
            var t when t == typeof(uint) || t == typeof(long) => "BIGINT",
            var t when t == typeof(ulong) => "DECIMAL(20,0)",
            var t when t == typeof(float) => "FLOAT",
            var t when t == typeof(double) => "DOUBLE",
            var t when t == typeof(decimal) => $"DECIMAL({column.Precision ?? 18},{column.Scale ?? 4})",

            // DATETIME 而不是 TIMESTAMP：后者范围只到 2038（且那一档取决于版本）、按会话时区解释、
            // 而它还有"第一列可能被隐式挂上 ON UPDATE CURRENT_TIMESTAMP"的历史行为（§9.1 那一列没记的那一颗）。
            // 3 位小数秒与另两族对齐（那是 §12.x 里"三家同一档"的既有形状，不是这一家的默认精度）。
            // 【v0.31 重数】那句"另两族"当时就写少了：这一档五家里<b>四家在前</b>时就全是三位
            // （<c>grep -rn "typeof(DateTime) =>" src/QuickSql.Providers*/*Types.cs</c> 五处：DATETIME2(3) /
            // TIMESTAMP(3) ×2 / DATETIME(3) ×2），第五家虚谷的 <c>DATETIME(3)</c> 是同档的第五颗，
            // 而它那一颗的精度限定词本身是【推断】（见 <c>XuguTypes</c> 类注释第四条）。
            var t when t == typeof(DateTime) => "DATETIME(3)",
            var t when t == typeof(DateTimeOffset) => throw new QuickSqlException(
                "MySQL 系没有能装下 DateTimeOffset 偏移量的时刻类型：DATETIME 与 TIMESTAMP 存的都是不带偏移的时刻，"
                + "而后者按会话时区解释。§9.3 给这一档写的退路是「降级 DateTime UTC + 列标记，读取端还原」，"
                + "那两半（绑定侧的时区换算、契约上那颗「本列存 UTC」标记）今天都不存在，所以宁缺不猜（缺口在 §14）。"
                + "今天要这一列：显式给 [Column(DbType = ...)] 自己选一个型并自己负责换算。"),
            var t when t == typeof(DateOnly) => "DATE",
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "TIME(3)",
            var t when t == typeof(Guid) => "CHAR(36)",
            var t when t == typeof(char) => "CHAR(1)",
            _ => throw new QuickSqlException($"MySQL 系（MySQL/MariaDB/TiDB）未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(DbType=...)]"),
        };
    }

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? "LONGBLOB" };

        // 与 PG 那一族同形而不同因：这里的 MaxBytes 不是一把纯宽松的尺子，它对应一个真实存在的字节闸门
        // （只是那道闸门在行上而不在列上，§10.4 的按列比对量不到它）。所以这一格填出来的数字给
        // FitsBytes 用是安全的，给"这一列最多几字节"用会偏大 —— 库里问得出的那一版在下面这个 reader 里。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * Utf8mb4WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 <see cref="LengthSemantics.Char"/>：与 PG 系同一答案，而这一家<em>问得出字节</em>
    /// （<c>CHARACTER_OCTET_LENGTH</c>）—— §10.4 那张表里"两把尺子都可读"这一格它是<b>第二家</b>
    /// （第一家是 Oracle 血统：那边靠 <c>CHAR_USED</c> 判哪一把是声明的，这一家由字段名定死，见
    /// <see cref="MySqlSchemaReader"/> 类注释），落地在那边而不是这里。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;
}
