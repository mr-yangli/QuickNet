using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.ClickHouse;

/// <summary>CLR ↔ ClickHouse 类型名（§9.3 那一列的第 22 份）。整层【文档·ClickHouse】（clickhouse.com/docs
/// 的数据类型各页），本机没有引擎，一次都没有实测过。
///
/// <para>与另几家的分岔逐条，每一条都是这一格的形状点：
/// <list type="bullet">
/// <item><b>布尔是真类型</b>【文档·ClickHouse：Bool 数据类型页】：CLR bool → 库里的 <c>Bool</c> 一步到位，
/// 与 PG 血统那样落 <c>BOOLEAN</c> 同形，而 MySQL 血统与 GBase 8s 两家都把 bool 攘进整数档。
/// 落 <c>Bool</c> 直接兑现那颗 <c>Boolean_Type</c> 能力位，谓词那一侧 <c>= true</c>/<c>= false</c>
/// 与列型同边（<see cref="ClickHouseDialect.EmitBooleanEquality"/>）。</item>
/// <item><b>整数有符号/无符号成对</b>【文档·ClickHouse：数值型那几行】：<c>Int8..Int64</c> 与
/// <c>UInt8..UInt64</c> 两列并存，所以 CLR 的 byte/ushort/uint/ulong 不必像只有有符号那几族那样
/// 全部并进宽档 —— 按位宽逐颗对齐。"窄整数往宽里放"那条纪律在这里的第一件<em>不要做</em>的事：
/// byte 落 UInt8 而不是 Int16，落宽档会把无符号语义静默丢掉一半（§10.5 的反面）。</item>
/// <item><b>decimal 给 <c>Decimal(p,s)</c></b>【文档·ClickHouse：Decimal 数据类型页】：precision/scale
/// 都要显式带。缺省那一档厂商页没有给默认值【推断：取 (18,4)，与另五族同数】。</item>
/// <item><b>string 落 <c>String</c>，无宽度</b>【文档·ClickHouse：String 数据类型页写明长度不受限】：
/// DDL 里不写任何括号，<see cref="ColumnContract.MaxChars"/> 只是<b>仓库侧</b>的尺子
///（<c>FitsChars</c> 闸还量它），不是库侧行为 —— 与 DuckDB 那颗"宽度声明被引擎忽略"同形而更直：
/// 发出去的型名里本来就没有那颗数。量纲这一问"库侧没有第二把尺可问"（§10.4），
/// <see cref="GetLengthSemantics"/> 落 <see cref="LengthSemantics.Char"/>（仓库侧的退化实现）。</item>
/// <item><b>时刻落 <c>DateTime64(3)</c></b>【文档·ClickHouse：DateTime64 数据类型页；不带精度的
/// <c>DateTime</c> 是秒精度，带毫秒要显式 (3)】：CLR DateTime 有 100 纳秒刻度，落秒精度会静默丢毫秒，
/// 取 3 位与五族"取 3 位（毫秒）"同一条纪律。<c>DateOnly → Date</c> 同页。
/// <b>没有带偏移的时刻型</b>【文档没逐页回读】：DateTime64 那一族的格子里没有 DateTimeOffset 的对应点，
/// 所以那一臂抛（与 GBase 8s 同果而不同因，记 §14）。</item>
/// <item><b>没有"由库递增的列"</b>【文档没逐页回读】：整型主键在 <see cref="GetStoreType"/> 得到自己的
/// 位宽型名就此终结，没有 <c>SERIAL</c>/<c>IDENTITY</c> 那两档。<b>它牵着一条本笔已知的缺口</b>：
/// 内核 <c>InsertEngine</c> 把自增列剔出 INSERT 列清单那一手在本家会把缺省 0 静默嵌进库 ——
/// 账记 §14（<see cref="ClickHouseProvider"/> 类注释第四条）。</item>
/// <item><c>TimeOnly</c>/<c>TimeSpan</c>/<c>byte[]</c>：三档全部抛 —— 本家读到的格里"一天的某个时刻"
/// 与"跨度"没有独立型、大对象那一档也没有对应型【文档没逐页回读】，逐档的抛话理由在各自分支写死。</item>
/// </list></para>
///
/// <para><b>本文件产出的每一条型名都没有在一台活库上跑过，而且本机没有任何一台可以跑</b>。
/// 逐格口径记 §13.5 第十七本台账，那里挂着出处 URL。</para></summary>
public sealed class ClickHouseTypes : ITypeMap
{
    public static ClickHouseTypes Instance { get; } = new();

    /// <summary>读侧的类型名表：本家 <c>system.columns.type</c> 那一列会报回来的基名【文档·ClickHouse，
    /// system.columns 页的类型清单节选口径】。带 <c>Nullable(…)</c>/<c>LowCardinality(…)</c> 包裹的型由
    /// <see cref="ClickHouseSchemaReader"/> 先剥皮再查这里。<c>Date32</c>/<c>DateTime</c> 与 <c>Date</c> 一档都收进来。</summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = typeof(bool),
        ["int8"] = typeof(sbyte),
        ["int16"] = typeof(short),
        ["int32"] = typeof(int),
        ["int"] = typeof(int),
        ["int64"] = typeof(long),
        ["uint8"] = typeof(byte),
        ["uint16"] = typeof(ushort),
        ["uint32"] = typeof(uint),
        ["uint64"] = typeof(ulong),
        ["float32"] = typeof(float),
        ["float"] = typeof(float),
        ["float64"] = typeof(double),
        ["double"] = typeof(double),
        ["decimal"] = typeof(decimal),
        ["string"] = typeof(string),
        ["uuid"] = typeof(Guid),
        ["date"] = typeof(DateOnly),
        ["date32"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["datetime64"] = typeof(DateTime),
    }.ToFrozenDictionary();

    /// <summary>UTF-8 下一个字符最多 4 字节（代理对）。与 <c>DuckDbTypes</c> 那颗同值同理：
    /// 只给 <see cref="ColumnContract.FitsBytes"/> 一把"永不误拒"的宽松尺（本家 String 那一档
    /// 没有库侧字节上限可量）。</summary>
    const int Utf8WorstCaseBytesPerChar = 4;

    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return "String";

        // 枚举存整数，与另几族同一决定；CLR 底层型是 long 的枚举需要 [Column(TypeName=...)] 点名 Int64
        //（这一份窄档只有 Int32 一支，与 GBase 8s 的"INTEGER 那一臂同样窄"同一处境）。
        if (clrType.IsEnum)
            return "Int32";

        return clrType switch
        {
            // 布尔是本家的真类型【文档·ClickHouse：Bool 数据类型页】。落 Bool 直接兑现 Boolean_Type 那颗位，
            // 谓词侧 = true 与列型同边（ClickHouseDialect.EmitBooleanEquality）。
            var t when t == typeof(bool) => "Bool",

            // 成对的 8 颗【文档·ClickHouse】：按位宽逐颗对齐。byte 落 UInt8 而不是往宽里并 ——
            // 落宽档丢掉的无符号语义才是静默退化（§10.5 的反面），本家两列都原生、不需要借"往宽里放"那一手。
            var t when t == typeof(sbyte) => "Int8",
            var t when t == typeof(byte) => "UInt8",
            var t when t == typeof(short) => "Int16",
            var t when t == typeof(ushort) => "UInt16",
            var t when t == typeof(int) => "Int32",
            var t when t == typeof(uint) => "UInt32",
            var t when t == typeof(long) => "Int64",
            var t when t == typeof(ulong) => "UInt64",

            var t when t == typeof(float) => "Float32",
            var t when t == typeof(double) => "Float64",

            // Decimal 的两档都要显式带【文档·ClickHouse：Decimal 数据类型页】；缺省那档厂商没有给精确档位
            //【推断：取 (18,4) 与另五族同数；厂商给的上限（precision 76）比 18 宽，缺省落在范围内成立】。
            var t when t == typeof(decimal) => $"Decimal({column.Precision ?? 18},{column.Scale ?? 4})",

            // 原生 16 字节【文档·ClickHouse：UUID 数据类型页】—— 与 GBase 8s 落 CHAR(36) 的那一档正相反，
            // 这一颗是"另一支候选在本家真的存在"，取的就是那一支（Guid_Raw16 能力位由此为真）。
            var t when t == typeof(Guid) => "UUID",

            // 取 3 位（毫秒）那一档：DateTime64 不带精度默认是秒【文档·ClickHouse：DateTime64 数据类型页】，
            // CLR 时刻不落 3 位丢毫秒。
            var t when t == typeof(DateTime) => "DateTime64(3)",

            var t when t == typeof(DateOnly) => "Date",

            var t when t == typeof(char) => "String",

            // DateTimeOffset —— 本族没有带偏移的时刻型【文档没逐页回读】。§9.3 给这一档写的退路
            //（降级 DateTime UTC + 列标记 + 读取端还原）本仓库今天都不存在，宁缺不错。
            var t when t == typeof(DateTimeOffset) => throw new QuickSqlException(
                "ClickHouse 的 DateTimeOffset 列无处可发：这一族没有带时区偏移的时刻型【文档没逐页回读】。"
                + "偏移量在今天只有两条路：把 UTC 的 DateTime 存进 DateTime64(3) 列并自己记偏移，或把偏移拆成一列。"
                + "§9.3 给这一档写的退路那两半本仓库今天都不存在，所以宁缺不错（与 GBase 8s 同果不同因，记 §14）。"),

            // TimeOnly/TimeSpan —— 本族没有"一天的某个时刻"与"时间跨度"的独立型【文档没逐页回读】。
            // 抛而不猜：折算成 DateTime64(3) 或 Int64 微秒都要先做一次语义层定案，那不是本文件能代做的。
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => throw new QuickSqlException(
                "ClickHouse 的时刻/时间跨度列无处可发：这一族没有 TimeOnly/TimeSpan 的对应型【文档没逐页回读】。"
                + "折成 DateTime64(3) 或 Int64 微秒都要先做一次语义层定案，本仓库今天没有那两半，所以抛而不猜。"),

            // byte[] —— 本族没有大对象那一档的型【文档没逐页回读】。先编 Base64 再落 String 是现场自己的路，
            // 不是本层能替人选的（选了就是"每行字符数翻倍"的静默空间决策，§5.8 的反面）。
            var t when t == typeof(byte[]) => throw new QuickSqlException(
                "ClickHouse 的 byte[] 列无处可发：这一族没有大对象那一档的型【文档没逐页回读】。"
                + "先编 Base64 再落 String 是现场自己的路（那一手会静默放大占用），本层不替人选，所以抛而不猜。"),

            _ => throw new QuickSqlException($"ClickHouse 未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        // 与 DuckDbTypes 那一支同一格：这一家的字符串没有库侧第二把尺（String 不限长度），量纲落到
        // Char（仓库侧的字符数），MaxBytes 给一把"永不误拒"的宽松上界（§10.4）。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * Utf8WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 <see cref="LengthSemantics.Char"/>：本家 DDL 侧 String 型名里本来就没有那颗数，
    /// "库问得回第二把尺"的探测口（§10.4 那张表）在这里没有对象，尺子完全在仓库侧
    ///【文档·ClickHouse：String 数据类型页】。与 GBase 8s 那颗恒 Byte 的对照读方向相反，
    /// 两家都是退化实现而不是探测实现。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;
}
