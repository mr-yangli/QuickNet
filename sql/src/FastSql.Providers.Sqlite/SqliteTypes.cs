using System.Collections.Frozen;
using FastSql.Metadata;
using FastSql.Providers;

namespace FastSql.Providers.Sqlite;

/// <summary>CLR ↔ SQLite 类型名（§9.3 那一列的第七家）。它是本仓库第一颗<b>每一格都在活库上问过</b>的
/// 类型表：另六族里 T-SQL、Oracle 血统、PG 血统、MySQL 血统四族的类型事实以厂商文档为主、虚谷以两份
/// shipped 产物加一份日志、GBase 8s 整族零实测（<c>Gbase8sTypes</c> 类注释末段），本族则是二十轮探针
/// 逐颗问出来的【实测】。但要连着读一句限定的话：<b>量的是"引擎 + 一个驱动版本"这一对</b>
/// （SQLite 3.46.1 内建 ∶ Microsoft.Data.Sqlite 9.0.0，两处都由 A0/A1/H1 三颗现场读出），而 SQLite 的
/// 现场部署可以差得很远（编译开关、是否内建 ICU、单文件还是服务）—— 所以档位是"这一对实测过"，
/// 不是"这一族都这样"。<b>还有一句读探针编号的话</b>：第 2 与第 4 两轮各自从 <c>100</c> 起编号
/// （<c>obj/sqlite-probe/probe2-report.txt</c> ∶ 同目录 <c>probe4-report.txt</c>），第 2 轮走到 <c>119</c>、
/// 第 4 轮走到 <c>156</c>，而中段重叠：<b>恰好十六颗同名不同物</b> —— <c>100</c>∶<c>106</c> 七颗、
/// <c>110</c>∶<c>115</c> 六颗、<c>117</c>∶<c>119</c> 三颗（<c>107</c>∶<c>109</c> 第 4 轮没有编，
/// <c>116</c> 那一格第 2 轮写作 <c>116a</c> 所以也不撞）。这一段撞号是探针脚本按轮重编 id 造成的，
/// 而本仓库的读法由此定下一条：<b>引到那一段必须带"第 2 轮"或"第 4 轮"</b>，不带轮次的 <c>1xx</c>
/// 一律按"第 4 轮往后的单值段"读。本文件在这一段里只引一颗 —— 第 4 轮 <c>110</c>，
/// 就是 <c>length()</c> 数字符不数字节那一句。<b>本笔另加一颗更正</b>：第 2 轮那颗 <c>111</c> 的列其实声明成 <c>TEXT</c>（它是
/// <c>RENAME COLUMN</c> 而来的，型名里没有宽度），所以它证的是"<c>TEXT</c> 装得下 20 字符"，
/// <em>并没有</em>把任何一颗数塞进 <c>VARCHAR(n)</c> 再超写 —— 宽度不强制这一判的正面读数在第 40 轮
/// （<c>VARCHAR(4)</c> 存进 20、<c>CHAR(2)</c> 存进 10、<c>NVARCHAR(6)</c> 存进 10【实测·4001 ∶ 4002 ∶ 4006】），
/// 本文件引宽度这一判的地方已全部换成那三颗。
///
/// <para><b>【本文件最重要的一条：这里的声明型名不是存储形状，是一颗 affinity 配方】</b>
/// 另六族的 <c>GetStoreType</c> 返回的字符串同时是三件事：库里的列形状、库认得的宽度、目录读回来时
/// 对得上那颗字形。SQLite 把它们拆开了：存储只有六档（<c>TEXT</c> / <c>NUMERIC</c> / <c>REAL</c> /
/// <c>INTEGER</c> / <c>BLOB</c> / 无 affinity），哪一档由<b>声明名里的子串</b>决定（含 <c>INT</c> 走
/// INTEGER、含 <c>CHAR</c>/<c>CLOB</c>/<c>TEXT</c> 走 TEXT、含 <c>BLOB</c> 或空走 BLOB、含
/// <c>REAL</c>/<c>FLOA</c>/<c>DOUB</c> 走 REAL，其余走 NUMERIC），而<b>宽度与标度从不参与</b>：
/// <c>VARCHAR(4)</c> 装得下 20 个字符【实测·4001 ∶ 4002】，<c>DECIMAL(18,4)</c> 里插一句文本会静默变成
/// <c>REAL 12.34</c>（标度那颗数被 affinity 抹平）【实测·606 ∶ 4206】。三处后果一起落在本文件：
/// ① 发出的型名要按<b>它触发的 affinity</b> 选，而不是按它看起来像什么；② §9.3 那句"默认必须映射到
/// 宽的一侧"在这里<em>失去对象</em> —— 没有"窄的一侧"，任何宽度都是注释；③ 本族因此是 §10.4 那张表
/// <b>唯一一处"两把尺都不在库里"</b>的：另六家至少问得出其中一把，这里连列上的声明值都不参与存储。
/// 反面读数也量过：声明成 <c>DATETIME</c> 的列落 NUMERIC affinity，而我们写进去的 ISO 文本因为
/// <em>不像数</em>而原样留成文本【实测·600/610/612/703】—— 也就是说这一族的行为<b>取决于值
/// 长什么样</b>，这正是下面定点那一臂不走 <c>DECIMAL(n,m)</c> 的全部理由。</para>
///
/// <para>七处分岔，逐条是本格与另六家的不同：
/// <list type="bullet">
/// <item><b>没有宽字符型，而 <c>Unicode</c> 仍然恒真</b>：结论与 PG 血统同一条，来路不同 —— 那一家是
/// "库里存的就是库编码"，这一家是"驱动写 UTF-8、而 <c>N'…'</c> 在这一族是<b>语法错</b>"【实测·P】。
/// 也就是"宽窄两型的差别在这一族压根没有对应物"这句话在这里<em>被引擎正面回答过</em>，
/// 与 <c>Gbase8sTypes</c> 取 <c>false</c> 那一家恰好相反（那一家取假是因为没有宽字符型可声明，
/// 本家取真是因为没有窄字符型可声明）。两颗取值都是第一次带实测档位。</item>
/// <item><b>布尔落 <c>INTEGER</c>，而本家不取那颗能力位的理由与另两家反号</b>：
/// MySQL 血统与虚谷那两家不取真布尔型的理由是"那一型装得下 2，于是 <c>IS TRUE</c> 与 <c>= 1</c>
/// 不同判据"；本家量到的相反：<c>typeof()</c> 对 BOOLEAN 声明的列报 <c>integer</c>【实测·607】，
/// 而 <c>= 1</c> 与 <c>IS TRUE</c> <b>两条都成立且同判</b>【实测·H0/335/607/831】。所以这里不声明
/// 那颗能力位说的不是"真布尔有坑"，是"库里没有布尔这一型，声明它只是换一颗 affinity 配方"。
/// v0.35 那条"位只跟存储型走"在本家第三次被套用，而<em>这是第一次由实测而不是文档给出理由</em>。</item>
/// <item><b>定点没有无损的落处，所以 <c>decimal</c> 落 <c>TEXT</c></b>：六族全部有原生定点型
/// （<c>DECIMAL</c>/<c>NUMBER</c>/<c>NUMERIC</c>/<c>DECIMAL_FIXED</c> 那一族），本家没有 ——
/// 声明名里的 <c>DECIMAL</c> 只是一份配方，它触发的 NUMERIC affinity 会在插入时把文本改写成
/// REAL【实测·606】，而把 <c>decimal</c> 原样交给驱动也要被重写一串【实测·63g】：第二十五轮在一颗
/// 无 affinity 的列上逐颗复点，得到的是<b>精确值而重排标度</b>
/// 【实测·2500：<c>12.3400</c> → <c>'12.34'</c>、<c>100</c> → <c>'100.0'</c>、22 位整数那一颗逐位保真
/// 只多一颗 <c>'.0'</c>】。这一批读数同时<em>推翻了本条初稿的归因</em>（原写"驱动走双精度"，
/// 而双精度会把 22 位那颗折成 <c>1.23E+21</c>）—— 内部经过哪一环没有读数，留给 §14；要紧的是
/// <b>两条路都会抹标度</b>，而 <c>12.3400</c> 与 <c>12.34</c> 在这一族是两串永不相等的文本
/// 【实测·2503】，所以"绑不变形文本"这一支保的不是精度而是<b>我们自己那一份形状</b>：标度 4 原样留在库里
/// 【实测·326/333/701 走文本，2501 复点对齐】。代价要逐颗说，因为它是这一族给 §14 添的最大一颗：
/// <b>钱这一列在库里不能比</b> —— <c>ORDER BY</c> 走词法序（那一族七行排出来是
/// <c>0.0, 1.05, 100.0000, 12.3400, 1234…12, 2.5, 9.9999</c>，也就是说<b>字典序里 "100" 挨着 "12"</b>
/// 【实测·2504】），<c>MAX</c>/<c>MIN</c> 在同一批上得到 <c>'9.9999'</c> 与 <c>'0.0'</c> 而 <c>SUM</c>
/// 一步跳成 <c>1.23E+21</c> 那颗双精度【实测·2506】，<c>WHERE "v" &gt; 10</c> 更是<em>七行全中</em>
/// 【实测·2509】—— <b>聚合、排序与过滤在这一族走的是三条互不相干的尺</b>。反面也要说，否则本条会被读成
/// "这一列什么都做不了"：算术<em>是</em>对的（<c>"v" * 2</c> 得 <c>24.68</c>【实测·2508】，
/// 数学函数直接吃文本【实测·2703】），所以缺口<em>只在比较与排序</em>、不在求值 ——
/// 那一句决定了 <c>SqliteExpressions</c> 里不给 <c>Greatest/Least</c> 包 <c>CAST</c>：
/// 包了会把两臂都变成数，而契约里那一格说的是"这一列"，不是"这一列换算之后"。
/// 另六族那句"精度由列给"在这里第一次读成"精度由我们自己的文本给"。</item>
/// <item><b><c>ulong</c> 也落 <c>TEXT</c>，而它的原因不是定点那一颗</b>：驱动把 64 位无符号往
/// <c>long</c> 里塞，<c>ulong.MaxValue</c> 现场读回 <c>-1</c>【实测·505】；绑成不变文本之后
/// <c>18446744073709551615</c> 原样可回【实测·702】。另六族的 <c>ulong</c> 落 <c>NUMERIC(20,0)</c>
/// 或 <c>DECIMAL(20,0)</c>，那一族方案在本家<em>会被 affinity 改写成 REAL</em>，所以这里不能照抄。</item>
/// <item><b>时刻四型一律 <c>TEXT</c>，而这是本仓库第一次"带偏移的时刻存得下"</b>：
/// <c>DateTimeOffset</c> 在 MySQL 血统那一族量到的是"列里存的是会话偏移而不是值自己的偏移"，虚谷是
/// "驱动不认"，GBase 8s 是"库里压根没有第三型"（三条缺法三条来路，逐字在 <c>Gbase8sTypes</c> 第五条
/// 旁边）。本家量到的是<b>它自己的偏移原样进原样出</b>【实测·Y/509/604】，所以这一家不抛、不降级、
/// 不拆列。<c>DateTime</c> 那一臂同落 <c>TEXT</c> 是双重理由：驱动写的 ISO 文本带<b>可变宽</b>的小数位
/// 且保住 100 纳秒刻度【实测·G2/318/319/907/908】，而把它声明成 <c>DATETIME</c> 只是换一颗会改写值的
/// affinity。这颗"可变宽"顺带<em>推翻了我原本要写的一条设计，而推翻它的是第 44 轮、不是初稿引的那两颗</em>：
/// 本来准备按定宽文本绑定来防"文本序不等于时刻序"，第 12 轮那两颗读数（912/913）按标签看是相反方向、
/// 脚本又没留在档里，所以这一格重新问过一遍。驱动写回的确实全是可变宽（同一批十二行里出现
/// <c>…30</c> ∶ <c>…30.05</c> ∶ <c>…30.005</c> ∶ <c>…30.4567891</c> 四种宽度，最长 27 字符最短 19
/// 【实测·4402】），而<b>小数位的可变宽在词法序上不会翻</b>：八颗手工挑出的最坏形状按文本排出来正是时刻序
/// 【实测·4403】，整列按文本排与按 <c>julianday</c> 排交出同一串、那一串对着 4402 的宽度表逐颗读回也正是
/// 时刻序【实测·4405】，<c>WHERE col &gt; @bound</c> 在三颗小数位枢轴上各命中 CLR 那一判【实测·4406 三臂】。
/// <b>反倒是"把宽度钉死"那一侧会坏</b>：<c>STRFTIME('%f')</c> 只到毫秒而且是<em>四舍五入</em>上去的
/// （<c>.4567891</c> → <c>.457</c>，且它让十二行里九行不再等于库里那一颗）【实测·4407】。
/// 但那条危险<em>不是不存在，是有两处边界</em>，两处都得写死：整数位宽度不齐（<c>2026-3-9</c>、
/// <c>1:20:30</c>）会翻词法序而 <c>julianday</c> 那颗串根本解不出、交回 NULL【实测·4408 前两行】；
/// 同一列里混了<b>带偏移与不带偏移</b>两形时词法序同样会翻 —— <c>…4567891</c> 排在
/// <c>…4567891+08:00</c> 之前，而那颗偏移折回 UTC 其实<em>更早</em>【实测·4408 第三行】，
/// 这一处正对着本条目第一句那颗 <c>DateTimeOffset</c> 的存储形，所以 §14 记的是"<em>同一列混两形时
/// 文本序不可信</em>"，不是"本家的时刻序是错的"。本文件因此仍然不留定宽那个机制；
/// 这一格整颗记 §13.5（本版被实测改掉的设计判断，连同一处引错的读数）。</item>
/// <item><b><c>TimeSpan</c> 与 <c>TimeOnly</c> 分道，而分道是驱动逼的</b>：同一颗 <c>TimeOnly</c>
/// 走驱动的默认路得到的是文本 <c>13:45:00</c>【实测·507/508】，而 <c>TimeOnly</c> 在这一格要的是
/// "与 <c>TimeSpan</c> 同形可比较"，所以两型一起落 <c>INTEGER</c>、绑定侧写 <c>Ticks</c>
/// 【实测·605/700 TS】。<b>这是七家里第一颗把"跨度"存成整数而"时刻"存成文本的家</b>，
/// 两臂不同形不是疏忽：时刻要能被 <c>strftime</c> 问（<see cref="SqliteExpressions"/>），
/// 跨度要能被减（<c>INTEGER</c> 上做算术是这一族最稳的一档）。</item>
/// <item><b>浮点只有一档，而升宽是默认方向</b>：<c>float</c> 与 <c>double</c> 都落 <c>REAL</c>，
/// 它是 8 字节 IEEE【文档·SQLite 数据类型第 3 节 —— <b>本家量不到这一颗</b>：想问"存了几字节"的第一条路
/// 是 <c>length(CAST(x AS BLOB))</c>，而它交回的是<em>十进制文本的字节数</em>（<c>3.5</c> 交回 <c>3</c>、
/// <c>1e300</c> 交回 <c>8</c>、<c>-0.5</c> 交回 <c>4</c>，两颗同为 REAL 的列在那一格逐字相等【实测·4207 ∶ 4209 ∶ 4210】，
/// 也就是说这一族的 BLOB 转换是<em>文本化</em>而不是 IEEE 图像），所以那颗数在这一族没有可问的入口】，
/// 所以 CLR <c>float</c> 进去无损、出来是 <c>double</c> 而由
/// 具体化侧窄化。与 <c>Gbase8sTypes</c> 那条"照抄型名会得到<em>错</em>的宽度"正好相反：本家
/// <em>没有</em>可以照抄的窄浮点 —— 声明 <c>FLOAT</c> 与声明 <c>DOUBLE</c> 触发同一颗 affinity，
/// 差别只在目录里回显的那串字（【实测·22/81/616】目录原样回显声明字形）。</item>
/// </list></para>
///
/// <para>自增仍然不在本文件：与另六族同规（型名归这里、身份属性归 <c>*CodeFirst</c>），而这一家多一层
/// 【实测】：<c>AUTOINCREMENT</c> 必须挂在 <c>INTEGER PRIMARY KEY</c> 那一颗内联主键上，表级
/// <c>PRIMARY KEY</c> 子句<em>接不住它</em>【实测·300/301/303/305】，而且 <c>INTEGER</c> 那颗宽是硬条件
/// （换成 <c>BIGINT</c> 直接被拒【实测·306】）。所以 <c>IsIdentity</c> 那一格在本家换的不是后缀而是
/// <b>整条键的写法</b>，那一句在 <see cref="SqliteCodeFirst"/>。</para></summary>
public sealed class SqliteTypes : ITypeMap
{
    public static SqliteTypes Instance { get; } = new();

    /// <summary>读侧的型名表。<b>它登记的键有两族来源</b>：我们自己发出去的那几颗（下面 <c>GetStoreType</c>
    /// 的每一支），以及"别人在这台库上建过的列会回显的字形"—— 后者在这一家格外重要，因为 affinity
    /// 让声明名与实际存储解耦，一张老表可能写着任何一形（<c>TINYINT</c>、<c>MEDIUMINT</c>、
    /// <c>DATETIMEV2</c> 那一族都在真实工程里流通）。两族分开读，值相同而判据不同。
    /// <para><b>整数一族全映射到 <see cref="long"/>，这是本表与另六族分岔最大的一格</b>：另六家按声明名
    /// 分 <c>short</c>/<c>int</c>/<c>long</c> 三档，本家的存储只有一颗 64 位整数【实测·200 那颗
    /// <c>Int64</c> 读数】，所以声明名窄而存储宽是这一族的常态而不是异常。这里仍按声明名给窄形，
    /// 理由是<b>对账</b>：<c>doctor</c> 比的是"契约写的型与库声明的型对不对得上"，把 <c>smallint</c>
    /// 折成 <c>long</c> 会让一列声明 <c>SMALLINT</c> 而契约是 <c>short</c> 的表看不出问题。
    /// <c>boolean</c>/<c>bool</c>/<c>bit</c> 三行都认，而 <c>GetStoreType</c> 只发 <c>INTEGER</c>
    /// （类注释第二条）：认它是读别人的表，不发它是自己的表。
    /// <b>本表今天在整个仓库里<em>零调用</em></b>（<see cref="ITypeMap.TryGetClrType"/> 在 src 与 tests
    /// 双侧都没有 caller，v0.37 本笔量的），所以这张表是"给 <c>doctor</c> 预备的读侧知识"而不是
    /// 现状；它在这里逐颗标出档位，是因为七族同规（另六家那五张表同样零调用而同样在）。</para></summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        // 字符与文本：本家自己发的三形（TEXT / VARCHAR / CHAR）加上大对象那一形。
        ["text"] = typeof(string),
        ["clob"] = typeof(string),
        ["character"] = typeof(string),
        ["char"] = typeof(string),
        ["varchar"] = typeof(string),
        ["nvarchar"] = typeof(string),
        ["string"] = typeof(string),

        // 整数一族（含各种窄声明）。
        ["integer"] = typeof(int),
        ["int"] = typeof(int),
        ["tinyint"] = typeof(sbyte),
        ["smallint"] = typeof(short),
        ["mediumint"] = typeof(int),
        ["bigint"] = typeof(long),
        ["int64"] = typeof(long),
        ["unsigned big int"] = typeof(long),

        // 布尔三形，见类注释里"认而不发"那一句。
        ["boolean"] = typeof(bool),
        ["bool"] = typeof(bool),
        ["bit"] = typeof(bool),

        // 浮点一档，定点那一族的声明名也在这里（它们在库里其实是 affinity 配方）。
        ["real"] = typeof(double),
        ["double"] = typeof(double),
        ["double precision"] = typeof(double),
        ["float"] = typeof(double),
        ["numeric"] = typeof(decimal),
        ["decimal"] = typeof(decimal),

        ["blob"] = typeof(byte[]),

        // 时刻四形。注意它们与 <see cref="GetStoreType"/> 的对应是<em>错位</em>的：本家发的是
        // <c>TEXT</c> 与 <c>INTEGER</c>（类注释第五、第六条），这三行认的是别人建的表。
        // 所以这一族读回的 DateTime 列在库里其实是什么形状，本表说不出 —— 那是 affinity 那一格的后果。
        ["date"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["datetimev2"] = typeof(DateTimeOffset),
        ["time"] = typeof(TimeOnly),
        ["timestamp"] = typeof(DateTime),

        // 两颗方言侧的惯用名：json 在这一族的生态里是文本加一道 CHECK【文档】，而本仓库
        // 没有任何一格问"这一列是不是 json"，所以认成 string 与 <c>PostgreSqlTypes</c> 同判。
        ["json"] = typeof(string),
        ["jsonb"] = typeof(string),
        ["uuid"] = typeof(Guid),
        ["guid"] = typeof(Guid),
    }.ToFrozenDictionary();

    /// <summary>UTF-8 下一颗字符最多 4 字节（代理对）。<b>这一颗在本家是纯尺子，不进任何文本</b>：
    /// 与 <c>Gbase8sTypes.WorstCaseBytesPerChar</c> 那颗同名同值的常量分别是两件事 —— 那一家它乘出来
    /// 就是 DDL 里的 <c>n</c>，本家它只喂给 <see cref="ColumnContract.FitsBytes"/>，因为这一族的宽度
    /// 从不参与存储【实测·4001 ∶ 4002】。取 4 与 PG 血统那一家同判：库里没有字节上限那一扇闸门，
    /// 所以要一把<b>永不误拒</b>的宽松尺子，而不是一把看起来精确的。</summary>
    const int Utf8WorstCaseBytesPerChar = 4;

    /// <summary>CLR 型 → 声明字形；人已点名的那一格原样 echo（§4 那句"provider 不翻译人写的话"，七家同规）。
    /// <b>本族这一格多一句诚实的话</b>：echo 出去的那串字在这一家<em>不改变存储</em>，只改变 affinity
    /// 配方与被点名时的那次比对 —— 也就是说 <c>[Column(DbType="DECIMAL(18,4)")]</c> 在这里能得到
    /// "目录回显这一串"而得不到"定点存储"，这是七家里只有本家成立的一格（缺口与 §10.4 那一段并排）。
    /// 与 <c>IsIdentity</c> 无关，理由与六家同一条（共用层先定型后判自增，<c>Gbase8sTypes</c> 第三条
    /// 记的那次改判在本家逐字成立），而本家的自增换的是键的写法，见 <see cref="SqliteCodeFirst"/>。</summary>
    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return String(column);

        if (clrType == typeof(byte[]))
            return "BLOB";

        // 枚举存整数，七族同一决定。这一家多一层：整数是这一族最"实"的一档（INTEGER affinity，
        // 存储就是 64 位整数），所以 CLR 枚举底层是 long 也接得住 —— 另六家里 MySQL 与 GBase 8s
        // 那两家的 INT 那一臂窄到 32 位，本家没有那一格。
        if (clrType.IsEnum)
            return "INTEGER";

        return clrType switch
        {
            // 类注释第二条：库里没有布尔这一型，声明 BOOLEAN 只是换一串回显的字。
            var t when t == typeof(bool) => "INTEGER",

            var t when t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) || t == typeof(int) => "INTEGER",
            var t when t == typeof(uint) || t == typeof(long) => "BIGINT",

            // 无符号 64 位落文本而不是 NUMERIC(20,0)：驱动侧会绕成负数，而 NUMERIC 那一形触发
            // 的 affinity 会把大整数改写成 REAL（类注释第四条，两处实测各指一颗）。
            var t when t == typeof(ulong) => "TEXT",

            // 只有一档浮点，所以 CLR float 与 double 同落 REAL：float 进去无损（REAL 是 8 字节），
            // 出来是 double 而由具体化侧窄化 —— 与 <c>Gbase8sTypes</c> 那条"照抄型名会拿到错的宽度"
            // 相反，本家压根没有窄浮点可照抄（类注释第七条）。
            var t when t == typeof(float) || t == typeof(double) => "REAL",

            // 定点落文本：这一族没有无损的定点落处（类注释第三条），而 <c>Precision</c>/<c>Scale</c>
            // 两颗在这里<em>不参与文本</em> —— 它们留在契约里，被绑定侧的"按标度补零"用掉一次。
            // 于是本家是七家里第一颗"精度与标度是纯应用侧事实"的家：另六家的 DECIMAL(n,m) 是库的话，
            // 这里它是我们的话。
            var t when t == typeof(decimal) => "TEXT",

            // 时刻三型 + 带偏移那一型一律 TEXT：驱动自己写的就是 ISO 文本，而可变宽的小数位
            // 实测保住 100 纳秒（类注释第五条）。声明成 DATETIME 不会换存储，只会换一整套行为
            // —— 那一条正是"本文件按 affinity 选型名"这句话的来由。
            var t when t == typeof(DateTime) => "TEXT",
            var t when t == typeof(DateTimeOffset) => "TEXT",
            var t when t == typeof(DateOnly) => "TEXT",

            // TimeOnly 在这里是 INTEGER 而时刻那一族是 TEXT：本文件唯一一处"两个 CLR 时刻型故意不同形"，
            // 理由是绑定侧两型都走 Ticks（类注释第六条），而在 INTEGER 上"比大小"与"加减"都不问库。
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "INTEGER",

            // Guid 落 TEXT 而<em>不</em>落 CHAR(36)：与达梦、MySQL 血统、GBase 8s 三家的答案同宽
            // （36 颗字符，实测 V/336/337/608），而这一家的 CHAR(n) 那一形不会带来任何检查
            // （宽度从不强制，实测 4001 ∶ 4002），所以写 36 只会得到一句看起来像约束的注释。
            // 于是取舍变了形：那三家是"文本那一档更宽所以取定长"，本家是"两档一模一样宽，
            // 取不冒充约束的那一形"。
            var t when t == typeof(Guid) => "TEXT",
            var t when t == typeof(char) => "TEXT",

            // 本家的兜底话术与另六家不同一条：这里未登记的型<em>多半也能建</em>（affinity 认任何字形），
            // 所以报的不是"这一族没有那一型"而是"本仓库不知道往哪一臂绑值"—— 绑定侧不认识
            // 的 CLR 型即使建成 TEXT 也会在发射时炸，那句要写在拒绝点而不是写在库里。
            _ => throw new FastSqlException($"SQLite 未登记 CLR 类型 {clrType.Name} 的存储类型：这一族没有"
                + "「库里不存在这一型」那种缺（声明任何字形都会被接受），所以缺的是本仓库的绑定臂。"
                + "请显式给 [Column(DbType=...)]，并注意声明名在这家决定的是 affinity 而不是存储。"),
        };
    }

    /// <summary>string → 有宽度声明时给 <c>VARCHAR(n)</c>，无宽度时给 <c>TEXT</c>。
    /// <b>n 在这一家是注释而不是约束</b>（实测 4001 ∶ 4002），所以本方法发出它的唯一理由是<b>对账可读</b>：
    /// 目录原样回显声明字形（实测 22/81/616），<c>doctor</c> 因此能把"契约说 200 字符"与
    /// "库声明 200"对上 —— 另六家这一格对的是库的容量，本家对的是库的自述。这一条也解释了为什么不
    /// 统一发 <c>TEXT</c>：那会让每一颗带宽度的列在 <c>doctor</c> 里变成一条真差。
    /// <b>它同时是一颗新的风险</b>：这一族的 <c>VARCHAR(4)</c> 装得下 20 个字符，也就是说
    /// 建侧的宽度不会替你挡超长值 —— 挡它的是共用层 <see cref="ColumnContract.FitsChars"/> 那道闸，
    /// 而那道闸只在<em>本仓库负责写入</em>的路径上生效；换一条手写 SQL 就没有闸门了。
    /// 这一格记 §14，重开条件是"哪一层要把'库不强制宽度'变成一条可见的警告"。</summary>
    static string String(ColumnContract column)
        => column.MaxChars is { } chars ? $"VARCHAR({chars})" : "TEXT";

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? "BLOB" };

        // 逐格（这一支是本文件与另六家读法差异最大的一处，因为两颗位在这里都不改变库的行为）：
        //   Unicode = true —— 类注释第一条：驱动写 UTF-8、而 N'' 是语法错。取真是 PG 血统同结论、
        //     来路不同；与 GBase 8s 那颗假值相反一侧，而那一句在本家是有实测撑着的。
        //   LengthSemantics = Char —— 实测 length() 数的是字符而不是字节（第 4 轮 110），所以这一家落在
        //     §10.4 那张表"恒 Char"那一档，与 PG 血统同档而不同来路：那一家恒 Char 是 codeset 的
        //     结论，本家它是"库里压根没有字节尺"的结论（宽度不强制，4001 ∶ 4002）。
        //   MaxBytes = 字符数 × 4 —— 一把宽松尺子（<see cref="Utf8WorstCaseBytesPerChar"/> 那一段），
        //     不是库里存在的约束。它不参与 DDL 文本，这一点与 GBase 8s 那一家分岔最大。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * Utf8WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) => StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 <see cref="LengthSemantics.Char"/>，与 PG 血统那两家同值。差别在<b>这一家连"第二个数"
    /// 都不存在</b>：那两家的 <c>n</c> 是字符而库里另有字节可读（<c>octet_length</c> 一类），本家
    /// <c>n</c> 是字符而<b>它从不被强制</b>（实测 4001 ∶ 4002），所以 §10.4 那张表里"问不出第二把尺"那一档
    /// 在本家读的其实是"两把尺都不在库里"。取 Char 因此不是"问到了字符那一把"，而是"契约里那一格
    /// 说的只能是字符" —— 与本家的量纲取向上唯一诚实的一档。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;
}
