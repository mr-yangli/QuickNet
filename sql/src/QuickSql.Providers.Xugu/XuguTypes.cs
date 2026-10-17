using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.Xugu;

/// <summary>CLR ↔ 虚谷类型名（§9.3 那一列的第五家）。与已落地四族逐条对照，而这一家的口径要先说清：
/// <b>它是本仓库第一份"类型事实有两个独立出处"的 provider</b>。前四族的类型表按厂商文档写、或者按另一家
/// 的文档类比写；这一族既有厂商一手文档（docs.xugudb.com 的 <c>sql/datatype</c> 那一整组页面），
/// 也有两份已发布的第三方适配器<em>实际发出去</em>的型名。四档出处在本文件里逐格标：
/// <list type="bullet">
/// <item>【文档】—— 厂商自己那一页写着的。这一档第一次不是唯一的出处，而是<em>可被产物推翻或印证</em>的那一个。</item>
/// <item>【产物】—— 有 shipped 代码这么发过（两家适配器各自一份，两份互相矛盾的地方比一致的地方多）。</item>
/// <item>【推断】—— 两档都没有、按"与已落地四族同形"挑的那一支。<b>本文件里这一档在 v0.32 第二格清零</b>：
/// 原来只剩的那一处是 <c>DATETIME(3)</c> 的那颗精度限定词，它现在被目录读出来了，见下面第四条与那一行。</item>
/// <item><b>【实测】—— 本文件新加的那一档，也是这一族第一次有它</b>：这一版把建表文本发进了一台真的
/// 虚谷实例（§13.17 授权到的那一档：建 <code>TESTDB</code> 里明确命名的靶表），于是本文件的产出文本
/// 头一回有了"库那边收下了、而且按发出去的宽度存着"的旁证。<b>四档之间不是替代而是层层收紧</b>：
/// 厂商写"能到 60000"，产物写"有人这么发过"，实测写的才是"这台库这一版收下了这一句"。
/// 而它有一道<em>自己的</em>限度，别读越了：这一档只覆盖<b>本族自己发出去的那些句子</b>，
/// 覆盖面逐颗写在 §13.5 那本台账里，而它<em>不</em>等于"虚谷已适配"。</item>
/// </list>
///
/// <para>五处与四族不同，逐条是这一格的分岔点：
/// <list type="bullet">
/// <item><b>宽度量纲是字符，而且这一条现在有两档出处：【文档】写明、真库答对</b>：<c>VARCHAR(n)</c> 的 <c>n</c>
/// 在厂商那一页明写"单位 = 字符"，并且给了 <b>1–60000</b> 的范围【文档】。实测补的是"闸门真的按字符落"
/// 那一半：一张只有 <c>"S" VARCHAR(4)</c> 的靶表，灌 <c>'abcde'</c>（5 颗 ASCII）交回
/// <c>E19230 字段(col_no:0,col_name:S)值错误(数据异常/字串超长)</c>，灌 <c>'甲公司'</c> 在声明了
/// <c>CHARSET=UTF8</c> 的连接上<em>存进去</em>了 —— 同一颗串在同一台库上有两种字符数，见下面那颗"两种
/// 读法"的分岔。<b>于是 <see cref="GetLengthSemantics"/> 恒 <see cref="LengthSemantics.Char"/> 这一格是
/// 五家里的第四颗</b>（只有 T-SQL 一家按 <c>Unicode</c> 分档，判据与逐家出处见该方法自己的注释），
/// 而"量纲是字符"这件事<em>由厂商文档正面写明</em>的是第二家
/// （第一颗是金仓那一族；MySQL 血统那颗靠历史推、Oracle 血统那颗写在列定义上），而 §10.4 那句"库里问不出字节"
/// 在这一家有具体形状：目录里只有一个宽度，而且那一格与精度/标度<em>共用编码</em>
/// （见 <see cref="XuguSchemaReader"/> 类注释里的 <c>SCALE</c> 拆包）。
/// <b>但它问得出来一颗本文件原先没有的名字</b>：<c>all_databases.CHAR_SET</c> 这一格在真库上答
/// <c>"GBK"</c>（四颗库名逐行都是它），而值上的字节数也量得到（<c>LENGTHB('甲公司')</c> = 6）——
/// 所以"报不出第二个数"那句要收窄成"报不出<em>某一列</em>的字节预算"。</item>
/// <item><b>有宽度闸门，而且闸门是列级的</b>：<c>MaxVarcharChars = 60000</c>。这一格与 <c>MySqlTypes</c>
/// 对照着读才有意思 —— 那边算出来的 16383 是<em>行</em>级预算折算到列上的数（65535 / 4），
/// 而这里是列定义本身的上限（超过就不是"写宽一点"而是<em>换一档型</em>）。两家都填 <see cref="ColumnContract.MaxChars"/>
/// 那一个槽位，含义不同一条。<b>而这一家这一颗闸门是这一版唯一一颗"两侧各问了一次"的数</b>【实测】：
/// <c>VARCHAR(60000)</c> 建得成，<c>VARCHAR(60001)</c> 被 <c>E19258 … 60000</c> 拒（BT-40a/b，
/// 报错里带的就是那一位数），而建成那一列在目录里报 <c>TYPE_NAME="CHAR" SCALE=60000</c> ——
/// 宽度落在与标度共用的那一格里、且 60000 &lt; 65536 那颗进制基数，所以它不会与
/// <c>精度 × 65536 + 标度</c> 那一支撞车（拆包见 <see cref="XuguSchemaReader"/> 第三条）。
/// <b>另有一颗行级闸门今天没有槽位承接</b>：一行的记录大小上限 64K【文档】，
/// 而 <c>ColumnContract</c> 只有"这一列多宽"没有"这张表还剩多少行预算"（缺口与 MySQL 那一格并排记 §14）。
/// <b>同一跑里还量到一颗不在闸门表上的数</b>：往 <c>TEXT</c> 列绑一颗串，<c>40 000</c> 字进得去而
/// <c>60 000</c> 字被 <c>E17111</c> 拒（BT-41b 那道梯子，逐档 1 000 / 8 000 / 32 767 / 40 000 绿、60 000 红）——
/// "没有宽度的那一档型"在绑定侧仍有一道没被文档写出的闸，边界在 40 000 与 60 000 之间，
/// 这一条与上面那颗闸门无关，记 §14。</item>
/// <item><b>它有原生 <c>BOOLEAN</c> 而这一版仍把布尔落成 <c>TINYINT</c></b>：型那一页写 <c>BOOLEAN/BOOL</c>
/// 一字节、取值 <c>TRUE|FALSE|NULL</c>、并且有一句"<em>数字非零即真</em>"【文档】。所以"为什么不声明
/// <c>Boolean_Type</c>"在这一家不能拿 MySQL 那条理由（那边的 <c>TINYINT(1)</c> 装得下 2），
/// 真正的理由写在 <see cref="GetStoreType"/> 与 <c>XuguDialect</c> 类注释第六条里 ——
/// 这一格是五家里第一次出现"库确实有那一型而这里仍不用它"，所以它比另四家都需要写清。
/// <b>而"写清"的三条理由今天各自有了读数，其中一条被驳回</b>【实测·BT-01a…BT-11】：布尔列上
/// <c>= 1</c> 数到 1、<c>IS TRUE</c> 在布尔列与整数列上<em>都</em>合法、BOOLEAN 列读回 CLR <c>Boolean</c>。
/// 于是"不声明那一颗位"剩下的理由是形状一致与代价可数两条，而不是"要多猜一处"；改判条件今天到场，
/// 决定本身留给 §14 那一列（它改的是五家共用层的发射形状）。<b>v0.35 第一笔裁决回格：这一位在布尔仍落
/// <c>TINYINT</c> 期间保持不声明 —— 位只跟存储型走，不跟库的能力走；重开点是布尔落原生 <c>BOOLEAN</c>
/// 的那一天，而那一天与"全新虚谷库复跑 + 家族专属夹具模型"那笔欠账同触发（规则全文在 §14 那一列）。</b></item>
/// <item><b>时刻有两型、<c>TIME</c> 的精度是文档级而 <c>DATETIME</c> 的精度这一版量到了</b>：文档给
/// <c>TIME[(p)]</c> 明写了 <c>p ∈ [0,3]</c>【文档】，而 <c>DATETIME</c> 与 <c>TIMESTAMP</c> 那两行没带限定词。
/// 于是"同一个精度限定词在相邻一型上是文档级、在这一型上是猜的"这个分岔，<b>后一半在 v0.32 第二格合上了</b>：
/// 本族发出去的 <c>"CREATEDAT" DATETIME(3)</c> 在真库目录里读回 <c>TYPE_NAME="DATETIME" SCALE=3</c>
/// （两颗时刻列逐行，见 <c>XuguTypes</c> 下面那一行与 §13.5 的 F-54）。<c>TIME(3)</c> 那一颗仍是文档级
/// —— 它没被任何一句真发过的 DDL 带进去，所以这一格里两列仍不同档，样本的价值留着。
/// <b>顺带量到的两颗数是这一家自己的天花板，与厂商给 TIME 的那一句<em>不通用</em></b>【实测·BT-20/BT-30】：
/// <c>DATETIME</c> 的限定词能写到 <c>(6)</c>，写 <c>(7)</c> 被 <c>E2051 … 6</c> 拒，裸写的默认是 6，
/// 而"文档只给 TIME 到 3"那一页如果照抄到这一型上，就会把"我们发 (3) 是<em>五家同一条纪律</em>"
/// 误读成"(3) 是这一家的上限"。两句是两句。</item>
/// <item><b><c>CHAR</c> 与 <c>VARCHAR</c> 存入时去掉尾部空格</b>【文档】。这一条不改本文件的任何一行代码，
/// 但它改两处<em>读法</em>：<see cref="XuguValues"/> 与 <c>XuguChangeGuard</c> 里"值变了没有"的判据在这一家
/// 是"去尾空格之后变了没有"。那是一条厂商明写的行为而不是隐蔽的坑，而它恰好落在 §10.5 那一类
/// "看着能用而语义少一截"的对面 —— 知道它才写得对，所以记在这里而不是藏在别处（回写 §9.3 同一格）。
/// <b>这一条今天有读数了</b>【实测·E-10d/e，自建表 <c>CHAR(10)</c> 收 <c>'ab  '</c>】：
/// <c>LENGTH</c> 交回 <c>2</c>（既不是补出来的 10 也不是带尾空格的 4），而与无空格对照列的 <c>=</c> 数到 <c>1</c>。
/// 同一跑里另一颗顺带量到的是 <c>EMPTY_STR_AS_NULL</c>：空串那一列 <c>IS NULL</c> 数到 <c>0</c> 而
/// <c>= ''</c> 数到 <c>1</c>（两向相加 = 1 才自证），也就是这台库上它就是默认关的那一档 ——
/// 那颗参数没读出来（<c>all_variables</c> / <c>props</c> / <c>all_props</c> / <c>all_db_info</c> / <c>CHARSET()</c>
/// 五问全报"不存在"，N-01c…f），而它现在<em>有了一条行为侧的旁证</em>。</item>
/// </list></para>
///
/// <para><b>"本文件的产出文本没有在一台真库上跑过"这句在 v0.32 第二格作废了</b>（原判：授权的实例里没有一家是
/// 虚谷）。这一版有了一台，而它改判的是<em>存在</em>不是<em>覆盖面</em>：跑过的是本文件发出去的那几型名
/// （建表文本被这台库收下、目录按发出去的宽度与标度存着），而"跑过"两字仍然不等于"虚谷已适配"——
/// 三句限制写在 §13.5 那本台账的抬头，逐颗记在那里而不是在这里打包。
/// 与另四族仍然不同的是，这一族的【文档】档给的是<em>范围与单位</em>这种可核对的量，所以现在它多了一档
/// 能逐格并排的出处。<b>而实测新添的不是印证，是一格原文没有的岔口</b>：<em>同一颗串在同一台库上有两种读法</em>
/// —— 库的 <c>CHAR_SET</c> 是 <c>GBK</c>（目录答的），而驱动把字符切成几颗由<em>连接串上那颗
/// <c>CHARSET=</c></em> 决定：不带它，<c>'甲公司'</c> 在这一台库上是 <c>LENGTH</c> 5 / <c>LENGTHB</c> 9，
/// 灌进 <c>VARCHAR(4)</c> 被 <c>E19230 字串超长</c> 拒掉；带上 <c>CHARSET=UTF8</c>，同一颗串是 3 / 6，
/// 同一句灌入成功。<b>于是本文件那张表里每一颗宽度数字都有两种含义</b>，而 SPI 里没有一格接住那颗开关
/// （§14 那句"连接串参数改变方言假设"的第三个实例，与上面 <see cref="WorstCaseBytesPerChar"/> 同一格）。</para></summary>
public sealed class XuguTypes : ITypeMap
{
    public static XuguTypes Instance { get; } = new();

    /// <summary>读侧的类型名表。键的取值集<em>照抄</em>驱动自带的"存储类型名 ↔ CLR 类型"那一份【产物】
    /// （它就是这个驱动怎么认自己的型），再加三颗厂商文档有、那份表没有的名字：
    /// <c>VARCHAR</c> 与 <c>TEXT</c>（两家适配器都发，而驱动表只有 <c>CHAR</c>/<c>CLOB</c>）、
    /// <c>INTEGER</c>（<c>INT</c> 的同义字【文档】）。
    /// <b>那三颗加进来的就是本文件唯一一处"两份出处互相沉默"</b>：驱动表没有 <c>VARCHAR</c> 这一行，
    /// 可能是那张表<em>不全</em>（它同样没有 <c>VARCHAR</c> 却给了 <c>CLOB</c>），也可能是库会把
    /// <c>VARCHAR(n)</c> 改名后存进目录（MySQL 血统的 <c>tinyint(1)</c> 就是这一档的先例）。
    /// 两种解释在目录读出上的后果不同，而今天没有一台库能分辨 —— 归 §14 那份"有真库第一天要重读的清单"。</summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        // 驱动那份表里 bool 与 tinyint 是两行【产物】，所以这里也分两行。写侧两型都产得出
        // （库里别人建的表带 BOOLEAN，我们自己的 DDL 发 TINYINT），而读侧只有下面这一条会撞上
        // "库里是 BOOL 而 CLR 想要 bool"以外的分支 —— 与 MySqlTypes 那句"看到 tinyint 就当 bool"
        // 是同一条不许猜的纪律：那一格在 MySQL 血统是"分不出布尔与 8 位整数"，在这里是"分得出，
        // 因为驱动登记了两个名字"，两家的答案不同而<em>判据</em>同一条：目录怎么说，我们怎么认；目录不说，不猜。
        ["bool"] = typeof(bool),
        ["boolean"] = typeof(bool),
        ["tinyint"] = typeof(sbyte),
        ["smallint"] = typeof(short),
        ["int"] = typeof(int),
        ["integer"] = typeof(int),
        ["bigint"] = typeof(long),
        ["float"] = typeof(float),
        ["real"] = typeof(float),
        ["double"] = typeof(double),
        ["numeric"] = typeof(decimal),
        ["decimal"] = typeof(decimal),
        ["char"] = typeof(string),
        ["varchar"] = typeof(string),
        ["clob"] = typeof(string),
        ["text"] = typeof(string),
        ["blob"] = typeof(byte[]),
        ["date"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["timestamp"] = typeof(DateTime),
        ["time"] = typeof(TimeSpan),
        ["guid"] = typeof(Guid),

        // 带时区那三型（TIME WITH TIME ZONE / DATETIME… / TIMESTAMP WITH TIME ZONE【文档】）刻意<em>不</em>登记：
        // 驱动侧没有 System.DateTimeOffset 那个 CLR 键【产物】，把它们读成 DateTime 会<em>静默丢偏移</em>，
        // 与 §5.8 禁的是同一件事。未登记的后果是 TryGetClrType 答 false，
        // 于是 <c>doctor</c> 报"这一型不认识"而不是悄悄给一个折过值的 DateTime。
    }.ToFrozenDictionary();

    /// <summary>宽度→字节的乘数，取 4。这一颗是<em>宽松尺子</em>而不是闸门换算：字符集是<em>会话级</em>选的
    /// （连接串上有 <c>CHAR_SET=</c>，厂商给的示例串里 GBK 与 UTF8 两种都出现过【文档】），
    /// 而本 SPI 没有任何一格接它 —— 这是 §14 那一行"连接串参数改变方言假设"的<b>第三个实例</b>
    /// （前两颗是金仓的 Oracle 兼容模式与 MySQL 的 <c>lower_case_table_names</c>）。
    /// 乘 4 覆盖 UTF-8 与 GB18030 两档里较宽的那一侧，所以它只会<em>偏大</em>。
    /// 与 <c>MySqlTypes</c> 同形而不同因：那边乘出来的数对应一行级真实预算（偏大偏小都会改变块数），
    /// 这边只影响 <see cref="ColumnContract.FitsBytes"/> 的松紧，而这一家的行预算（64K【文档】）在这里量不到。</summary>
    const int WorstCaseBytesPerChar = 4;

    /// <summary>单列 <c>VARCHAR</c> 装得下的字符数上限：<b>60000</b>，直接抄厂商那一页给的列定义范围
    /// （<c>CHAR[(n)] 1–60000</c>、<c>VARCHAR[(n)] 1–60000</c>，且明写单位是字符【文档】）。
    /// 与 <c>MySqlTypes.MaxVarcharChars</c> 那个 16383 对照：<b>那一颗是推出来的</b>（行预算 65535 除以
    /// <c>utf8mb4</c> 的最坏 4 字节），这一颗是<em>写在那一页上的</em>。两处同一个槽位、两种出处档位，
    /// 而超过它的处理一致：换一档型而不是报一个"再宽一点"的错。
    /// 顺带记一处这一家特有的余量：那一行还有一个"不写长度时 <c>VARCHAR</c> 吃掉本行剩余空间"的默认【文档】，
    /// 所以"超过 60000"与"不写宽度"在库那边不是同一件事 —— 本文件两样都碰了（这里换 CLOB，下面 <c>null</c> 那一支发 TEXT）。</summary>
    const int MaxVarcharChars = 60_000;

    /// <summary>CLR 型 → 存储型名；而<em>人已点名的那一格原样 echo</em>。
    /// <c>[Column(TypeName = …)]</c> 在这一族"认哪些名"的清单因此是：<b>全都认——按不校验、不翻译的规矩穿过</b>
    /// （§4 那句"provider 不翻译人写的话"，五家同规），唯一的例外不在这颗口上：
    /// <c>rowversion</c> 由 <c>XuguCodeFirst.EmitColumnDefinition</c> 在 DDL 发射点拒收（v0.34 第二笔，§14）——
    /// 那里管的是"我们要不要建它"，这里管的是"这一格叫什么"；读侧目录对账（<c>XuguSchemaReader</c>）
    /// 仍要能说出别人建的表里有什么，所以这颗口保持无闸门，别把拒收搬进来。</summary>
    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        // 超过列级上限就不是"这一列写宽一点"而是换一档型：TEXT 在这一家是 VARCHAR 的同义字【文档】，
        // 而 CLOB 是另一档（2GB【文档】）。所以无宽度那一支发 TEXT（跟着两家产物的形状【产物】），
        // 超上限那一支发 CLOB —— 两支的差别是"这一列有没有宽度声明"，不是"哪一档更大"。
        if (clrType == typeof(string))
            return column.MaxChars switch
            {
                null => "TEXT",
                var chars when chars > MaxVarcharChars => "CLOB",
                var chars => $"VARCHAR({chars})",
            };

        // 二进制走 BLOB 而不是 BINARY：厂商那一页给 BINARY 的上限是 64KB 且<em>没有</em> VARBINARY 那一型【文档】，
        // 而 byte[] 在契约上没有宽度槽位（MaxBytes 量的是文本的字节尺子）。所以只有 2GB 那一档接得住"没上限"这个语义。
        if (clrType == typeof(byte[]))
            return "BLOB";

        // 枚举存整数，与另四族同一决定。这一家的驱动类型表里没有枚举那一行【产物】，
        // 而两家适配器也一律发 INT，所以这一格是"四族同形 + 产物同形 + 文档沉默"三支指向同一个答案的少数格。
        if (clrType.IsEnum)
            return "INT";

        return clrType switch
        {
            // 布尔落 TINYINT 而<em>不用</em>它原生的 BOOLEAN —— 这一格是五家里第一次"库有那一型而这里不取"，
            // 所以要把三条理由都摆出来（前两族的"那一型装得下 2"在这里不成立）。
            // <b>v0.32 第二格给这三条各自落了一次读数</b>（<c>obj/logs/v32-recon-booltz-3.log</c> 的 BT-01a…BT-11，
            // 建的是本探针自建的 <c>QUICKSQLRECONBOOL ("B" BOOLEAN, "I" INT)</c>，跑完自己删）：
            // 其一，共用层的布尔谓词是 = 1（DialectBase.EmitBooleanEquality 那一支），布尔内联也是 1/0，
            //     于是"列是整数、谓词比整数、字面量是整数"三处落在同一个形状上；换成 BOOLEAN 就要三处一起改，
            //     而三处里唯一能撑起"改对了"的证据是厂商那句"数字非零即真"—— 那一句说的是<em>数字进布尔</em>，
            //     不是"布尔列上写 = 1 合法"，方向差一格。<b>那一格今天被 BT-03 填上了：布尔列上 <c>WHERE "B" = 1</c>
            //     数到 1</b>【实测】，所以这一条不再是"方向不明"，而是"形状一致性"这一条纯工程理由。
            // 其二，声明能力位 Boolean_Type 等于把 EmitBooleanEquality 推到 IS TRUE 那一支（金仓那一条路），
            //     而这一家有没有 IS TRUE【两份出处都没写过】，所以那一步不是"更忠实"而是"多一处猜"。
            //     <b>这一条是本臂三条里唯一被<em>驳回</em>的那一条</b>【实测】：<c>IS TRUE</c> 在这一台库上
            //     落在 BOOLEAN 列（BT-02 数到 1）与<em>整数</em>列（BT-11 数到 1）上都合法 —— 后那一支才算数，
            //     因为改判之后我们自己建的列在库里仍是 TINYINT，谓词要落的就是整数列。
            // 其三，代价可数：库里别人建的表带 BOOLEAN 时读侧认得（StoreToClr 里那两行），
            //     只有"我们自己建的布尔列在库里的名字与厂商推荐不同"这一格，而它不改任何行为。
            //     这一条今天有了旁证【实测】：<c>SELECT "B"</c> 从 BOOLEAN 列交回 <c>True &lt;Boolean&gt;</c>
            //     （BT-04），也就是驱动认得它自己那一款 BOOLEAN，读侧那一行不是猜的。
            // <b>原第三条那句"谁接了 IS TRUE 的合法性，这一格就重开"—— 条件今天到场了，而这一行没动</b>，
            // 理由要说清才不算把改判拖成搁置：那一颗位真正改的不是类型名而是<em>发射形状</em>
            // （<c>EmitBooleanEquality</c> 从 <c>= 1</c> 换到 <c>IS TRUE</c>，五家共用层跟着变，
            // 而 §13.4 那 95 条里没有一条能证明换完还对 —— 布尔在夹具里压根没有列）。
            // 所以"要不要声明 <c>Boolean_Type</c>"与"建表要不要用 <c>BOOLEAN</c>"两格一起交 §14 的用户那一列，
            // 判据与另两颗（<c>CHARSET=UTF8</c> 注入、<c>rowversion</c> 拒收）并排，而这一版交的是<em>读数</em>不是决定。
            // <b>v0.34 第二笔把并排三颗里的 <c>rowversion</c> 那颗结了</b>（「全部完善」整批授权，裁决自决为拒收，
            // 落点在 <c>XuguCodeFirst.EmitColumnDefinition</c>）；~~本文件这一格（布尔落哪一型）与 CHARSET 那颗仍开在 §14。~~
            // <b>v0.35 第一笔把剩下的三颗一次收口，裁决都是"不动"，而各有一条规则</b>：CHARSET 裁成
            // ②"跟着会话，工厂一生不碰 <c>CHARSET=</c>"（非 GBK 实例上的 LENGTH∶LENGTHB 探针是唯一重开触发）；
            // 布尔两问裁成 <b>"位只跟存储型走：bool 落 <c>TINYINT</c> 期间 <c>Boolean_Type</c> 永不声明"</b> 与
            // <b>"列型跟着已建成的走：改 <c>BOOLEAN</c> 零行为收益、造新旧库分叉"</b>，共同重开触发 =
            // "全新虚谷库复跑 + 家族专属夹具模型"那笔欠账（v0.34 第二笔欠账 ①）。规则全文在 §14 三格里；
            // 这一行落 <c>TINYINT</c> 从今天起是<b>裁决定住的</b>，不是暂时的。
            var t when t == typeof(bool) => "TINYINT",

            // 窄整数一律往宽里放，与四族同一条纪律。这一家没有 UNSIGNED 那一档【文档的类型页没列】，
            // 所以 MySQL 血统那份"unsigned 要单独一行"的读侧表在这里少一半（上面那张表的键数就是差出来的）。
            var t when t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) || t == typeof(int) => "INT",
            var t when t == typeof(uint) || t == typeof(long) => "BIGINT",
            var t when t == typeof(ulong) => "NUMERIC(20,0)",
            var t when t == typeof(float) => "FLOAT",
            var t when t == typeof(double) => "DOUBLE",

            // NUMERIC 的精度上限是 38 位、默认 NUMERIC(12,0)【文档】，所以 ulong 那一颗 (20,0) 在范围内、
            // 而这里给的默认 (18,4) <em>比库的默认宽</em> —— 与 §9.3 那句"默认必须映射到宽的一侧"同一条判断。
            var t when t == typeof(decimal) => $"NUMERIC({column.Precision ?? 18},{column.Scale ?? 4})",

            // 那颗 (3) 曾经是<em>本文件最后一处【推断】</em>，v0.32 第二格把它量掉了【实测·目录】：
            // 本族建表文本发出去的两颗 <c>DATETIME(3)</c> 列，在真库目录里读回
            // <c>TYPE_NAME="DATETIME" SCALE=3</c>（F-54，靶表 QUICKSQLORDER 的 CREATEDAT / PAIDAT 逐行），
            // 也就是<em>那一颗限定词吃进去了、而且落在跟 TIME 同一个标度槽上</em>；而值上的旁证更早一版就有：
            // F-40 读回 <c>2026-01-05 08:30:00.1230000</c>，亚秒没被截。
            // <b>今天"库里存了 3"那一格升级成了"库只能存到几"</b>（BT-20 与 BT-30 两组，
            // <c>obj/logs/v32-recon-booltz-3.log</c>）：同一颗串 <c>…06:06:06.123456</c> 灌进
            // <c>DATETIME(3)</c> / <c>DATETIME(6)</c> / 裸 <c>DATETIME</c> 三列，判据一律取<em>谓词</em>不取读值 ——
            // <c>WHERE x &gt; '…1234'</c> 在 (6) 与裸列各数到 <c>1</c>，在 (3) 那一列数到 <c>0</c>，
            // 于是"第 4~6 位在库里在不在"这一问按列分开答了：<b>(3) 是真的截，不是目录里的一个数</b>。
            // 天花板也同一跑问出来了：<c>DATETIME(7)</c> 与 <c>(8)</c> 建表都被拒，报的是同一句
            // <c>E2051 … DATETIME … 6</c>（那一串中文过境受损，取的只有型名与那一位数），
            // 而目录给的 <c>SCALE</c> 恰好是限定词的回声：3→3、6→6、<em>裸→6</em>。
            // <b>那这一行为什么仍写 (3)？因为这一格不是这一家自己的决定，是五家同一条纪律</b>：
            // <c>grep -rn 'typeof(DateTime) =&gt; "' src/QuickSql.Providers.*/</c> 五处产出各带一颗 <c>(3)</c>
            // （<c>DATETIME2(3)</c> / <c>TIMESTAMP(3)</c> / <c>DATETIME(3)</c> 两种型名同一颗数），
            // 单把这一家放到 6 会让"同一份 CLR 值在五家留几位"这条跨库判断变成逐家问。
            // 收窄的代价这一版有了具体形状：CLR <c>DateTime</c> 的第 4~7 个十进制位在<b>我们自己建的表里</b>进不去。
            // 而它还有一层今天<em>看不见</em>：<b>驱动往 CLR 读回的那一刻只到毫秒</b> —— 同一张表上三列
            // （含库里确实留着 .123456 的那两列）经 <c>GetValue</c> 全交回 <c>…1230000</c>（BT-20h），
            // 也就是"读侧看不见第 4 位"这件事在这一家与建表那一格无关。两档各记 §14，别并成一条。
            // 与 TIMESTAMP 的选择也在这里定：文档给 DATETIME 与 TIMESTAMP 两型，而后者在 MySQL 血统那一家
            // 带"按会话时区解释 + 2038"的历史包袱 —— 这一家没说它有没有同一档【文档沉默】，取名字更直白的那一个。
            // <b>这一句今天被降级，而降它的是"两型之间问不出差别"这一组读数</b>【实测】：
            // <c>TIMESTAMP</c> 建表收下，而目录把那一列的 <c>TYPE_NAME</c> 报成 <c>DATETIME</c>、
            // <c>SCALE</c> 走同一颗标度槽（BT-08），两列并读同一颗串交回<em>一字不差</em>的两个值（BT-07），
            // 2040 与 1960 两个年份都进得去、读回都不挪小时（BT-06/09/10）。
            // 所以这一家在<b>这一版问到的那一档上</b>没有 MySQL 那一族的 TIMESTAMP 包袱：它压根是同一型的两个名字。
            // 取 <c>DATETIME</c> 因此不需要理由了，而"避开包袱"那句也不再是理由 —— 留下的只是"与目录同名"。
            var t when t == typeof(DateTime) => "DATETIME(3)",

            // 抛。这一家<em>有</em>带时区的时刻型（TIME / DATETIME / TIMESTAMP 三型各带一个 WITH TIME ZONE 档【文档】，
            // 而这一版把其中一档<em>建出来了</em>：BT-05b 的 <c>CREATE TABLE … ("TSTZ" TIMESTAMP WITH TIME ZONE)</c>
            // 被这台库收下【实测】—— 只核到"建表接受"那一格，往那一列写与读出来的语义没问，
            // 因为下面那句"缺的是绑定路"不会因为建得成表而少一分），
            // 缺的不是库的能力而是把偏移量带进去的那条绑定路：驱动的 CLR 登记表里没有 System.DateTimeOffset【产物】，
            // 而 §4 的 SPI 里没有任何一处做时区换算。<b>这一格与 MySQL 血统同判而不同缺</b>：
            // 那一家是"库里那一型存的不是值的偏移而是会话偏移"，这一家是"库有那一型而驱动不认那个 CLR 值"。
            // 两家的错话也不同：这里如果说"这一家没有带时区的类型"，现场就会去找 TIMESTAMP WITH TIME ZONE
            // 并以为自己找到了退路 —— 而那一步缺的在绑定侧。
            var t when t == typeof(DateTimeOffset) => throw new QuickSqlException(
                "虚谷的 DateTimeOffset 列无法绑定：库侧有 TIMESTAMP WITH TIME ZONE 这一型（厂商文档），但驱动的 CLR 类型表里"
                + "没有 System.DateTimeOffset，而 §4 的 SPI 里没有任何一处做时区换算。§9.3 给这一档写的退路"
                + "（降级 DateTime UTC + 列标记 + 读取端还原）那两半今天都不存在，所以宁缺不猜（缺口记 §14，与 MySQL 那一格"
                + "同一条判据而缺的东西不同一条）。今天要这一列：显式给 [Column(TypeName = ...)] 自己选型并自己负责换算。"),

            var t when t == typeof(DateOnly) => "DATE",

            // 这颗 (3) 与上面那颗不同档：TIME[(p)] 与 p∈[0,3] 是厂商那一页<em>明写的</em>【文档】，
            // 所以 3 既是最宽的那一档也是这一家允许的那一档 —— 本文件里唯一一处"精度声明不需要赌"的时刻型。
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "TIME(3)",

            // Guid 落 CHAR(36) 而<em>不</em>用它自己的 GUID 那一型 —— 而这一格在这一家比在另四家更该解释，
            // 因为这里两边的档位<em>都</em>有出处：厂商说 GUID 就是 16 字节的 uuid【文档】，
            // 而两家 shipped 适配器一致发 char(36)【产物】。取后者跟着的是 §9.3 那句"跨库默认 CHAR(36) 以便手工查询"：
            // 二进制的字节置换要与驱动约定一致，而 SPI 里没有"连接串参数改变方言假设"那一格（本文件已经占了一颗 CHAR_SET，
            // 不该再靠"猜驱动的置换"省四个字节）。想退回文本形态仍然够得到：[Column(TypeName = "GUID")] 在第一行短路。
            // 顺带记一处文档给的副产品：这一家有 SYS_UUID()/UUID()/GEN_RANDOM_UUID()/NEWID() 四颗【文档】
            // "库给主键"的函数，而 §4 的 SPI 里没有"这一列的值归库给 Guid"那一格（StoreFillsValue 今天只有
            // rowversion 用得上）—— 缺口记 §14，与 MySQL 那句 ON UPDATE CURRENT_TIMESTAMP 同一批。
            var t when t == typeof(Guid) => "CHAR(36)",
            var t when t == typeof(char) => "CHAR(1)",
            _ => throw new QuickSqlException($"虚谷未登记 CLR 类型 {clrType.Name} 的存储类型，请显式给 [Column(TypeName=...)]"),
        };
    }

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? "BLOB" };

        // 与 PG 系同答案（恒 Char），与 MySQL 血统同形状而<em>不同性质</em>（类注释第二条）：
        // 那边的 MaxBytes 对应一个真实存在的字节闸门（只是那道闸门在行上而不在列上），
        // 这边那颗 ×4 是一把纯宽松的尺子 —— 而"宽松"在这一家是可以证的：字符集由会话决定，
        // 所以列上<em>没有</em>一个厂商字段记着"这一列最多几字节"（§10.4 那张表里这一家落在金仓那一档）。
        // 两处不同以谁为准由 doctor 说，而它今天还没有读这一格的口（§14）。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 <see cref="LengthSemantics.Char"/>。先把可重数的分布放在这里：<c>GetLengthSemantics</c>
    /// 在五家里<b>四家恒 <c>Char</c>、一家分档</b>（T-SQL 按 <c>column.Unicode</c> 在 <c>Char</c> 与 <c>Byte</c>
    /// 之间挑，理由是 <c>varchar(n)</c> 量字节而 <c>nvarchar(n)</c> 量字符，两型的 <c>n</c> 不同义）。这一格在本族是
    /// <em>五家里第二颗"单位是字符"写在厂商文档里的</em>：厂商那一页明写 <c>CHAR/VARCHAR</c> 的长度单位是字符
    /// 【文档】（第一颗是金仓那一族，见 <c>PostgreSqlTypes</c> 同一条；MySQL 血统那一颗要靠"4.1 起才是字符"
    /// 那段历史推，Oracle 血统那一颗把答案写进了列定义而不是文档段落）。但<em>第二把尺子</em>仍然问不出来（目录里只有一个宽度，且它与精度/标度共用一颗字段，
    /// 见 <see cref="XuguSchemaReader"/>），所以 §10.4 那张表里这一家落在<b>金仓那一档</b>而不是 MySQL 那一档：
    /// <see cref="XuguSchemaReader.ProbeLengthSemanticsAsync"/> 因此是退化实现而不是探测。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;
}
