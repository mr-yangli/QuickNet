using QuickSql.Sql;
using System.Collections.Frozen;

namespace QuickSql.Providers.Xugu;

/// <summary>C# 形状 → 虚谷模板表（§5.8，第五家）。键与另四族同一套 <c>Fn.Key(符号, 元数)</c>，
/// 所以五边比的是同一批符号；<b>键集一颗不少（45 颗）</b>，因为少一颗不是"少一条模板"而是那一格在
/// 翻译器里抛 <c>UntranslatableExpressionException</c>，报错形状与"这一家没有这个函数"完全一样，得人再分一次。
///
/// <para><b>这一份的出处判据与另四份不同，它是这一版真正要交的东西</b>。前四族的模板表按厂商文档写
/// （"这一家有 ROUND"那种句子来自手册），这一族<em>两样都有</em>，而且两份的口径不一样：
/// 厂商文档进本仓库了（SQL 手册那一页一页读过），同时还有两家第三方 ORM 为这一家发出去的 SQL 文本可当产物读。
/// 逐颗键的出处在下面标成四档，而<b>优先级也按这四档排</b>：
/// <list type="bullet">
/// <item>【文档】—— 厂商那一页这么写。它是"官方说法"，而官方说法会漏、会与新版本不符（<see cref="XuguTypes"/>
/// 那一族类型表的读法就是照它写的，而那张表在 v0.32 第二格有了逐颗的实测档，见那里）。</item>
/// <item>【产物】—— 有 shipped 代码这么发过。这是"库没驳回过"级别的事实，比文档硬。
/// <b>而它在 v0.32 第二格被证明<em>也不是顶档</em></b>：本表那六颗日期加法就是产物给的、文档没这么说，
/// 而这一台活库把那一形整批驳回了（下面那颗键的注释是全过程）—— 产物说的是"有人这么发过"，
/// 不是"这台库收过"。它能推翻文档，它自己也要被实测推翻。</item>
/// <item>【推断】—— 文档与产物都没有出处，取"与已落地四族同形"的那一支。这一档<em>是推断</em>，
/// 逐位进 §13.5 那本台账的"未实测"列。它下面还有两个限定词：<b>【推断·偏离产物】</b>只有两颗，
/// 都在下面写了理由，而且写的是<em>同一句判据的两次应用</em>——产物优先的前提是"产物在那个槽位上
/// 做的是与共用层同一件事"，当产物那一形服务的是另一个语义（<see cref="QuickFn.Blank"/> 与
/// <see cref="QuickFn.Coalesce"/> 那两格），跟它就是改语义而不是跟出处。</item>
/// <item><b>【实测】—— 本表新加的那一档，也是这一族在这一格上唯一有它的理由</b>：v0.32 第二格把这张表里
/// 从没发进真库的那批模板逐颗发进了一台活虚谷（<c>RECON_PHASE=expr</c>，日志
/// <c>obj/logs/v32-recon-expr-{1..5}.log</c>）。<b>它压在前三档之上，压的方式有两种</b>：
/// 印证（TRIM 三形、SUBSTR、GREATEST/LEAST、REPLACE、COALESCE、NULLIF、ADD_MONTHS 那一批绿），
/// 与驳回（日期加法那一整批红，<c>GETSECOND</c> 那一条在场而语义少一截）。
/// 写在下面各颗键上的判据是<em>那一问交回的那颗数</em>，不是"跑过了"这一句。</item>
/// </list>
/// 这一格值得展开的地方是：<b>第五家第一次让"血统"这个词在一张模板表上站不住</b>。它的日期部分与
/// <c>SYSDATE</c>/<c>DUAL</c>/<c>INSTR</c> 的四参形落回 Oracle 那一族，它的日期加法
/// <c>DATE_ADD(x, INTERVAL n unit)</c> 落回 MySQL 血统那一形，它的 <c>CAST(… AS date)</c> 落回 T-SQL 那三家的多数派，
/// 而它的 <c>GETYEAR</c> 六颗与 <c>CAST(… AS NVARCHAR)</c> 是谁都不像的第五形。
/// §9.1 那句"类型名与转义规则自成一套"因此要读成：<em>每格各归一家，而不是整族归一家</em>。
/// <b>而实测把这句话又推前了一步：连"归哪一家"都不是谈出来的，是问出来的</b>——
/// 那六颗日期加法在实测之后不再落回 MySQL 那一形，改落 Oracle 那一族的
/// <c>NUMTODSINTERVAL</c> / <c>ADD_MONTHS</c>，因为这一台库只收后者的乘数槽。</para>
///
/// <para>三处语义与另四家不逐字相同、对拍查不出来（与 <c>PostgreSqlExpressions</c>、<c>OracleExpressions</c>
/// 各自记的那两格同一档）：<b>其中两处已在 v0.32 第二格量掉，剩一处留着</b>。
/// ① <c>str.length</c> 在这一家计字符还是字节【原判：文档没分这两颗，产物只发过 LENGTH】—— 量到的是
/// <em>计字符</em>（F-32 逐颗：<c>LENGTH('甲')=1</c> 而 <c>OCTET_LENGTH('甲')=2</c>），
/// 而它顺带多出一颗原本没在清单上的未知：那颗"字符"由连接串的 <c>CHARSET=</c> 决定，
/// 同一句在两台读法下交回 3 与 5（见 <c>Fn.Length</c> 那一颗的注释）。
/// ② <c>date.second</c> 那一颗 <c>GETSECOND</c> 与 <c>EXTRACT(second FROM …)</c> 在"含不含小数秒"上
/// 可能差一档 —— <b>量到了，而且比"差一档"更糟：两形<em>都</em>带小数</b>
/// （E-05a/E-05b 各交回 <c>59.456</c>），所以本仓库要跟 C# 对齐只能换第三颗裸名 <c>SECOND(x)</c>
/// （E-13a 交回 <c>9 &lt;Int32&gt;</c>），那两形都在这一家而在这一格都不能用。
/// ③ <c>agg.average</c> 在整数上截断与否<b>同一条仍未量</b>：这一版问的都是逐行表达式，聚合那一批
/// 没有一条真库旁证，它留在 §14 那张跨库差异表里，重开条件是"聚合下推那一期接它"。
/// 后两处原判都在 §14 各占一行，这一版把它们<em>各加一个实例</em>而不是新加一行。</para>
///
/// <para><see cref="SupportsRegex"/> 留 <see langword="false"/>：三档出处里这一颗<em>一档都没有</em>
/// （产物里没出现过正则函数，文档那一侧本次没有读到对应页），而 §5.7 的 P0 行里没有正则那个槽位、
/// 共用层没有任何一处消费这颗位 —— 声明一个没有消费者的"支持"正是 §13.5 那本台账要避免的东西，与另四家同一立场。</para></summary>
public sealed class XuguExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly Lock _gate = new();

    /// <summary>写 <c>||</c> 原判【推断】，<b>v0.32 第二格改判【实测】</b>：两台问过的连接上
    /// <c>SELECT '甲' || '乙'</c> 都答 <c>"甲乙"</c>（F-01），<c>CONCAT('甲','乙')</c> 同答（F-02）。
    /// 先把这一槽的分布数出来（判据：<c>grep -rn "ConcatOperator =&gt;" src/
    /// --include=*.cs | grep -v "///"</c> 五处）：落地前四家分三支 —— 金仓与 Oracle 血统都报 <c>||</c>（两家）、
    /// T-SQL 报 <c>+</c>、MySQL 血统报 <c>CONCAT</c>（它那一颗的理由写在 <c>MySqlExpressions</c>：
    /// 那一家的 <c>||</c> 默认是逻辑 OR），所以这一格从来没有过"多数派"，只有"两家共用的一支"。
    /// 这一家加入的是那一支有<em>两家</em>的先例，同时它也是 <c>INSTR</c> 四参形那一格同一条取向。今天这一格<em>没有消费方</em>（拼接一律走 <c>Fn.Key(Fn.Concat, 2)</c>），
    /// 所以它是台账上的一颗而不是产出差异；改判只把"这一家有 <c>||</c>"从赌变成核，<b>没有</b>把那颗"没消费方"
    /// 一起改掉 —— 真库跑过的是探针发的那一句，不是本仓库产出的句子（探针纪律第三条）。
    /// <b>而它一旦接上就多一条要实测的语义，这一条本版也量掉了</b>：文档那一侧记着这一家的 <c>||</c>
    /// 把 NULL 当空字符处理（<c>'a' || NULL</c> 得 <c>'a'</c> 而不是 NULL），本版在<em>真行</em>上双向数过
    /// （F-53，靶表两颗 <c>VARCHAR</c> 行）：<c>("S" || NULL) = "S"</c> 数到 2、<c>("S" || NULL) IS NULL</c>
    /// 数到 0，两向相加正好等于行数 —— 空字符派坐实，而那颗函数形 <c>CONCAT("S", NULL) = "S"</c> 同样数到 2，
    /// 也就是这一家<em>运算符与函数同侧</em>（MySQL 血统那一家两形不同侧，那里 <c>CONCAT</c> 传染而 <c>||</c> 是逻辑 OR）。
    /// 这一条与 Oracle 血统同侧、与 T-SQL/金仓/MySQL 三家的默认相反，而共用层这一格<em>没有</em> NULL 语义的槽位
    /// —— 它今天不产出差异，将来产出的是"同一句拼接在五家里有三种 NULL 行为"，所以那一格进 §14
    /// 的跨库差异表而不是本文件的一段解释；本版做的只是把那一格从"不知道"挪到"知道而仍没有槽位"。</summary>
    public string ConcatOperator => "||";

    /// <summary>【实测·真行，v0.32 第二格改判；原判【推断】】：三家取 <c>\</c>，只有 MySQL 血统换成了 <c>~</c>，
    /// 而它换的理由是那一家的 <c>sql_mode</c> 让 <c>'\'</c> 成为一条未闭合的串（<c>MySqlExpressions</c> 类注释那一段）。
    /// <b>【v0.31】上面那句"三家"漏了它自己</b>：这一家发的就是 <c>\</c>，所以这一格今天是
    /// <b>四家取反斜杠 ∶ 一家取波浪号</b>。判据这一条 grep 写作
    /// <c>grep -rn "LikeEscapeCharacter =&gt;" src/ --include=*.cs</c>（箭头要写成实体，否则本文件这一行
    /// 自己会被数进去而得到六），五处命中、一家一处。同一格在 <c>PostgreSqlExpressions</c> 里记的是同一个数，
    /// 两处一起以这条 grep 为准。
    ///
    /// <para><b>改判的根据</b>：<c>obj/logs/v32-recon-funcs-12-plain.log</c> 的 F-51 把带转义的模式扫在
    /// <em>真行</em>上（两行：一行 <c>a_b</c>、一行 <c>axb</c>），交回
    /// <c>LIT_BSLASH=1 | LIT_BARE=0 | LIT_BANG=1 | LIT_WILD=2 | LIT_PCT=2</c> 与绑定参数那一侧的
    /// <c>PAR_BSLASH=1 | PAR_BARE=0 | PAR_WILD=2 | PAR_PCT=0 | PAR_STARTS=1</c>，而 F-51g 的
    /// <c>LENGTH('a\_b')=4 ∶ LENGTH(:p)=4</c> 排除了"那颗反斜杠被字面量解析器吃掉"。三件事一次数清：
    /// ① 这一家<em>认</em> <c>ESCAPE</c>，且换感叹号同样认（转义符不写死）；② 不给 <c>ESCAPE</c> 子句时
    /// 反斜杠是<em>字面字符</em>，所以本仓库必须把子句写出来（<c>DialectBase</c> 那一句无条件发射是对的）；
    /// ③ 本仓库真发的那一形（模式走绑定参数、结尾补 <c>%</c>）在这一家是命中的 —— <c>PAR_STARTS=1</c>。
    /// <b>而它<em>不</em>认的是常量折叠那一条路</b>：同一套式子写成 <c>'a_b' LIKE 'a\_b' ESCAPE '\'</c>
    /// 这种常量比常量，F-50（<c>obj/logs/v32-recon-funcs-11-{plain,utf8}.log</c>）交回六颗 0，
    /// 一度被读成"这一家收下 <c>ESCAPE</c> 而不理它"。那条读法作废，教训与判法记 §13.5 的探针纪律
    /// （判 LIKE 的语义要扫真行），集成层的证人是 <c>ReadPathTests.Like_predicates_honor_their_escapes</c>。</para>
    ///
    /// <para>这一家没有 <c>sql_mode</c> 那颗模式【产物里三处都没有】，所以不跟第四家；今天也不需要跟 ——
    /// 五家在这一格同一形。重开条件同 §14 那一行（会话模式建模）。</summary>
    public char LikeEscapeCharacter => '\\';

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

    static FrozenDictionary<string, FunctionMapping> Build() => new Dictionary<string, FunctionMapping>(StringComparer.Ordinal)
    {
        // 这一格是五家唯一一颗"五家同形而没有一处产物出处"的：四族全都发 CONCAT({0}, {1})，
        // 而这一家的两份产物里都没有出现过 CONCAT。取同形那一支的判据是"五家已经五家都这么写"，
        // 它比"这一家自己没说过"更接近真相，但不是真相 —— 记 §14 那份清单里"最不可能错、所以要最后重读"那一类。
        // <b>那颗"最后重读"在本版本格读完了【实测】</b>：F-02 交回 <c>CONCAT('甲','乙') = "甲乙"</c>，
        // 而 F-53 在真行上量到它遇 NULL 的语义与 <c>||</c> <em>同侧</em>（都是空字符派，两颗各数到 2）——
        // 上面那句"最不可能错"今天兑现了，但它兑现的是<em>这颗函数在场</em>，不是"这一家只有它"：
        // 同一段路 <c>||</c> 也在场，两形都在而 NULL 语义同侧，所以这一格没有留下要重开的岔。
        [QuickFn.Key(QuickFn.Concat, 2)] = new("CONCAT({0}, {1})"),

        // 【产物】：已发布的第三方适配器为这一家发的就是裸 LENGTH({0})。<b>v0.32 第二格把那颗【没有出处】
        // 量掉了，而答案是"两边都对，因为问的不是同一颗东西"</b>：LENGTH 数<em>字符</em>、OCTET_LENGTH 数
        // <em>字节</em>（F-27/F-32 逐颗：ascii 一颗都是 1，中文那颗 LENGTH=1 / OCTET_LENGTH=2）——
        // 类注释第三段第一颗那句"同名那一颗落在哪一侧不知道"因此改判为"落在字符这一侧"。
        // <b>而它带出一颗新的、更该记的未知</b>：字符怎么切由连接串上那颗 <c>CHARSET=</c> 决定 ——
        // 同一句 <c>LENGTH('甲公司')</c> 在带 <c>CHARSET=UTF8</c> 的连接上是 3，不带的连接上是 5
        // （F-32 utf8 ∶ cjk-plain-3 那一份，库的 <c>CHAR_SET</c> 两处都答 <c>GBK</c>）。
        // 于是这一颗函数在<em>换连接串的两台同一台库</em>上给出不同的数，而 provider 看不见那颗开关
        // （§14"连接串参数改变方言假设"同一行，与 <see cref="XuguTypes"/> 的 <c>WorstCaseBytesPerChar</c> 那一格同因）。
        [QuickFn.Key(QuickFn.Length, 1)] = new("LENGTH({0})"),
        [QuickFn.Key(QuickFn.Upper, 1)] = new("UPPER({0})"),
        [QuickFn.Key(QuickFn.Lower, 1)] = new("LOWER({0})"),

        // 原判【推断】，v0.32 第二格改判【实测】：三形一颗一句全绿（F-04 交回
        // <c>TRIM('  x  ')="x" | LTRIM('  x')="x" | RTRIM('x  ')="x"</c>），所以"取那两家那一形"这颗赌注兑了。
        // 顺带把 <c>Fn.Blank</c> 那一格要用的另一颗量了：这一家<em>不</em>需要金仓那颗 BTRIM，
        // 也<em>不</em>像 T-SQL 那一家要套两层（E-10h 在真行上把 <c>TRIM("S") = ''</c> 数到 1）。
        // （金仓那颗叫 BTRIM，T-SQL 那一家压根要 LTRIM(RTRIM(…)) 两层。）
        [QuickFn.Key(QuickFn.Trim, 1)] = new("TRIM({0})"),
        [QuickFn.Key(QuickFn.TrimStart, 1)] = new("LTRIM({0})"),
        [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("RTRIM({0})"),

        // 【推断·偏离产物·第一颗】。产物里这一槽发的是 `( {0}<>'' AND {0} IS NOT NULL )`——注意它<em>不带 TRIM</em>，
        // 因为它对的是 C# 的 IsNullOrEmpty；而共用层这一格对的是 IsNullOrWhiteSpace（§5.7 那一行）。
        // 跟着产物的字形走会得到"空格串在四家绿着的写法在第五家永远不成立"，那是 §10 那一类
        // "看着能用而语义少一截"的形状，而且它不报错、不红、只是少一批行。
        // 取与 MySQL 血统<em>逐字同形</em>的那一支（金仓那一家带的是 BTRIM，Oracle 血统那一族因为
        // "'' 就是 NULL"只能写 TRIM(x) IS NULL —— 这一家没有那一条，所以它落回 = '' 那三家这一边，
        // 连同这一家是四家；同一格在 OracleExpressions 类注释里数的是同一个 4 ∶ 1）。
        // 但这一格的"没有那一条"是<em>带条件</em>的：文档记着这一家有一颗 EMPTY_STR_AS_NULL 参数，
        // 默认 FALSE（空串就是空串，因此 = '' 有意义），关掉它才滑向 Oracle 那一族"'' 即 NULL"的读法。
        // 于是这一形在默认实例上对、在那颗参数被打开的实例上会<em>静默漏掉纯空格串</em>
        // （TRIM 出来的空串成 NULL，两侧都不成立）。它与 LikeEscapeCharacter 那格同属"会话/实例参数改变
        // 方言假设"那一类，缺口在 §14 同一行（金仓的兼容模式、OceanBase 的 MySQL 模式、这一家的这颗，三个实例）。
        // <b>那颗参数在这一台实例上量到了默认档【实测·真行】</b>（E-10，<c>obj/logs/v32-recon-expr-4.log</c>）：
        // 往 <c>VARCHAR(10)</c> 灌一颗 <c>''</c>，读回 <c>LENGTH</c> = 0、<c>IS NULL</c> 数到 <b>0</b>、
        // <c>= ''</c> 数到 <b>1</b> —— 空串<em>没有</em>变成 NULL，默认档 FALSE 坐实，本仓库这一形在这台实例上是对的。
        // <b>同一跑还把"参数打开会静默漏"那半句的后果钉清了</b>：那颗参数今天没有读法
        // （<c>all_variables</c> / <c>props</c> 四形候选全报"表或视图不存在"，N-01 那一段），
        // 所以 provider 既看不见它也没法验它 —— 这一格留在 §14 那一行，而它现在多了一句：
        // <em>换一台实例部署时要重跑 E-10f/E-10g 那一对（两颗数相加 = 1 才说明默认档还在）</em>。
        // 顺带量的两颗一起记在这里，因为它们改的是<em>读法</em>不是文案：<c>CHAR(10)</c> 灌 <c>'ab  '</c>
        // 读回 <c>LENGTH</c> = 2（去尾空格那条【文档】在真行上成立，而它<em>不</em>补空格），
        // 且那一列与一颗无尾空格的 <c>VARCHAR</c> 列<em>相等</em>（E-10e 数到 1）—— 等号成立是"两边都存成 2 颗字"
        // 的结果，不是这一家有 PAD SPACE；别把它读成后者。
        // 【v0.31】上面那份三分清单要打上问号而不是照读：同一行在 <c>IdentifierPolicy.Xugu</c> 数的是
        // "第四个实例（前三颗：金仓兼容模式、lower_case_table_names、这一家的 CHAR_SET）"，在 <c>MySqlTypes</c>
        // 数的也是四颗，而三处各写了一份不同的名单，谁也没签过全量（<c>EMPTY_STR_AS_NULL</c> 只在本文件出现过，
        // <c>NLS_LENGTH_SEMANTICS</c> 只在 <c>OracleTypes</c>/<c>DamengProvider</c> 出现过）。
        // <b>实例清单合并成一份可数的记 §14 那一行</b>，本版回写规格时按五族逐颗列全，本文件不再自带那颗"三"。
        [QuickFn.Key(QuickFn.Blank, 1)] = new("({0} IS NULL OR TRIM({0}) = '')"),

        // 原判【推断】+【无出处】两颗，v0.32 第二格一并改判【实测】：
        // ① <c>SUBSTR</c> 两档就在（F-05 交回 <c>SUBSTR('abcdef', 2, 3)="bcd"</c> 与负索引那一档
        // <c>SUBSTR('abcdef', -2)="ef"</c>），而且打在真列的<em>表达式结果</em>上也对（F-44 把
        // <c>TO_CHAR(MAX("PAIDAT"))</c> 截到 19 颗，读的正是本仓库裁判要的那一格）；
        // ② 那颗【无出处】问的是"这一家也认 SQL 标准那一形吗"，答<em>认</em>：
        // <c>SUBSTRING('abcdef' FROM 2 FOR 3)</c> 同样交回 <c>"bcd"</c>（F-06）。
        // 认归认，本表仍发 <c>SUBSTR</c>：两形都在时跟的是<em>产物与 Oracle 血统那一形</em>，
        // 而换形的代价是把这一族唯一一处与另一族逐字同形的键改成第五族的口味，收益为零。
        // 这一格是"新家族把一颗键拉回某一家的旧形状"的那一类：它不新增第 5 形，而是让第 3 形有了第二家。
        [QuickFn.Key(QuickFn.Substring, 2)] = new("SUBSTR({0}, {1} + 1)", RequiresNonNegativeLength: true),
        [QuickFn.Key(QuickFn.Substring, 3)] = new("SUBSTR({0}, {1} + 1, {2})", RequiresNonNegativeLength: true),

        // 元数含接收者：REPLACE 三槽在 /3、INSTR 两槽在 /2。抄错元数的后果在金仓那一族已经演示过一次
        // （永远查不到，而不是渲染错 —— 那两格当时全仓库没有一条含它们的黄金样本）。
        // <b>两颗都在真列上核过【实测】</b>：E-02 的 <c>REPLACE("NOTE", 'ul', 'UL')</c> 交回
        // <c>"fULcrum-p0"</c>（三槽逐位对得上，元数没错位），E-03 见 <see cref="Fn.IndexOf"/> 那一颗的注释。
        [QuickFn.Key(QuickFn.Replace, 3)] = new("REPLACE({0}, {1}, {2})"),

        // 【产物】+ 实测：这一家发的是<em>四参</em> INSTR(串, 子串, 1, 1)，后两颗是"起始位置"与"第几次出现"。
        // 于是这一格有了第五形：金仓/Oracle 血统发 2 参 INSTR，T-SQL 发反序的 CHARINDEX，MySQL 血统发反序的 LOCATE，
        // 这一家发同序而多两颗实参。四参那一形与 2 参<em>同义</em>（第一个匹配、从第一字符起），
        // 而它比 2 参多一个好处：它不需要假设这一家的 INSTR 默认值与 Oracle 一致 —— 那正是产物自己选的写法。
        // <b>那颗"假设"本版不必再假设【实测·真列】</b>：本仓库发的整形（含外面那层减一）打在真列上交回
        // <c>(INSTR("NOTE", 'ul', 1, 1) - 1) = 1</c>（E-03，<c>quicksql-p0</c> 里 <c>ul</c> 的第 2 颗 ⇒ 0 基的 1），
        // 而找不到的那一档在 N-02 量到 <c>INSTR(…, '公', 1, 1) = 0</c> ⇒ 减一之后是 <c>-1</c>，与 C# 同值。
        // 中文子串那一档也在这两颗上分岔过（utf8 连接命中、plain 连接不命中，N-02 两处并排），
        // 它分岔的不是 INSTR 而是<em>那颗 CHARSET</em>，与 <c>Fn.Length</c> 那格同因、同一行 §14。
        [QuickFn.Key(QuickFn.IndexOf, 2)] = new("(INSTR({0}, {1}, 1, 1) - 1)"),

        // 【产物】+ 实测：CAST(x AS date) 逐字出现在这一家的产物里，所以这一格不跟着 Oracle 血统那颗 TRUNC，
        // 而是落回 T-SQL / 金仓 / MySQL 血统那三家的多数派。代价与那三家一样：DATE 列上它是空操作。
        // E-08a 把那句代价量成了数：真列 <c>2026-05-06 07:08:09.456</c> 过这一形交回
        // <c>2026-05-06 00:00:00.0000000</c> —— 截到日、不报错、也不改型（仍是 <c>DateTime</c>）。
        [QuickFn.Key(QuickFn.ToDate, 1)] = new("CAST({0} AS date)"),

        // 【产物】+ 实测（推到 40 000 字那一档）：这一家发的是<em>裸</em> NVARCHAR，不带宽度。
        // 四家在这一格已经是四形（nvarchar(max) / text / TO_CHAR / char），第五家是第五形。
        // 裸写的默认宽度没有出处，而它是<em>会截断</em>的那一类默认值 —— 这一格进过 §14 那份清单，
        // 而它在 v0.32 第二格被量掉了：<b>裸形到 40 000 字不截</b>（BT-42d/e 两颗尺各交回 <c>40000</c>，
        // 与不 CAST 的对照列同数；BT-40e/f 先在 30/60/200 三档上给了同一答案，
        // <c>obj/logs/v32-recon-booltz-9.log</c>）。上限没找到，但"裸写就掉字符"这一句在这一台上不成立。
        // <b>而这一跑量出来的不是"裸形安全"，是"带宽度那一形在这一家<em>有毒</em>"</b>，这一条比上面那句重要：
        // 目标宽度<em>窄于值的实际长度</em>时，整条查询交回<em>空结果集，不抛异常、不进错误行</em> ——
        // 五颗靶全复现（<c>CAST(TEXT AS NVARCHAR(100))</c> 遇 1 000 字与 40 000 字、
        // <c>CAST(VARCHAR(10) 列上的 6 字值 AS VARCHAR(2))</c> 在选择列与谓词列上各一次，BT-43c/f/g、BT-44d/e），
        // 而钉死"是结果集空掉、不是行被滤掉"的那一句是 <c>SELECT COUNT(*) … </c> 交了 <c>0</c> 行 ——
        // 聚合永远该有一行。等宽与更宽的目标正常（BT-44f 的 <c>VARCHAR(10)</c>、BT-44g 的 <c>NVARCHAR(6)</c>、
        // BT-43e 的 <c>NVARCHAR(40000)</c> 三颗各交回 1 行）。
        // <b>所以这一格今后不补宽度，而且这不是取向问题</b>：给 <c>CastText</c> 加一颗 <c>(n)</c> 等于把
        // "静默丢行"接进链路 —— 这一族其余四家的"补个宽度更保险"在这里是反的。判据与出处逐颗记 §13.5，
        // 后果与"哪家在 CAST 上会静默丢行"一起记 §14 的跨库差异表（它是新的一行，不并进现有任何一行）。
        [QuickFn.Key(QuickFn.CastText, 1)] = new("CAST({0} AS NVARCHAR)"),

        // 【产物】六颗 → v0.32 第二格改成"五颗跟产物、一颗跟真库"。这一家有它自己的 GETYEAR/GETMONTH/…
        // 一颗一个函数，而不是 Oracle 血统与金仓那两家的 EXTRACT(unit FROM x)，也不是 T-SQL 的 DATEPART(unit, x)。
        // 这一档在五家之间是<em>三形</em>：T-SQL 一形、EXTRACT 三家、GET* 一家 —— 第五家没有把形数加到四，
        // 而是自己开了一形。上一版设计稿这里取的是 EXTRACT（理由是"这一家像 Oracle"），交账前按本文件自己的
        // 出处判据改判 —— 产物发的就是这六颗，而"像哪一家"不是出处。
        // <b>【实测，五颗留在原形】</b>：E-04 六颗逐颗打在真列上，交回 <c>2026 / 5 / 6 / 7 / 8 / 9.456</c>，
        // 前五颗是 <c>Int32</c> 且值正确（<c>obj/logs/v32-recon-expr-5.log</c>）。
        // <b>而第六颗在这一跑里被驳回了 —— 本族唯一一处"产物在场而产物那一颗是错的"</b>：
        // <c>GETSECOND</c> 交回 <c>9.456</c> 一颗 <c>Double</c>，它<em>把小数秒带进了整数槽</em>
        // （E-05a/E-13b 两处逐字复现，字面量与真列同判）。同一时刻裸名 <c>SECOND(x)</c> 交回的才是
        // <c>9</c> 一颗 <c>Int32</c>（E-13a），而 C# 那一格 <c>DateTime.Second</c> 要的就是那颗 0..59。
        // <b>后果不是错值而是错行</b>：<c>Where(o => o.CreatedAt.Second == 9)</c> 在这一家会<em>漏掉</em>
        // 那一行（比的是 <c>9.456 = 9</c>），而它不报错、不红 —— 正是 §10 那一类"看着能用而语义少一截"，
        // 也是这份模板表上第一条"跟着产物就错"的实测。所以这一颗换形，而换的依据是<em>真列</em>那一档
        // （F-15 在字面量上早就量到 <c>SECOND</c> 交回 <c>59 &lt;Int32&gt;</c>，E-13a 补的是列上那一半）。
        // 顺带两颗一起记：<c>MINUTE(x)</c>/<c>HOUR(x)</c> 那一族裸名也在场且与前五颗同值（E-13d），
        // 所以这一家是"GET* 与裸名两形都有"，本表仍跟产物的 GET* 五颗（两形都给同一颗数时跟有出处那一支）；
        // 而 <c>SECOND</c> 打在只到日的 DATE 上给 <c>0</c> 不报错（E-13e），DATE 列上它是空操作那一格与
        // <see cref="Fn.ToDate"/> 同形，别读成"DATE 列上它坏了"。
        [QuickFn.Key(QuickFn.DateYear, 1)] = new("GETYEAR({0})"),
        [QuickFn.Key(QuickFn.DateMonth, 1)] = new("GETMONTH({0})"),
        [QuickFn.Key(QuickFn.DateDay, 1)] = new("GETDAY({0})"),
        [QuickFn.Key(QuickFn.DateHour, 1)] = new("GETHOUR({0})"),
        [QuickFn.Key(QuickFn.DateMinute, 1)] = new("GETMINUTE({0})"),
        [QuickFn.Key(QuickFn.DateSecond, 1)] = new("SECOND({0})"),

        // 【产物】：SYSDATE，而且产物里那句探活是 SELECT SYSDATE FROM DUAL —— 这一家<em>有</em> DUAL（S-04）。
        // 于是"这一家像 Oracle"这件事在两颗键上各成立一次，而日期加法那一格又把它推翻（见下面那六颗）。
        // 与另四家同一性质：它说的是"库那台机器上的此刻"而不是 CLR 进程的此刻，放进逐行表达式里整批同值。
        // <b>要记的那颗代价在 v0.32 第二格被实测驳回了</b>（原判："Oracle 那一族的 SYSDATE 只到秒，
        // 所以这一家若同形，取'此刻'会丢亚秒 —— 缺口记 §14"）：这一台的 SYSDATE 交回
        // <c>2026-09-24 21:35:26.4440000</c>（S-06），<em>带毫秒</em>，与 <see cref="XuguTypes"/> 那一列的
        // <c>DATETIME(3)</c> 正好对齐。于是 §14 那一行"SYSDATE 精度"在这一家不再是缺口而是<em>已闭合</em>，
        // 而它<em>仍然</em>是另两族（Oracle 血统那一族的秒档）的缺口 —— 一处实测只结一家一格，别跨族推广。
        [QuickFn.Key(QuickFn.Now, 0)] = new("SYSDATE"),

        // 【同形·本表推断最重的一格】：这颗函数要<em>两颗</em>未证的东西同时成立（SYS_EXTRACT_UTC 与 SYSTIMESTAMP）。
        // 为什么仍然写而不是<em>删</em>这一颗键：删掉它会让所有含 DateTime.UtcNow 的链在这一家抛
        // UntranslatableExpressionException，而那条文案说的是"这一家没有这个函数" —— 那是一句比"这一格是推断"
        // 更不好核的假话（我们并不知道它没有）。两档代价都是抛或错，取"可指认的同形 + 逐位标注"这一档，
        // 并把这一颗单独列进 §14 那份"真库第一天最先要跑的表达式"。<b>那颗"最先要跑"在本版本格跑完了</b>：
        // E-06a/E-06b（<c>obj/logs/v32-recon-expr-3.log</c>）—— 两颗同时成立，本仓库发的那一句交回
        // <c>2026-09-24 13:55:50.6330000</c> 一颗 <c>DateTime</c>（且与本机此刻的差正好是那颗会话时区的
        // <c>GMT+08:00</c>，说明它做的确实是"取 UTC"而不是"取本地"）。
        // <b>而这一问顺带量到一颗旁边的事</b>：<c>SELECT SYSTIMESTAMP</c> 单发（E-06a）<em>红在驱动侧</em> ——
        // <c>System.OutTypeException:未支持的列转化类型</c>，不是 SQL 报错而是这台库把带时区的时刻型
        // 交给驱动时<em>没有对应的 CLR 型</em>。它给 <see cref="XuguTypes"/> 那颗 DateTimeOffset 的抛
        // 补上一颗独立旁证：<em>库侧那一型确实在场，缺的确实是绑定与读回那一路</em>，
        // 而这一格从此不再是"我们没见过那一路的证据"。
        [QuickFn.Key(QuickFn.UtcNow, 0)] = new("(SYS_EXTRACT_UTC(SYSTIMESTAMP))"),


        // <b>【实测改判，v0.32 第二格】六颗日期加法全部换形，而这一处是本版本族唯一一处"真库把产物的写法驳回了"。</b>
        // 原判【产物】：<c>DATE_ADD(x, INTERVAL ({1}) UNIT)</c> 逐字抄自两家已发布适配器，与 MySQL 血统同形，
        // 是整张表最反直觉的一格（一个有 DUAL、SYSDATE、NVL 的库，日期加法走 MySQL 那一形而不是 NUMTODSINTERVAL）。
        // <b>而这一台活库不接这一形</b>：六颗单位逐行交回 <c>E19260 syntax error, unexpected _YEAR,
        // expecting ',' or ')'</c>（E-07，<c>obj/logs/v32-recon-expr-3.log</c>）—— 解析器在 <c>INTERVAL</c>
        // 之后不接受那颗括号。往回退一步问出来的形状是（E-07b/E-11，同一份日志）：
        // <list type="bullet">
        // <item><c>DATE_ADD(x, INTERVAL 2 DAY)</c> 绿、<c>INTERVAL '2' DAY</c> 绿，但乘数一旦是
        // <em>表达式</em>（<c>1 + 1</c>）或<em>绑定参数</em>（<c>:n</c>）或<em>列名</em>就红 —— 也就是
        // 这一格救不回本仓库要的那个槽位：{1} 在链式里就是一颗任意表达式。</item>
        // <item><c>x + NUMTODSINTERVAL({1}, 'DAY')</c> 四档全绿：裸数字（E-11b）、绑定参数（E-11c）、
        // 表达式 <c>1 + 1</c>（E-11d）、真列（E-11e），而 <c>HOUR</c>/<c>MINUTE</c>/<c>SECOND</c> 三颗掩码
        // 各自单独绿（E-11f 一颗一句）。结果读回 <c>DateTime</c>，DATE 上加两天也还在 DATE 那一档（E-12b）。</item>
        // <item>年与月只有 <c>ADD_MONTHS</c> 这一颗接得住（区间换算没有"月"这一档）：三档同样全绿
        // （E-11g/h/i），打在真 DATETIME 列上时分秒原样保留（E-12d 读出 <c>2026-06-06 07:08:09.456</c>），
        // 年那一档取 <c>ADD_MONTHS(x, (n) * 12)</c> —— 乘号在外面，所以那颗括号仍然保得住表达式。</item>
        // </list>
        // <b>取向因此翻了一格</b>：这一族在日期加法上不再是"MySQL 血统那一形"，而是<em>Oracle 血统那一形</em>
        // （<c>NUMTODSINTERVAL</c> / <c>ADD_MONTHS</c> 都是那一族的现成件），于是类注释第二段那句
        // "每格各归一家"更准了：<em>归哪一家由这台库说了算，不由产物说了算</em>。产物那两本的写法今天
        // 只能读成"它们对的是别的实例档位（兼容模式或版本）"或者"它们这一段本来就是坏的"。
        // <b>两支解释在这一版<em>分不开</em>，而分不开这件事本身是量出来的</b>【实测·不可问】：想问出这一台是
        // 哪一档兼容，五颗候选全报"不存在"（<c>all_variables</c> / <c>props</c> / <c>all_props</c> /
        // <c>all_db_info</c> / <c>CHARSET()</c>，N-01c…f），<c>all_databases</c> 那一份里也没有兼容位那一格
        // —— 所以这一格留在 §14 那份清单上，标的是"问法没有"而不是"我们没有问"。
        [QuickFn.Key(QuickFn.AddYears, 2)] = new("ADD_MONTHS({0}, ({1}) * 12)"),
        [QuickFn.Key(QuickFn.AddMonths, 2)] = new("ADD_MONTHS({0}, {1})"),
        [QuickFn.Key(QuickFn.AddDays, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'DAY'))"),
        [QuickFn.Key(QuickFn.AddHours, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'HOUR'))"),
        [QuickFn.Key(QuickFn.AddMinutes, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'MINUTE'))"),
        [QuickFn.Key(QuickFn.AddSeconds, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'SECOND'))"),

        // 原判【推断】+【无出处】，v0.32 第二格改判【实测】：本仓库发的裸形六颗全部在场
        // （E-01a/b 的 ROUND 两档、E-01c ABS、E-01d CEIL、E-01e FLOOR、E-01f POWER、E-01g SQRT，
        // 逐颗打在真列 AMOUNT 上，<c>obj/logs/v32-recon-expr-3.log</c>）。
        // <b>而那颗"要不要金仓那一层 CAST(… AS numeric) 包装"问出了一个反直觉的数</b>：
        // 包装形<em>也收</em>（E-01h 绿），可它<em>换了值</em> —— <c>ROUND("AMOUNT", 2)</c> 交回
        // <c>"12.35"</c>，<c>ROUND(CAST("AMOUNT" AS numeric), 2)</c> 交回 <c>"12"</c>。
        // 也就是这一家的裸 <c>numeric</c> 把标度折到 0，包装不是"多一层保险"而是<em>静默改值</em>（§5.8 禁的那一档）。
        // 金仓那一层在它的家族里是"不包就报错"，这一家如果跟了就是"包了少两位"，两处同形而后果相反 ——
        // 这一格进 §14 的跨库差异表，而这里取裸形这颗现在是<em>有据</em>的。
        // 另一颗顺带量到的读侧事实：ROUND/ABS/CEIL/FLOOR 的结果列经这台驱动读回是 <c>String</c>
        // （<c>"12.35"</c>），而 POWER/SQRT 是 <c>Double</c> —— 十进制的<em>表达式结果</em>不走 decimal 那一格，
        // 与本文件类注释第三段并排读（集成层今天没有一条链把 decimal 函数结果直接读进 DTO，所以它不红）。
        [QuickFn.Key(QuickFn.Round, 1)] = new("ROUND({0})"),
        [QuickFn.Key(QuickFn.Round, 2)] = new("ROUND({0}, {1})"),
        [QuickFn.Key(QuickFn.Abs, 1)] = new("ABS({0})"),

        // 【产物】：CEIL 逐字出现在产物那句 CEIL(GETMONTH(x)/3) 里 —— 而它是这一家写法的一枚指纹：
        // T-SQL 那一族叫 CEILING，另三家叫 CEIL，这一家跟着 CEIL 且与它的 Oracle 倾向一致。
        [QuickFn.Key(QuickFn.Ceiling, 1)] = new("CEIL({0})"),
        [QuickFn.Key(QuickFn.Floor, 1)] = new("FLOOR({0})"),
        [QuickFn.Key(QuickFn.Power, 2)] = new("POWER({0}, {1})"),
        [QuickFn.Key(QuickFn.Sqrt, 1)] = new("SQRT({0})"),

        // 原判【推断】+【无出处】两颗，v0.32 第二格都改判【实测】。原生 GREATEST/LEAST 在（F-07 交回
        // <c>G=2 | L=1</c>），四家里三家原生、只有 T-SQL 要 CASE 展开 —— 取多数派这颗赌赢了，而这一家
        // <em>仍然</em>没有产物出处，它的档位从"取多数派"升成"多数派且库认"。
        // <b>第二颗才是这一格真正的那个数</b>：NULL 传染落在哪一侧，F-07b 在常量上量到
        // <c>GREATEST(NULL, 2) = NULL</c> —— 与金仓/Oracle 那两家<em>同侧</em>（传染），与 MySQL 血统那一家相反。
        // 它是常量折叠，而探针纪律第一条刚说过常量会撒谎；这里敢用，是因为同一台库上被同一条路反复验证过的
        // 常量折叠有旁证（F-43d/F-43e 的小数逐字比 F-18 的中文比较都在常量上给出正确分岔），
        // 而这一格若翻，后果是"两形给不同的数"这种能一眼看出的事，不是静默少行。
        // 于是 §14 那张跨库差异表在这一家<em>加一个实例而不是新加一行</em>（与类注释第三段同一取向）。
        [QuickFn.Key(QuickFn.Greatest, 2)] = new("GREATEST({0}, {1})"),
        [QuickFn.Key(QuickFn.Least, 2)] = new("LEAST({0}, {1})"),

        // 【推断·偏离产物·第二颗】。产物在这一槽发的是 NVL({0}, {1})。这里不跟，理由与 Blank 同一句判据的
        // 另一次应用：NVL 只证"这一家有二参空值替换"，不证"这一家没有 COALESCE"，而共用层这一颗是
        // <em>任意元</em>的空值替换在 2 元处的投影（Fn 集里今天只有 /2 这一颗键，将来补 /3 时 NVL 就装不下了）。
        // 四家在这一格逐字同形，跟多数派的代价是零，跟产物的代价是把一颗键的语义<em>写窄</em>。
        [QuickFn.Key(QuickFn.Coalesce, 2)] = new("COALESCE({0}, {1})"),
        [QuickFn.Key(QuickFn.NullIf, 2)] = new("NULLIF({0}, {1})"),

        // 六颗聚合全取五家同形：这一家没有任何一颗聚合函数的产物出处（<c>WM_CONCAT</c> 有，而它是字符串聚合，
        // <b>共用层的 Fn 集里还没有那一颗符号</b> —— 于是"这一家有它的字符串聚合而契约上没有那一格"
        // 记进 §14 与 §16.14 那一批"先不补"的同一列）。
        [QuickFn.Key(QuickFn.Count, 1)] = new("COUNT({0})"),
        [QuickFn.Key(QuickFn.CountDistinct, 1)] = new("COUNT(DISTINCT {0})"),
        [QuickFn.Key(QuickFn.Sum, 1)] = new("SUM({0})"),
        [QuickFn.Key(QuickFn.Average, 1)] = new("AVG({0})"),
        [QuickFn.Key(QuickFn.Min, 1)] = new("MIN({0})"),
        [QuickFn.Key(QuickFn.Max, 1)] = new("MAX({0})"),
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
