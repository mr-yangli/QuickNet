using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.Gbase8s;

/// <summary>CLR ↔ GBase 8s（Informix 血统）类型名（§9.3 那一列的第六家，也是<em>整族自写</em>的第一家）。
/// 出处档位比另五家都薄，而且薄的不是<em>覆盖面</em>而是<em>可核性</em>【文档·IBM】：这一族的类型事实本次
/// 只有一档（IBM Informix 手册），没有 GBase 8s 自己的一页，也没有第二档"已发布适配器实际发过的型名"
/// —— 虚谷那一家靠两份 shipped 代码把【文档】与【产物】并排对读过（<c>XuguTypes</c> 类抬头那四档），
/// 这里两档合成一档。所以本文件每一行的档位都到不了 v0.32 那一格"实测"的高度，一次都没有。
///
/// <para>六处与另五家不同，逐条是这一格的分岔点：
/// <list type="bullet">
/// <item><b>宽度量纲恒字节，而且是本仓库第一颗<em>恒</em> <see cref="LengthSemantics.Byte"/> 的策略</b>：
/// <c>GetLengthSemantics</c> 在五家里的分布是「四家恒 Char ∶ 一家按 <c>Unicode</c> 分档」，这一家把它撑成
/// <b>四 Char ∶ 一 Byte ∶ 一分档</b>。分档那一家的理由是"两型的 n 不同义"（<c>nvarchar</c> 量字符、
/// <c>varchar</c> 量字节），而这一家只有一型（<c>VARCHAR</c>），它的 n 量字节 —— 于是"要不要分档"这一问
/// 在这一家<em>没有对象</em>：不是选了字节，而是压根没有第二个候选。
/// <b>这一格的直接后果写在 <see cref="GetStoreType"/> 的 string 那一臂</b>：DDL 里的 n 必须由字符数乘
/// 最坏字节数算出来，也就是说本家发出的宽度<em>永远比契约要的宽</em>（<c>string</c> 上 UTF-8 最坏 4 字节），
/// 而另一侧（<see cref="ColumnContract.FitsBytes"/>）用的也是同一颗乘数 —— 两处必须同源，
/// 否则就是"闸门按字符量、DDL 按字节写"那一类静默不一致（§10.4 那张表点的正是这一格）。</item>
/// <item><b>没有"带字符集的列定义"那一档</b>：Oracle 血统把 <c>VARCHAR2(50 CHAR)</c> 那种字形发进 DDL，
/// 也就是用<em>列定义自己</em>回答量纲问题（<c>OracleTypes.GetLengthSemantics</c> 因此能恒 Char）；
/// 这一家的 <c>VARCHAR(n)</c> 后面<em>没有</em>那个限定槽【文档·IBM】，量纲只能由会话的 codeset 决定，
/// 而 codeset 是建库时定的、SPI 里没有任何一格接住它 —— 这是 §14 那句"连接串参数改变方言假设"的
/// <b>第四个实例</b>（前三颗：金仓的 Oracle 兼容模式、MySQL 的 <c>lower_case_table_names</c>、虚谷的
/// <c>CHARSET=</c>），而它是四颗里唯一一颗<em>不在连接串上</em>而在服务端配置里的。</item>
/// <item><b>自增写在类型名上，而不是列尾的一句</b>：<c>SERIAL</c> / <c>SERIAL8</c> / <c>BIGSERIAL</c>
/// 是<em>类型</em>，不是 <c>IDENTITY(1,1)</c> 那种跟在宽度后面的子句【文档·IBM】。本文件最初的写法是让
/// <see cref="GetStoreType"/> 按 <c>IsIdentity</c> 在 <c>INTEGER</c> 与 <c>SERIAL</c> 之间分岔，
/// 而那一条<em>在本仓库的调用次序下不可能成立</em>，是本次由测试抓出来的：<b>共用层先定型、后判自增</b>
/// （<c>EntityContractFactory.Complete</c> 里 <c>ApplyStorageDefaults</c> 在第 130 行、
/// <c>IsIdentity</c> 的约定判定在第 133 行），所以本文件看到的每一颗列在定型那一刻 <c>IsIdentity</c> 恒假，
/// 而它算出的型名会被<em>echo 进契约</em>并在那里定死。<b>于是这一家的分工最终与另五家同一条</b>
/// （型名归本文件、自增归 <c>*CodeFirst</c>），只是换的那一步落在<em>两个型名之间</em>
/// （<c>INTEGER</c> → <c>SERIAL</c>）而不是列尾加一句 —— 见 <c>Gbase8sCodeFirst.SerialFor</c>。
/// 这一格整颗记 §13.5（本版被测试改掉的设计判断），而它顺带把"六家同一份 SPI"读出新的一半：
/// <b>provider 侧看不到约定层的结论，任何"型名取决于身份属性"的设计在这一层都写不出来</b>。
/// <b>更要紧的半格是：这一家只有起点没有步长</b> —— <c>SERIAL(n)</c> 的 <c>n</c> 是起始值，
/// 而步长（增量）压根不是列定义能说的话（<c>BY n</c> 那一支没有）【文档·IBM】，
/// 于是"步长"在契约里有格子、在这一家<em>无处发</em>。今天不报这个缺口是假的，它记 §14。</item>
/// <item><b>64 位整数有 <c>BIGINT</c> 与 <c>INT64</c> 两个名字，而 <c>SERIAL8</c> 与 <c>BIGSERIAL</c> 也是同义字</b>
/// 【文档·IBM】。本文件取 ANSI 那两支（<c>BIGINT</c> / <c>BIGSERIAL</c>），理由与 §9.3 那句"取跨库看得懂的那一形"
/// 同一条：<c>SERIAL8</c> 是 Informix 特有的名字，而 <c>BIGSERIAL</c> 与另五家的写法同源。
/// <b>取向不是能力</b>：目录里读回的是哪一颗名字，本文件没有读数能说（<c>syscolumns</c> 与
/// <c>systypes</c> 里那一格存的是什么，见 <c>Gbase8sSchemaReader</c> 类注释第三条）。</item>
/// <item><b>没有带偏移的时刻型</b>：不是"有那一型而驱动不认"（虚谷那一缺），也不是"那一型存的是会话偏移"
/// （MySQL 那一缺），而是<em>库里压根没有第三型</em>【文档·IBM】。所以 <see cref="GetStoreType"/> 的
/// <c>DateTimeOffset</c> 那一臂报的错话与另两家<em>不能是同一句</em> —— 在这一家说"驱动不认"会让人去查驱动，
/// 而那条路尽头没有东西。三家的缺法三条各不同，这是本文件里"同一个 CLR 型、三种不可表达"最齐的一格。</item>
/// <item><b>没有原生 UUID</b>：<c>Guid</c> 落 <c>CHAR(36)</c> 与达梦、MySQL 血统同一答案，而这一家
/// <em>连 <c>SYS_UUID()</c> 那一族取号函数都没有</em>【文档·IBM】 —— 虚谷那一格记的"库给值的三个出口"
/// 在这一家只剩两个（<c>DEFAULT</c> 表达式与 <c>DBINFO</c>），而这一档与它无关。
/// 于是"取文本那一支"在这里不是取舍而是唯一。</item>
/// </list></para>
///
/// <para><b>本文件产出的每一条型名都没有在一台活库上跑过，而且本机没有任何一台可以跑</b>。
/// 这句话在另五家是逐步收紧的（虚谷那一条在 v0.32 翻过一次，§13.5 记着翻的理由），在这一家是起点而不是结论：
/// 它不等<em>改判</em>，等<em>第一台库</em>。逐格欠的账写在 §13.5 与 §14 的本版那一段。</para></summary>
public sealed class Gbase8sTypes : ITypeMap
{
    public static Gbase8sTypes Instance { get; } = new();

    /// <summary>读侧的类型名表。键的取值集<em>不是</em>照抄驱动的自带表（另五家里有这份的只有虚谷，
    /// 而本家驱动 <c>GBase.DataDB</c> 从未解析成功过，§9.5），而是 <c>systypes.name</c> 那一列在
    /// IBM 手册里列出的内建型名【文档·IBM】。
    /// <b>这一格的处境要说死：它是一张"库会报什么名字"的清单，而不是一张"驱动认什么名字"的清单</b>，
    /// 两件事在另五家各有一条独立的来路，在这里共用同一档。后果是 <c>TryGetClrType</c> 答得出而
    /// <c>doctor</c> 的对账仍然可能错 —— 未登记的型答 false，那一条兜底不变。
    /// 带 <c>8</c> 后缀那一族（<c>int8</c> / <c>serial8</c> / <c>float8</c>）与 <c>INT64</c> 都收进来：
    /// Informix 的这几颗是同义字【文档·IBM】，而"库里那一列的名字是哪一颗"取决于当初谁建的表。</summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        // 布尔：这一家既有 INTEGER 承载的应用层布尔（我们自己的 DDL 发的就是那一型），也有
        // 库里别人建的 BOOLEAN 列。两行分开登记，与 <c>XuguTypes</c> 那句"目录怎么说我们怎么认、
        // 目录不说不猜"同一条判据。
        ["boolean"] = typeof(bool),
        ["tinyint"] = typeof(sbyte),
        ["smallint"] = typeof(short),
        ["int"] = typeof(int),
        ["integer"] = typeof(int),
        ["bigint"] = typeof(long),
        ["int8"] = typeof(long),
        ["serial"] = typeof(int),
        ["serial8"] = typeof(long),
        ["bigserial"] = typeof(long),
        ["smallfloat"] = typeof(float),
        ["float"] = typeof(double),
        ["float8"] = typeof(double),
        ["double"] = typeof(double),
        ["precision"] = typeof(double),
        ["decimal"] = typeof(decimal),
        ["numeric"] = typeof(decimal),
        ["money"] = typeof(decimal),
        ["char"] = typeof(string),
        ["varchar"] = typeof(string),
        ["lvarchar"] = typeof(string),
        ["clob"] = typeof(string),
        ["text"] = typeof(string),
        ["blob"] = typeof(byte[]),
        ["byte"] = typeof(byte[]),
        ["image"] = typeof(byte[]),
        ["date"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["interval"] = typeof(TimeSpan),

        // DATETIME YEAR TO SECOND 与 DATETIME HOUR TO SECOND 在目录里都读作 datetime【文档·IBM】，
        // 于是"库里那一列是时刻还是日期时刻"这一问<em>目录答不出</em>，只能靠 precision/width 那一格。
        // 这一句不是推测：本文件把 TimeOnly 落成 datetime hour to second，而读侧那一行仍然只能给
        // typeof(DateTime) —— 它是"型名不足分档"的一个具体形状，缺口记 §14，与虚谷那颗
        // "宽度与标度共用编码"同族而不同格。
    }.ToFrozenDictionary();

    /// <summary>字符→字节的乘数，取 4。<b>这一颗与本文件的量纲取向是绑死的</b>：另五家它是<em>宽松尺子</em>
    /// （列上量字符，MaxBytes 只是第二把尺），这一家它是<em>发射参数</em> —— DDL 里 <c>VARCHAR(n)</c> 的
    /// <c>n</c> 就是 <c>MaxChars × 本数</c>（见 <see cref="GetStoreType"/>）。
    /// 取 4 而不是 3：UTF-8 的最坏情况是 4 字节（代理对），而 codeset 也可能是 GB18030（同样 4）。
    /// 与 <c>XuguTypes</c> 那颗同名常量<b>同值而同形不同义</b>：那里乘出来只影响 <c>FitsBytes</c> 的松紧，
    /// 这里乘出来会写进建表文本。所以这一颗要<em>偏大</em>：宁可建出一列比契约宽的列，
    /// 也不能建出一列装不下契约值的列（§10.4 那句"由 provider 决定，不由注解决定"在这一家是唯一一处
    /// 决定<em>改变了库的存储</em>的）。代价也要说：一列 <c>string(100)</c> 在这一家是 400 字节，
    /// 而行预算是服务端算的，于是本家的行容量比量字符的几家小约 4 倍 —— 这一格今天没有槽位表达，
    /// 与 MySQL 那颗行级预算并排记 §14。</summary>
    const int WorstCaseBytesPerChar = 4;

    /// <summary>CLR 型 → 存储类型名；人已点名的那一格原样 echo（§4 那句"provider 不翻译人写的话"，六家同规）。
    /// <b>本方法与 <c>IsIdentity</c> 无关，而这不是取向而是一条读不到的约束</b>：共用层先定型后判自增
    /// （类注释第三条把那次改判记全了），所以 <c>SERIAL</c> 那一族的名字由 <c>Gbase8sCodeFirst</c> 换出来。
    /// 读侧因此要连着读两处：这里给出的是<em>非自增</em>那一支的名字。</summary>
    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return String(column);

        if (clrType == typeof(byte[]))
            return Blob;

        // 枚举存整数，与另五族同一决定，而这一家多一句：INTEGER 是 32 位，而 CLR 枚举的底层型
        // 可以是 long —— 底层型是 long 的枚举在这一家需要 [Column(TypeName=...)] 点名 BIGINT。
        // 另五家没写这一句不是它们对了，是它们的 INT 那一臂同样窄（MySQL 落 INT 也一样）。
        if (clrType.IsEnum)
            return "INTEGER";

        return clrType switch
        {
            // 布尔落 INTEGER 而<em>不用</em>它那一型 BOOLEAN —— 这是"五家→六家"那一格里第<b>三</b>家
            // 做出同一种选择（MySQL 落 TINYINT、虚谷落 TINYINT、这里落 INTEGER），而三条理由各不同：
            // 那两家的是"那一型装得下 2 所以 IS TRUE 与 = 1 不同判据"，这一家的是
            // <b>"库里 BOOLEAN 的存储形状本身在两档版本里不一致"</b>【文档·IBM】：早期实现的布尔列在
            // 目录与驱动侧读作单字符（'T'/'F'），后来的实现报 TRUE/FALSE —— 两种读法都不是 CLR 的 bool，
            // 而 SPI 里没有"这一型的读法随版本"那一格（§14）。
            // 于是三条判据合成一条本家的：<b>位只跟存储型走</b>（v0.35 第一笔立的那条规则，这是它第一次
            // 被<em>新落地的家</em>主动套用而不是被复述），INTEGER 上共用层的 = 1 / = 0 恰好成立，
            // Boolean_Type 因此不声明，而"库里有没有 BOOLEAN 这一型"与这一位无关。
            var t when t == typeof(bool) => "INTEGER",

            // 窄整数一律往宽里放，与五家同一条纪律。这一家没有 UNSIGNED 那一档【文档·IBM】，
            // 所以 MySQL 血统那份"unsigned 要单独一行"的读侧表在这里少一半。
            var t when t == typeof(byte) || t == typeof(sbyte) => "SMALLINT",
            var t when t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) || t == typeof(int) => "INTEGER",
            var t when t == typeof(uint) || t == typeof(long) => "BIGINT",
            var t when t == typeof(ulong) => "DECIMAL(20,0)",

            // FLOAT 在这一家是 8 字节的那一颗，4 字节的叫 SMALLFLOAT【文档·IBM】—— 与另五家
            // "FLOAT 跟着 CLR 的 double 走、REAL 才是 4 字节"的形状正好错开一格。
            // 这一格是本文件唯一一处"照抄型名会得到<em>错</em>的宽度"的：把 CLR float 落成 FLOAT
            // 不会报错，只会每行多 4 个字节而没人发现（§10.5 那一类"看着能用而语义少一截"的反面）。
            var t when t == typeof(float) => "SMALLFLOAT",
            var t when t == typeof(double) => "FLOAT",

            // DECIMAL 的精度上限是 32 位【文档·IBM】，比 Oracle 血统那四家（38）小，而这里给的默认
            // (18,4) 与五家同数、在范围内。ulong 那颗 (20,0) 也在。
            // 这一格因此是"§9.3 那句默认必须映射到宽的一侧"第一次<em>不成立</em>的一档：
            // 上限最小的那一家里，"宽的一侧"比别家窄，而契约没有那一问（§14）。
            var t when t == typeof(decimal) => $"DECIMAL({column.Precision ?? 18},{column.Scale ?? 4})",

            // DATETIME 在这一点上两半都是这一家特有的形状：<b>它不带尾随括号</b>，精度写在
            // "YEAR TO FRACTION(3)" 那一串字段名里【文档·IBM】，所以五家同一条"取 3 位（毫秒）"的纪律
            // 在这里第一次不是一颗限定词而是<em>一段字段范围</em>。
            // FRACTION 的可写档位本次没读到确切的最高档【文档·IBM，待逐页回读】，取 3 与五家同数；
            // 若这一家的上限其实是 2（百分之一秒），那么 CLR DateTime 的第 3 位在库里进不去，
            // 而今天没有任何一台库能量出来 —— 这一格整颗记 §14，与虚谷那颗"读回只到毫秒"并排。
            var t when t == typeof(DateTime) => "DATETIME YEAR TO FRACTION(3)",

            var t when t == typeof(DateTimeOffset) => throw new QuickSqlException(
                "GBase 8s 的 DateTimeOffset 列无处可发：这一家没有带时区偏移的时刻型（与另两家的缺法都不同 —— "
                + "虚谷是「库有那一型而驱动的 CLR 表不认」，MySQL 血统是「那一型存的是会话偏移而不是值的偏移」， "
                + "这里是库里压根没有第三型）。偏移量在今天只有两条路：把 UTC 的 DateTime 存进 DATETIME 列并自己记 "
                + "偏移，或把偏移拆成一列。§9.3 给这一档写的退路（降级 DateTime UTC + 列标记 + 读取端还原）那两半"
                + "本仓库今天都不存在，所以宁缺不猜（缺口与另两家并排记 §14，三条判据三条来路）。"),

            var t when t == typeof(DateOnly) => "DATE",

            // 时刻型在这一家不是独立的一型，而是 DATETIME 的<em>一个字段范围</em>【文档·IBM】。
            // 于是"TimeOnly 落在哪"这一问的答案是五家五种形状之外的第六种：既不是 TIME(3)（虚谷/达梦/
            // PG 那几档），也不是 TIME（T-SQL），而是一颗带限定词的 DATETIME。
            // 它与上面那颗 DateTime 列<em>在目录里同名</em>（都读作 datetime）—— 类注释读侧那一段说的
            // 就是这一格，而它是"型名不足分档"在本仓库的第一个实例。
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "DATETIME HOUR TO SECOND(0)",

            // 没有原生 UUID，落 CHAR(36) —— 与达梦同一答案而<em>不同</em>因（那一家是"没有 uuid 型"，
            // 这一家连取号函数那一族都没有，类注释第六条）。这里的 36 是<em>字符</em>，而这一家的
            // CHAR(n) 量的是<em>字节</em>：36 颗 ASCII 恰好 36 字节，所以这颗数在本家不需要乘 4，
            // 而这一点只有 GUID 的 'D' 形（恒 ASCII）成立。想换成手写仍然够得到：
            // [Column(TypeName = ...)] 在第一行短路。
            var t when t == typeof(Guid) => "CHAR(36)",
            var t when t == typeof(char) => "CHAR(1)",
            _ => throw new QuickSqlException($"GBase 8s 未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    /// <summary>string → <c>VARCHAR(字符数 × 4)</c>。乘出来的那颗数<em>就是</em> DDL 里的 n（类注释第一条）。
    /// 无宽度那一支发 <c>CLOB</c> 而不是"很宽的 VARCHAR"：这一家的 <c>VARCHAR</c> 上限是<em>服务端配置</em>
    /// 决定的一档（<c>ONCONFIG</c> 的 <c>SQL_VARCHAR</c>，取值范围与默认值本次都没有逐页回读【文档·IBM】），
    /// 所以这里<em>不写</em>一颗数——不写比写错一档诚实，而后果是"超过现场上限的声明会在库里被拒"。
    /// 那一格与 §14 那颗"行级预算没有槽位"并排，而且它比 MySQL 那颗更缺：<b>这一家连列级上限都不是常数</b>。</summary>
    static string String(ColumnContract column)
        => column.MaxChars is { } chars
            ? $"VARCHAR({chars * WorstCaseBytesPerChar})"
            : "CLOB";

    /// <summary>byte[] → <c>BLOB</c>。契约里没有"这一列二进制最多几字节"那一格（<c>MaxBytes</c> 量的是
    /// 文本的字节尺子，见 <c>ColumnContract</c> 那一段），所以本家没有别的选择：只有无上限那一档接得住
    /// "没说过宽度"这个语义 —— 与 <c>XuguTypes</c> 那句同一格而不同族（那里是 2GB 对 64KB，这里是
    /// "BLOB 与 BYTE 两档，而 BYTE 有上限"）【文档·IBM】。</summary>
    const string Blob = "BLOB";

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? Blob };

        // 这一支是本文件与另五家<em>分岔最大</em>的一处，所以逐格说：
        //   Unicode 取 false —— 这一家没有 N'' 那一形（<c>Gbase8sDialect.PrefixUnicodeLiterals</c> 因此恒 false），
        //     而 Unicode 那颗位在别的家管的是"n 量字符还是字节"，在这里量纲已经由库定了，
        //     留下 false 说的是"没有宽字符型"这一件事实。取 true 会让 <c>ColumnContract.NullableInStore</c>
        //     那条链上游的读法（<c>nvarchar/nchar</c> 才是 Unicode 列）在本家变成一句假话。
        //   LengthSemantics 取 Byte —— 与 GetLengthSemantics 同一颗数，两处不许分岔（类注释第一条）。
        //   MaxBytes 取"已算出来的那个字节数"而不是"乘一把宽松的尺"：在量纲本来就是字节的库里，
        //     MaxBytes 与 DDL 里的 n 必须是<em>同一个数</em>，否则闸门量的与建表写的差 4 倍，
        //     那是 §10.4 那张表立起来要挡的第一号错。
        return column with
        {
            Unicode = false,
            LengthSemantics = LengthSemantics.Byte,
            MaxBytes = column.MaxChars is { } chars ? chars * WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 <see cref="LengthSemantics.Byte"/>。<b>这是六家里第一颗恒字节</b>，分布从
    /// 「四家恒 Char ∶ 一家分档」变成「四 Char ∶ 一 Byte ∶ 一分档」。分档那一家的理由（两型的 n 不同义）
    /// 在这一家没有对象：只有一型字符列，而它的 n 量字节 —— 所以这一格<em>不是选择</em>，
    /// 是"另一支候选在这一家压根不存在"。与 §10.4 那张表的关系要说清：那张表量的是
    /// <b>能不能从库里问出第二把尺</b>，而这一家问不出（目录里只有一个 <c>syscolumns.width</c>，
    /// 见 <c>Gbase8sSchemaReader</c>），所以本家落在<b>与虚谷、金仓同一档</b>（退化实现而不是探测），
    /// 而"取 Byte"这一格比那几家多承担一层后果：那几家的量纲不影响 DDL 里的数字，这里影响。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Byte;
}
