using System.Collections.Frozen;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的函数表（§4 的 <c>IExpressionRules</c>，第七家）。<b>这一张表是七家里第一颗四十五键全登记的，
/// 也是第一颗每一行都有【实测】的</b>：另四家登记同样的四十五颗而出处是文档或产物，<c>Gbase8s</c> 只登记三十五颗
/// 而出处也是文档 —— 本家这一份的出处是<em>同一台机器上跑过的那二十七轮探针</em>（第 1 到 20 轮在
/// <c>obj/sqlite-probe/</c>、第 21 到 27 轮在 <c>obj/sqlite-probe2/</c>；本文件里指的 <c>2200</c>…<c>2710</c>
/// 是后一批，逐颗对到点）。"全登记"不是能力最强，是<em>问得起</em>：
/// 每一颗都能对着一份报告核，而这一格在另六家做不到。
///
/// <para><b>本文件有一条形状纪律，它比任何一颗单键都重要</b>：所有产出<em>时刻</em>的模板（<see cref="QuickFn.Now"/>、
/// <see cref="QuickFn.UtcNow"/>、六颗 <c>Add*</c>、<see cref="QuickFn.ToDate"/>）都套同一层 <see cref="Stored"/>，
/// 因为这一家的时刻是文本，而<em>等号比的是字面串</em>。不套的话引擎交回的是定长三位小数
/// （<c>2026-03-09 10:20:30.000</c>），而库里那一格由驱动写成"去尾零、整秒连小数点都不留"
/// （<c>2026-03-09 10:20:30</c>【实测·2400 的 <c>id 1…6</c> 那六行】），两形相遇<em>恒不等</em>：
/// <list type="bullet">
/// <item>【实测·2403 对 2404】同一批真行上"加零天再等回自己"，套了归一的命中 <c>id=4</c>/<c>id=5</c> 两行，
/// 没套的<b>三行全不命中</b>。</item>
/// <item>【实测·2405 对 2406】归一形与一颗内联字面量相遇数到 <c>1</c>，未归一那一形数到 <c>0</c> ——
/// 同一句 <c>WHERE</c> 的两种写法，一个返回行一个返回空集，而两条都不报错。</item>
/// </list>
/// <b>归一救不了的只剩一格，而那一格是硬的</b>：<c>%f</c> 只到毫秒【实测·2214 的 <c>.4567</c> → <c>.457</c>】，
/// 所以库里那颗 <c>2026-03-09 10:20:30.0000001</c> 经过任何一次日期算术都会退成 <c>.000</c> 再被归一成整秒
/// 【实测·2402/2403 的 <c>id=6</c> 那一行就是这一格】。列是 100 纳秒而算式只到毫秒，那是 <c>§14</c> 的一行，
/// 不是一句"以后优化"。</para>
///
/// <para><b>第二格本家特有：<c>printf</c> 的 <c>%+d</c> 与 <c>%+f</c> 二选一，判据是 CLR 那一侧截不截断</b>。
/// 六颗 <c>Add*</c> 在 CLR 是两种元数：<c>AddYears/AddMonths</c> 收 <c>double</c> 而<em>只用整数部分</em>，
/// <c>AddDays/AddHours/AddMinutes/AddSeconds</c> 收 <c>double</c> 而<em>全用</em>。于是：
/// <list type="bullet">
/// <item>年与月发 <c>%+d</c>：【实测·4219】<c>printf('%+d day', 1.5)</c> 交回 <c>'+1 day'</c>（向零截断），
/// 与 CLR 的"只用整数部分"同侧；【实测·2702】绑定参数那一臂同形（<c>1.5</c> → <c>'+1 years'</c> →
/// <c>2027-03-09</c>）。若发 <c>%+f</c> 会得到"一个半月"：【实测·2409】同一颗 <c>1.5</c> 交回
/// <c>2026-04-24</c> 而 CLR 交回 <c>2026-04-09</c> —— <b>差十九天而不报错</b>。</item>
/// <item>日与时/分/秒发 <c>%+f</c>：【实测·2410】<c>1.5</c> 天 → <c>+36 小时</c> 正确，而 <c>%+d</c> 会把它
/// 截成一天（同一条读数的第二臂）。【实测·2215】秒那一档 <c>1.5</c> 也在。</item>
/// </list>
/// 一颗 <c>printf</c> 在同一张表里两种用法，这不是笔误而是 CLR 契约的分岔；改的人要先答"另一边截不截断"。</para>
///
/// <para><b>第三格是这一家独有的错法：月与年的算术<em>不进位到"当月最后一天"，而是溢出到下一个月</em></b>。
/// 【实测·2138】<c>'2026-01-31' +1 month</c> 交回 <c>2026-03-03</c>（引擎先算出"二月三十一日"再顺着天数往前走），
/// 而 CLR 的 <c>AddMonths(1)</c> 交回 <c>2026-02-28</c>；【实测·2707】三行同一判（<c>01-31</c> → <c>03-03</c>、
/// <c>01-30</c> → <c>03-02</c>、闰年 <c>2024-01-31</c> → <c>03-02</c>），【实测·2705】打在真列上也一样。
/// <b>七家里另外六家在这一点上全是对的方向</b>（<c>DATEADD</c>、<c>ADD_MONTHS</c>、<c>INTERVAL</c>、
/// <c>NUMTODSINTERVAL</c> 各按各的册子都会夹到月末），本家是第七种而它是<em>唯一一种给出一张不存在的日历</em>。
/// 为什么不修：<b>这一格第 43 轮把它量到底了，而量到的结论是"能修的写法确实存在，代价比原本记的还大"</b>。
/// 那一棵的形状是 <c>CASE WHEN</c> 判"引擎滚过月没有"（拿结果行的号数与源行的号数比大小），滚过就发
/// <c>start of month / +2 month / -1 day</c> 三颗修饰符<em>夹到目标月月末</em>，未滚过就发裸 <c>+n month</c>，
/// 外面再套一层 <c>STRFTIME</c> 把时分秒接回去。四条输入逐颗对过 CLR 的 <c>AddMonths</c>：
/// <c>01-31</c> 与 <c>01-30</c> 各交回 <c>02-28</c>、闰年 <c>2024-01-31 +12</c> 交回 <c>2025-01-31</c>、
/// <c>2024-02-29 +12</c> 交回 <c>2025-02-28</c>【实测·4308 四臂】，打在真列上同一判【实测·4333】。
/// <b>三颗代价，颗颗有读数</b>：其一，<c>start of month</c> 会把时钟抹到零点（连小数位一起），
/// 本来的【推断】现在是【实测·4301 ∶ 4302】，而裸 <c>+n month</c> 不抹【实测·4303】——所以两条时刻分支
/// 必须各自重接时分秒，那一棵 <c>CASE</c> 不是加在模板尾巴上而是包住整颗模板；其二，重接只能走
/// <c>STRFTIME('%Y-%m-%d %H:%M:%f', …)</c>，因为 <c>DATETIME()</c> 的输出<em>永远</em>不带小数秒
/// 【实测·4330 ∶ 4331】，而 <c>%f</c> 只到毫秒且定宽三位【实测·4334：<c>.7</c> → <c>.700</c>、
/// 缺省 → <c>.000</c>、七位小数 → 折成三位】——也就是说<em>这一棵修正本身会把本层写进去的 100 纳秒刻度
/// 主动降级成毫秒</em>，修一颗语义错换来一颗精度错；其三，这棵写法极易写错，<b>本仓库的探针第一版就错在
/// 那颗月末上</b>：<c>start of month / +1 month / -1 day</c> 量的是<em>起始月</em>的月末而不是目标月的
/// （<c>2026-01-31</c> 走那一串交回 <c>2026-01-31</c>），要拿目标月的月末得写 <c>+2 month</c>
/// 【实测·4304 ∶ 4309】。取向不变：<b>发短的那一形</b>，把这一格放在这里、放在 §14、放在 <c>docs</c>
/// 那句"本家日期语义有出入"里 —— 只是从今天起这一句后面站着第 43 轮的一整排读数，而不是一棵没写出来的 <c>CASE</c>。
/// <b>同一轮顺带钉住的是本表既有的一条形状选择</b>：那九处套 <c>STRFTIME('%Y-%m-%d %H:%M:%f', …)</c> 的行
/// （六颗加减、两颗取时钟、一颗 <see cref="QuickFn.ToDate"/>）今天不再是审美而是精度——换成 <c>DATETIME(…)</c>
/// 就交不回小数秒【实测·4330 那一行的第一格与第四格：同一个入参，<c>DATETIME()</c> 交回 <c>12:34:56</c>
/// 而 <c>STRFTIME(…%f)</c> 交回 <c>12:34:56.789</c>】——改成 <c>DATETIME</c> 不需要新证据就能把
/// <see cref="SqliteTypes"/> 第三条的"保住 100 纳秒"那句废掉。</para>
///
/// <para><b>第四格：文本做载体付的两颗税</b>。<see cref="SqliteTypes"/> 第三条把 <c>decimal</c> 落成 <c>TEXT</c>
/// 是为了无损，而无损的代价是<em>比较与排序退回词法</em>：
/// 【实测·2504】七颗定点值按列排出来的序是 <c>0.0, 1.05, 100.0000, 12.3400, …</c>；
/// 【实测·2506】<c>MAX</c> 交回 <c>'9.9999'</c> 而场上最大的是 <c>1.2e21</c>；
/// 【实测·2608】两颗定点数过 <c>MAX()</c> 同一判；
/// 【实测·2710】更硬：<c>MAX("m", 100)</c> 交回那颗文本 <c>'12.34'</c> —— 在这一家的类型序里
/// <b>文本永远大于任何数</b>，所以"一列定点 vs 一颗字面数"连截断的机会都没有。
/// 与 <see cref="QuickFn.Greatest"/>/<see cref="QuickFn.Least"/> 同一格的还有 <c>Coalesce</c> 之外的两颗聚合
/// （<c>Min/Max</c>），而<b>那两颗不能不登记</b>。所以本表照登，代价写在两处：§14 那一行与
/// <c>SqliteTypes</c> 第三条旁边。<em>另一颗是好消息</em>：算数函数会自己把文本折成数
/// 【实测·2703】<c>CEIL('12.34') = 13</c>、<c>POWER('12.34', 2)</c>，而同一颗 <c>ROUND</c> 打在
/// <c>m = '-0.5'</c> 那一行交回 <c>-1</c>【实测·2703 第二行 ∶ 4217 的字面量臂】——一半<em>向外</em>舍。
/// 这一档与 CLR 的 <c>Math.Round</c>（默认向偶数舍）相反，而<b>它不是本家特有的</b>：七家发的都是
/// SQL 那颗 <c>ROUND</c>，所以它不进 §14，只在这里点名一次，免得读到这一行的人以为换一家就一致。
/// 所以 <c>Sum/Average/Round/Power/Sqrt/Ceiling/Floor/Abs</c> 这八颗<em>不需要</em>金仓那一形
/// （<c>CEIL(CAST(x AS numeric))</c>）的括号 —— 那一格本家是七家里最省的。
/// 而折得<em>不一致</em>：<c>CEIL('abc')</c> 交回 <c>NULL</c>，<c>ABS('abc')</c> 交回 <c>0</c>
/// 【实测·2704】，同一批函数对"折不动"那一档没有统一答案。</para>
///
/// <para><b>第五格只有一句话，但它值整份文件</b>：<see cref="QuickFn.Blank"/> 在本家<em>登得了</em>
/// （<c>({0} IS NULL OR TRIM({0}) = '')</c>，同一颗参数在文本里出现两次），因为本家参数是具名形
/// <c>@p0</c>【实测·4220 两臂 ∶ 4221 三处】。<c>Gbase8sExpressions</c> 为同一条形丢了这颗键
/// （位置形 <c>?</c> 下"文本两颗、表里一颗"会把值绑到隔壁列），那一格的分岔在这里落回"能登"。
/// <b>两颗 <c>ParameterPrefix</c> 相反的邻居，在同一张函数表上各缺一半</b>，而缺的原因不是库而是形状。
/// 顺带一颗：<c>TRIM</c> 在这一家只去空格【实测·2203 的 <c>char(9)</c> 那一臂】，所以
/// <c>IsNullOrWhiteSpace("\t")</c> 在本家不成立 —— 那一条<b>六家同然</b>（它们的模板就是这一形），
/// 本家只是第一家把它量出来的。</para>
///
/// <para><b>不登记的键：零颗</b>，与类注释第一句同一事。要逐颗记的那一类"登了而读的人会上当"的三颗：
/// <list type="bullet">
/// <item><see cref="QuickFn.ToDate"/> 交回的是<em>零点那一形</em>（<c>2026-03-09 00:00:00</c>）而不是裸日期
/// （<c>2026-03-09</c>），两条都有实测：<b>【实测·2600/2604】裸形命中三行而遇不上 <c>DateTime</c> 零点的绑定
/// 参数（2605 数到 0）；【实测·2602/2603】零点形命中同一批三行</b>。取零点形是为了与另六家在这一点上同语义
/// —— 虚谷那一颗 <c>CAST(x AS date)</c> 在真列上交回的就是 <c>2026-05-06 00:00:00.0000000</c>
/// （<c>XuguExpressions</c> 里那条 <c>E-08a</c>），也就是说<em>七家的翻译契约里这一格是"截到日而仍是时刻"</em>。
/// 代价写在原处：与一颗 <c>DateOnly</c> 参数相遇时本形<em>不等</em>（差一段 <c>' 00:00:00'</c>），
/// 那一档要的是裸 <c>DATE({0})</c>。两臂都量了，选一臂就得承认另一臂。</item>
/// <item><see cref="QuickFn.DateSecond"/> 截到整秒【实测·2515：<c>.456</c> 那一行的秒是 <c>30</c> 不是 <c>30.456</c>】，
/// 与虚谷那一颗 <c>GETSECOND</c> 交回 <c>9.456</c> 的错法相反（那一家的修法在同一条键上）。
/// <see cref="QuickFn.DateYear"/> 那一族打在带偏移的文本上读的是 <b>UTC</b> 那一份字段
/// 【实测·2412：<c>10:20:30.456+08:00</c> 过 <c>STRFTIME('%H')</c> 得 <c>02</c>】，
/// 而打在无偏移文本上就是本地那一格 —— 同一颗键两种语义，取决于<em>库里那一串长什么样</em>。</item>
/// <item><see cref="QuickFn.Now"/> 与 <see cref="QuickFn.UtcNow"/> 在这一家<em>不是"库那台机器的钟"</em>：
/// 引擎在进程内（<c>e_sqlite3.dll</c> 与我们同址），所以这一格与 CLR 的 <c>DateTime.Now</c> 之间不再有
/// 六家都有的那颗网络/时区差。这是七家里的第一次，而它带来的不是省事是一格新的判据：<b>本家 <c>Now</c>
/// 的唯一损失就是那颗 <c>%f</c> 的毫秒档</b>【实测·2211/2237】，所以 <c>Where(x =&gt; x.At &lt; Now())</c>
/// 在这一家比的不是"服务器时间"而是<em>本机时间截到毫秒</em>，写在注释里是因为有人会以为换库能换掉它。</item>
/// </list></para></summary>
public sealed class SqliteExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    /// <summary><c>||</c>【实测·2200】。与 <c>Fn.Concat/2</c> 那颗模板是同一条事实的两个出口，
    /// 而它在 <c>src/</c> 里<b>零消费者</b>（七家同一格，那一句判据在 <c>MySqlDialectTests</c> 里钉着），
    /// 所以这里填的不是行为而是台账。NULL 传播与另六家同形【实测·2200 第二臂：<c>'a' || NULL</c> → NULL】。
    /// <b>连着读一句以免两行被读成互相打脸</b>：这一位说的是"裸算子<em>自己</em>的行为"，
    /// 而共用层发出去的是 <c>Fn.Concat/2</c> 那一颗模板（<see cref="Build"/> 里带着 <c>COALESCE</c> 折叠，
    /// 理由与读数在那一条注释里），所以算子的这一档<em>不是</em>本仓库的拼接产出形状。</summary>
    public string ConcatOperator => "||";

    /// <summary>反斜杠。<b>本家没有"默认转义符"这一颗东西</b>【实测·2119：<c>'a_b' LIKE 'a_b'</c> 命中，
    /// 即裸下划线当通配用而没有任何字符被预留】，共用层每次都显式发 <c>ESCAPE</c> 子句
    /// （<c>DialectBase</c> 那一颗），于是这一位的真语义与另六家一样是"允许哪一个字符做转义符"
    /// 【实测·2120 反斜杠两臂、2121 换 <c>#</c> 也认、2225 是绑定参数那一臂】。
    /// 这一格在<em>没有</em> ESCAPE 子句的手写文本上是另一回事，而本仓库不发那种文本。</summary>
    public char LikeEscapeCharacter => '\\';

    /// <summary>false，而且这一位在本家是<em>问出来的</em>而不是推出来的【实测·2700/2701】：
    /// <c>REGEXP</c> 这一族默认<em>根本没有</em>那颗函数（操作符与函数两形都报 <c>no such function: REGEXP</c>），
    /// 它要宿主进程自己注册（Microsoft.Data.Sqlite 没有注册）。模式匹配这一格场上有的是 <c>GLOB</c>
    /// 【实测·2123】与 <c>LIKE</c>【实测·2119 起】，而 <c>GLOB</c> 的是 <c>*</c>/<c>?</c>/<c>[ ]</c> 那一族
    /// 通配、不是正则。<b>把 <c>SupportsRegex</c> 报真是让共用层发一颗这一家没有的算子</b>，
    /// 而那一条错在七家里只有本家能当场问出来。</summary>
    public bool SupportsRegex => false;

    public bool TryGetFunction(string key, out FunctionMapping mapping) => _map.TryGetValue(key, out mapping!);

    public void Register(string key, FunctionMapping mapping)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, FunctionMapping>(_map, StringComparer.Ordinal);

            next[key] = mapping;
            _map = next.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    /// <summary>键的取值集见类注释第一段（四十五颗全在）。<b>本家不需要 <c>DialectBase.EmitFunction</c>
    /// 那道位置形核对</b>（参数前缀是具名的 <c>@</c>，那颗核对在本家恒不触发），而模板里出现两次
    /// <c>{0}</c> 的那一颗（<c>str.blank</c>）正是靠这一格才登得进来 —— 与 <c>Gbase8sExpressions</c>
    /// 那条"每一行都要过 0,1,2… 各一次自查"是同一道闸的两种结果。现场补一颗函数时，
    /// 产出<em>时刻</em>的那一类要过 <see cref="Stored"/>，理由在类注释第二段。</summary>
    static FrozenDictionary<string, FunctionMapping> Build() => new Dictionary<string, FunctionMapping>(StringComparer.Ordinal)
    {
        // 括起来是必需的，而且本家把这一格量成了两条【实测·2230】：`1 || 2 = 12` 交回 0（算子被 `=` 抢了先），
        // `(1 || 2) = '12'` 才交回 1。共用层把结果再拼进 WHERE 时不带自己的括号（§5.6）。
        //
        // <b>两颗 COALESCE 不是保险，是 §5.8 那一条契约在本家的落地形</b>：C# 的 <c>a + b</c>（字符串）把
        // null 当空串，而本家的 <c>||</c> 任一实参为 NULL 则整式 NULL【实测·2200 第二臂：
        // <c>'a' || NULL</c> → NULL，第 22 轮】。不折就是"库里原本为 NULL 的那一行改完还是 NULL"，
        // 而那是 §5.8 明令不许静默发生的形状（共用层 <c>ExpressionTranslator</c> 那条的原话是"悄悄少行"，
        // 本家第一次跑到时它是<em>悄悄少值</em>：
        // <c>UpdateDeletePathTests.A_string_set_computed_from_the_column_concatenates_and_treats_null_as_empty</c>
        // 在第七家第一跑交回 <c>NULL</c> 而判据要 <c>/尾</c>，<c>obj/logs/v37-sqlite-int-1.log</c>）。
        // <b>为什么折在本族的模板里而不是共用层</b>：七家在这一格上分三档，而本家是第三档。
        // ① <b>模板本身就跳 NULL</b>：T-SQL、PG 血统、虚谷三家的 <c>CONCAT</c> 由函数自己给这一档，
        //   共用层再补 <c>COALESCE</c> 就是给"库已经会做的事"多写第六颗字面量。（三家各自的档位不同：
        //   虚谷是<em>真行上双向数过的</em>【<c>XuguExpressions</c> 第三条】，T-SQL 的那一条立论就是共用层那句
        //   "不折成 CONCAT 就会少行"而它跑在真库上，PG 血统那一档只有 <c>MySqlExpressions</c> 第二条拿它当对照面
        //   点了名、没有活库证人。）
        // ② <b>模板按文档传播 NULL 而本仓库没折</b>：MySQL 血统那一家自陈【同一条第二条】，Oracle 血统与
        //   GBase 8s 那两家同形而同样的"零活库证人"。这一档整格留在 §14 的跨库差异表里，
        //   <b>不由本笔替它们改</b>：三家一颗真库都没有，把它们的模板换掉等于把一条推断写成产出。
        // ③ <b>本家</b>：同一件事被<em>量</em>到了（2200），而量到的是"不折就错"，所以折在本族的模板里。
        // 折叠用的那颗问过了：二参与三参的 <c>COALESCE</c> 都交回右支【实测·2143：<c>COALESCE(NULL, NULL, 3)</c> → 3、
        // <c>IFNULL(NULL, 'd')</c> → <c>'d'</c>】，取 <c>COALESCE</c> 是因为本家另有 <c>Fn.Coalesce</c> 那颗模板，两处同一拼法。
        [QuickFn.Key(QuickFn.Concat, 2)] = new("(COALESCE({0}, '') || COALESCE({1}, ''))"),
        [QuickFn.Key(QuickFn.Concat, 2)] = new("(COALESCE({0}, '') || COALESCE({1}, ''))"),

        // 颗数而不是字节【实测·2201】：'中文' → 2、'😀' → 1（代理对算一颗），与另六家同一档而本家是量出来的。
        // 打在文本化的定点上无损【实测·2708：<c>'12.34'</c> → 5】。
        [QuickFn.Key(QuickFn.Length, 1)] = new("LENGTH({0})"),

        // 两颗只折 ASCII【实测·2202】：'éa' → 'éA'（é 原样），'İ' 一颗都不动。
        // 与 <c>COLLATE NOCASE</c> 同一条边界（【实测·2239】'ä' 与 'Ä' 在那一档下也不等），
        // 所以"这一家的 <c>UPPER</c> 与 <c>LOWER</c> 只到 ASCII"是两格而同一因。
        // CLR 的 <c>ToUpper</c> 走当前区域性 —— 差在哪一颗字符上要现场问，本表仍登这一形（六家同形）。
        [QuickFn.Key(QuickFn.Upper, 1)] = new("UPPER({0})"),
        [QuickFn.Key(QuickFn.Lower, 1)] = new("LOWER({0})"),

        // 只去空格【实测·2203】：<c>TRIM(char(9) || 'x' || char(9)) = 'x'</c> 交回 <b>0</b>，
        // 而两臂带空格的交回 1。两颗带字符集形（<c>TRIM(x, '`')</c>）也在场（同一跑的中间臂），
        // 本表不用它 —— CLR 的 <c>Trim()</c> 语义是"全部空白"，那一档在这里发不出来，记 §14。
        [QuickFn.Key(QuickFn.Trim, 1)] = new("TRIM({0})"),
        [QuickFn.Key(QuickFn.TrimStart, 1)] = new("LTRIM({0})"),
        [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("RTRIM({0})"),

        // 类注释第五格：这一形在本家登得进来，因为 <c>@p0</c> 在同一句文本里出现两次是驱动的合法用法
        // 【实测·4220 两臂 ∶ 4221 三处】；<c>Gbase8sExpressions</c> 为同一条形把这颗键删了。
        // "空格串算不算空白"这一问的答案与 <c>Fn.Trim</c> 同边（【实测·2204 数到 1 的是 <c>''</c> 那一行】，
        // 一颗 <c>'\t'</c> 在这一形下<em>不</em>成立），那是六家共有的语义差而不是本家新添的。
        [QuickFn.Key(QuickFn.Blank, 1)] = new("({0} IS NULL OR TRIM({0}) = '')"),

        // CLR 的 startIndex 从 0 起而 SUBSTR 从 1 起（七家同一条换算）。
        // 【实测·2205 两臂 + 2610 打在真列上】。
        // 越界与零长那一档本家比另六家宽：【实测·2130/2131】<c>SUBSTR('abcdef', 0)</c> 交回整串、
        // <c>SUBSTR('abc', 1, 0)</c> 交回 <c>''</c>、负起点从串尾数 —— 所以 <c>RequiresNonNegativeLength</c>
        // 那道闸不是防这一家报错（它不报），是防我们把 CLR 会抛的那一档静默发下去（§5.7 那一条）。
        [QuickFn.Key(QuickFn.Substring, 2)] = new("SUBSTR({0}, {1} + 1)", RequiresNonNegativeLength: true),
        [QuickFn.Key(QuickFn.Substring, 3)] = new("SUBSTR({0}, {1} + 1, {2})", RequiresNonNegativeLength: true),

        // 【实测·2206】，三处空串与重叠那一档也一起量了【实测·2236】：<c>REPLACE('aaa','aa','z')</c>
        // 交回 <c>'za'</c>（不回头扫），这一条与 CLR <c>String.Replace</c> 同。
        [QuickFn.Key(QuickFn.Replace, 3)] = new("REPLACE({0}, {1}, {2})"),

        // INSTR 同序（串在前、子串在后，与金仓的 STRPOS 与 Oracle 血统那一形一边，T-SQL 与 MySQL 血统是反的）。
        // 1 基换算成 CLR 的 0 基，找不到交回 -1【实测·2207 两臂】、NULL 传播 NULL【实测·2226】
        // （绑定参数那一臂同判：2612）。
        [QuickFn.Key(QuickFn.IndexOf, 2)] = new("(INSTR({0}, {1}) - 1)"),

        // 零点那一形，两臂的取舍写在类注释"不登记之外"那一格的第一条【实测·2600…2605】。
        // 里外两层都是必需的：内层 <c>DATE()</c> 截到日（【实测·2209】交回 <c>text</c>），
        // 外层把那一形<em>展开成时刻文本</em>再归一，否则会差一段 <c>' 00:00:00'</c>。
        [QuickFn.Key(QuickFn.ToDate, 1)] = new(Stored("STRFTIME(" + Timestamp + ", DATE({0}))")),

        // 这一格与 <c>Gbase8sExpressions</c> 那颗<em>不登记</em>的 <c>cast.text</c> 是同一问的两种答案：
        // 本家的目标型 <c>TEXT</c> 是<em>不带宽度</em>的合法型名【实测·2208：<c>CAST(12 AS TEXT)</c> → '12'，
        // typeof → 'text'】，所以那颗"缺的不是函数而是一个宽度数"在这里根本不成立。
        // 打在 REAL 上交回引擎的浮点转文本【实测·2606：<c>2.5</c> → '2.5'】，与 §13.5 那条"文本是载体"同边。
        [QuickFn.Key(QuickFn.CastText, 1)] = new("CAST({0} AS TEXT)"),

        // 六颗都过 STRFTIME 的掩码再折整数【实测·2210 六臂、2515 打在两形真列上】。
        // 三处要说清：① <c>'%S'</c> 只到整秒（2515），② 掩码打在读不懂的文本上交回 NULL 而不是报错
        // （【实测·2227/2613：<c>STRFTIME('%Y','not a date')</c> → NULL】，所以一句 <c>WHERE Year(x)=2026</c>
        // 在一列混着脏数据的文本上会<em>静默漏行</em>），③ 带偏移的文本读的是 UTC 那份字段（2412）。
        // 整数那一档【实测·2232】打在 <c>1700000000</c> 这种裸整数上是 NULL —— 引擎只认那几种时刻文本，
        // 而本家 <c>TimeSpan</c>/<c>TimeOnly</c> 落的是 <c>Ticks</c>（<c>SqliteTypes</c> 第六条），
        // 所以<em>那两列上这六颗都不该用</em>，共用层按 CLR 型就不会走到。
        [QuickFn.Key(QuickFn.DateYear, 1)] = new("CAST(STRFTIME('%Y', {0}) AS INTEGER)"),
        [QuickFn.Key(QuickFn.DateMonth, 1)] = new("CAST(STRFTIME('%m', {0}) AS INTEGER)"),
        [QuickFn.Key(QuickFn.DateDay, 1)] = new("CAST(STRFTIME('%d', {0}) AS INTEGER)"),
        [QuickFn.Key(QuickFn.DateHour, 1)] = new("CAST(STRFTIME('%H', {0}) AS INTEGER)"),
        [QuickFn.Key(QuickFn.DateMinute, 1)] = new("CAST(STRFTIME('%M', {0}) AS INTEGER)"),
        [QuickFn.Key(QuickFn.DateSecond, 1)] = new("CAST(STRFTIME('%S', {0}) AS INTEGER)"),

        // 'now' 是 UTC【实测·2211 两臂差八小时，与本机偏移正好对上】，所以 <c>Now</c> 要带 'localtime'。
        // 为什么这一家 <c>Now</c> 配 <c>localtime</c>：驱动写入 <c>DateTime</c> 时<em>丢掉 Kind</em>
        // 【实测·2400 的前三行：<c>Unspecified</c>/<c>Utc</c>/<c>Local</c> 落成同一串 23 字符的裸时刻】，
        // 而 <c>Local</c> 那一档写的就是本地墙上时间 —— 于是与"库里最近的那一行"可比的只有本地档。
        // 那颗 <c>UtcNow</c> 与 <c>Kind=Utc</c> 写进去的裸串也对得上，因为写的时候<em>已经是</em>那一格的墙上时间。
        // 两形的归一命中【实测·2407/2408/2516】。类注释最后一条：这一家没有"库那台机器的钟"这一格。
        [QuickFn.Key(QuickFn.Now, 0)] = new(Stored("STRFTIME(" + Timestamp + ", 'now', 'localtime')")),
        [QuickFn.Key(QuickFn.UtcNow, 0)] = new(Stored("STRFTIME(" + Timestamp + ", 'now')")),

        // 六颗 <c>Add*</c>：类注释第三格（月/年溢出）与第二格（<c>%+d</c> 与 <c>%+f</c> 的分工）都在这一组上。
        // 年与月走 <c>%+d</c>（CLR 只用整数部分，4219/2409/2702）；日与时分秒走 <c>%+f</c>（2410/2215/2511/2614）。
        // 修饰符的单复数两形都收【实测·2213】，本表统一发复数（与 <c>%+f</c> 那一臂的小数同读法：
        // <c>+1.5 days</c> 才是"一天半"，<c>+1.5 day</c> 没有量过，不赌）。
        // 月末那一档【实测·2138/2705/2707】是本家与另六家<em>唯一一处算术结果本身不同</em>的格。
        [QuickFn.Key(QuickFn.AddYears, 2)] = new(Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+d years', {1}))")),
        [QuickFn.Key(QuickFn.AddMonths, 2)] = new(Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+d months', {1}))")),
        [QuickFn.Key(QuickFn.AddDays, 2)] = new(Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+f days', {1}))")),
        [QuickFn.Key(QuickFn.AddHours, 2)] = new(Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+f hours', {1}))")),
        [QuickFn.Key(QuickFn.AddMinutes, 2)] = new(Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+f minutes', {1}))")),
        [QuickFn.Key(QuickFn.AddSeconds, 2)] = new(Stored("STRFTIME(" + Timestamp + ", {0}, printf('%+f seconds', {1}))")),

        // 一元那一臂补字面量 0 而不是发 <c>ROUND(x)</c>：<c>ROUND</c> 在这一家确实认一元
        // 【实测·2217/2234 的第一臂】，而<em>它到 0 位而不是 CLR 那一档</em>是巧合，写成两臂一致更可读
        // （与 <c>Gbase8sExpressions</c> 同一句理由）。三颗要记的：
        // ① 半数是<em>远离零</em>进的（2217：<c>2.5</c> → 3、<c>-2.5</c> → -3）而 CLR <c>Math.Round</c> 是银行家
        // ——<b>六家同然</b>（那六家的模板就是这颗 <c>ROUND</c>），本家只是第一家量出来的一格，记 §14；
        // ② 负标度被<em>忽略</em>（2217：<c>ROUND(1234.0, -2)</c> 交回 <c>1234</c> 而不是 <c>1200</c>），
        // CLR 那一档压根不接受负标度（抛），所以这一格不会错值而是"少一步"；
        // ③ 定点那一列上它是折了文本再算【实测·2703 第二行：<c>ROUND('-0.5')</c> → -1】。
        [QuickFn.Key(QuickFn.Round, 1)] = new("ROUND({0}, 0)"),
        [QuickFn.Key(QuickFn.Round, 2)] = new("ROUND({0}, {1})"),

        // 【实测·2221/2609/2703】。两颗一起记：<c>CEIL</c> 与 <c>CEILING</c> 都在场且同值
        // 【实测·2218】，本表跟 MySQL 血统那颗的短形 <c>CEIL</c>；交回的是 <c>REAL</c>
        // （2218 的 typeof 那一臂），所以 CLR 的 <c>decimal</c> 在这一家过这两颗会退成浮点 ——
        // 那是<em>算式的返回值</em>而不是存储，与本文件的第四格同一因。
        [QuickFn.Key(QuickFn.Abs, 1)] = new("ABS({0})"),
        [QuickFn.Key(QuickFn.Ceiling, 1)] = new("CEIL({0})"),
        [QuickFn.Key(QuickFn.Floor, 1)] = new("FLOOR({0})"),

        // 实参序与 CLR <c>Math.Pow(x, y)</c> 同（另六家同形，<c>POWER({0}, {1})</c>）。
        // <c>POW</c> 与 <c>POWER</c> 两形都在【实测·2219】，跟另六家那一颗长的；<c>SQRT(9)</c> 交回的
        // 是 <c>3</c> 一颗 <c>Double</c>（同一条读数），所以本家这两颗出来的数落不进定点那一列而不报错
        // —— 与本文件的第四格同一因（<c>typeof(CEIL(...))</c> 那一臂在 2218/2703）。
        // 旁边两颗在场而本表没有对应的键，记一句免得下一版重新发现：<c>EXP(1)</c> 与 <c>LOG(1000)</c>
        // （常用对数）也都在【实测·2219】，而 CLR 的 <c>Math.Log</c> 一档是<em>自然</em>对数 ——
        // 真要加 <c>math.log</c> 那一颗，先答"这一家的 <c>LOG</c> 是不是那一档"（2219 给的读数是
        // <c>LOG(1000) = 3</c>，也就是说<em>不是</em>，得配 <c>LOG(x)/LOG(e)</c> 或 <c>LN</c> 那一颗，而后者本次没量）。
        [QuickFn.Key(QuickFn.Power, 2)] = new("POWER({0}, {1})"),
        [QuickFn.Key(QuickFn.Sqrt, 1)] = new("SQRT({0})"),

        // 本家没有 <c>GREATEST/LEAST</c>【文档·SQLite 手册把它们列在"多参标量聚合"那一类】，
        // 场上有的就是<em>变参的 <c>MAX()/MIN()</c></em>【实测·2220 四臂、2608、2710】。
        // 两格代价都写在类注释第四格：定点那一列上它比的是词法（2608），且文本在这一家的类型序里
        // 恒大于数（2710 的 <c>MAX("m", 100)</c> → <c>'12.34'</c>）。NULL 那一档与 <c>Math.Max</c> 不同：
        // 【实测·2220 第四臂】<c>MIN(NULL, 1)</c> 交回 NULL 而不是跳过 —— 本表不补 <c>COALESCE</c>，
        // 因为补了就把"参与比较的是 NULL"这件事藏起来，而那正是共用层 <c>Nullable</c> 契约要露的一格。
        [QuickFn.Key(QuickFn.Greatest, 2)] = new("MAX({0}, {1})"),
        [QuickFn.Key(QuickFn.Least, 2)] = new("MIN({0}, {1})"),

        // 【实测·2223（含 <c>IIF</c> 那一形，本表不用）、2710 的 <c>COALESCE(NULL, "m")</c> 那一臂】。
        [QuickFn.Key(QuickFn.Coalesce, 2)] = new("COALESCE({0}, {1})"),
        [QuickFn.Key(QuickFn.NullIf, 2)] = new("NULLIF({0}, {1})"),

        // 六颗聚合【实测·2222 八臂、2607】。两颗本家特有的：
        // <c>COUNT(DISTINCT x)</c> 在这一家与 <c>COUNT(x)</c> 同一档而 <c>GROUP_CONCAT</c> 要带分隔符才可控
        // （【实测·2224】，本表不发 <c>GROUP_CONCAT</c> —— CLR 侧没有对应符号）；
        // <c>SUM</c> 在<em>全 NULL</em> 时交回 NULL 而 <c>TOTAL</c> 交回 <c>0.0</c>
        // 【实测·4212 那一行 ∶ 4213 那两颗 typeof（三行全 NULL 的列，空集那一臂同判在 4214）】，
        // 本表跟 <c>SUM</c>（六家同），
        // 因为 CLR 那侧的 <c>Sum()</c> 对空集交的正是 <c>null</c> 一颗 <c>decimal?</c>。
        // <c>Min/Max</c> 在定点列上是词法【实测·2506】，与 <c>Greatest/Least</c> 同一格同一代价。
        [QuickFn.Key(QuickFn.Count, 1)] = new("COUNT({0})"),
        [QuickFn.Key(QuickFn.CountDistinct, 1)] = new("COUNT(DISTINCT {0})"),
        [QuickFn.Key(QuickFn.Sum, 1)] = new("SUM({0})"),
        [QuickFn.Key(QuickFn.Average, 1)] = new("AVG({0})"),
        [QuickFn.Key(QuickFn.Min, 1)] = new("MIN({0})"),
        [QuickFn.Key(QuickFn.Max, 1)] = new("MAX({0})"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>STRFTIME 那颗掩码串（带单引号，所以拼进模板时不用再补）。本表九处用到它，
    /// 一处写死是为了不让九行<em>各自</em>漂移成两种精度。</summary>
    const string Timestamp = "'%Y-%m-%d %H:%M:%f'";

    /// <summary>把引擎交回的定长毫秒形<em>归一回驱动自己写进库里的那一形</em>：先削尾零，再削那颗因此悬空的点。
    /// 类注释第二段整段是这一颗方法的判据（【实测·2401 四臂、2403 对 2404、2405 对 2406、2513 四行全等、2706 两行全等】）。
    /// <b>为什么两颗 <c>RTRIM</c> 而不是 <c>TrimEnd</c> 那一族</b>：<c>RTRIM(x,'0')</c> 削到尾零为止就停在
    /// <c>'.'</c> 上（【实测·2401 第一臂：<c>…10:20:30.000</c> → <c>…10:20:30</c> 而不是 <c>…10:20:3</c>】），
    /// 那颗点正是"秒位上的零"与"小数位上的零"的分界，所以它不会把 <c>10:20:00</c> 削成 <c>10:20:0</c>
    /// （【实测·2401 第四臂】那一行就是专门试这一格的）。
    /// <b>两处它救不回来，两处都记了账</b>：<c>%f</c> 只到毫秒（2214/2402），以及带偏移的输入会被折成 UTC
    /// 裸时刻（2412）—— 后者是本家的 <c>DateTimeOffset</c> 列<em>不能过日期算术</em>的一条硬约束，
    /// 与 <c>SqliteValues.InlineOffset</c> 旁边那句"能等值而不能排序"并排记 §14。</summary>
    static string Stored(string expression) => "RTRIM(RTRIM(" + expression + ", '0'), '.')";
}
