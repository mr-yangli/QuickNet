using System.Collections.Frozen;
using FastSql.Providers;
using FastSql.Sql;

namespace FastSql.Providers.Gbase8s;

/// <summary>GBase 8s（Informix 血统）的函数表（§4 的 IExpressionRules，第六家）。
/// <b>这张表的"缺"与"有"同等重要，所以两半都逐条给理由</b>，而它比另五家多一道<em>本家特有的</em>约束：
///
/// <para><b>位置形占位符把模板的形状约束变成了一条硬判据</b>。本家文本里每一颗 <c>?</c> 都按
/// <em>出现次序</em>对应参数表里下一颗（<c>ParameterPrefix == '?'</c> 那一家没有名字可以对号），
/// 于是模板里 <c>{i}</c> 必须<b>恰好按 0,1,2… 各出现一次</b>。这条约束不是本家发明的——
/// 它是 <c>DialectBase.EmitFunction</c> 里那颗核对，而本家是它第一个走得到的家（另五家
/// <c>ParameterPrefix</c> 是 <c>@</c> 或 <c>:</c>，那颗核对恒不触发）。
/// 后果落在两类模板上，两类都在本家被挡下：
/// <list type="bullet">
/// <item><b>一颗实参用两次</b>：<c>str.blank</c> 在另五家是 <c>({0} IS NULL OR TRIM({0}) = '')</c>。
/// 带名字的方言里同一颗参数出现两次是免费的（按名回填），带 <c>?</c> 的方言里它就是"文本两颗、表里一颗"，
/// 值会绑定到隔壁列上 —— 那正是最贵的一类静默错。<b>所以本家不注册 <c>str.blank</c></b>：
/// <c>IsNullOrWhiteSpace(x)</c> 在这一家交 <c>UntranslatableExpressionException</c>。
/// 要接它有一条干净的路（按<em>文本序</em>把同一颗参数登记两次），而那一格在 SPI 里今天没有，
/// 它记 §14 与 §13.5 —— <b>这一格是"能力缺在形状而不在库"在本家的第一个实例</b>。</item>
/// <item><b>实参顺序与文本顺序不一致</b>：<c>date.addDays</c> 在本家是 <c>({0} + {1} UNITS DAY)</c>，
/// 恰好同序；而 T-SQL 的 <c>DATEADD(day, {1}, {0})</c> 是反的。<b>本家写不出反序形的那一类模板</b>，
/// 这是这一格的分岔长出的一支新判据（§9.1 那句"日期函数体系完全不同"到这里才落到一件可数的具体事上）。</item>
/// </list></para>
///
/// <para>不注册的其余各颗，逐颗给到"为什么不是一时遗漏"：
/// <list type="bullet">
/// <item><c>str.indexOf</c>：这一族的候选是 <c>INSTR</c>，而它<em>不是</em> Informix 的内建函数【文档·IBM】
/// （那是 Oracle 血统那一支）。<c>MATCHES</c> 谓词找得到"匹配不上"，给不出位置 —— 形状不对。
/// 宁可让 <c>IndexOf</c> 在本家不可翻译，也不发一颗库里没有的函数名。</item>
/// <item><c>cast.text</c>：这一家的 <c>CAST(x AS type)</c> 要一个<em>带型名的</em>目标型，而"无宽度的文本型"
/// 在这一家不存在（<c>VARCHAR</c> 与 <c>LVARCHAR</c> 的宽度都可配、也都有上限，<c>CLOB</c> 不是 CAST 的目标）
/// 【文档·IBM】。于是这一颗缺的不是函数而是<em>一个宽度数</em>，而 SPI 里没有任何一格能给出它 ——
/// 与 <c>Gbase8sTypes.String</c> 那句"不写列级上限"是同一件事的两半。</item>
/// <item><c>date.utcNow</c>：有出处而形状不对 —— <c>DBINFO('utc_current')</c> 交回的是<em>整数秒</em>
/// 而不是时刻值【文档·IBM】，把它变成 <c>DATETIME</c> 要 <c>UNITS SECOND</c> 那一族表达式再叠一颗基准。
/// 一条没有库见证的两级拼装不发（§1.2 原则 4）。</item>
/// <item><c>math.ceiling</c> / <c>math.floor</c> / <c>math.power</c> / <c>math.greatest</c> /
/// <c>math.least</c>：这几颗的名字在 IBM 手册的数学函数清单里本次<em>没有逐页回读</em>，
/// 而它们的形状与 Oracle 血统那一家最像（那里四颗全有）——"像"不是出处。整批不注册，
/// 等一台库或一份逐页读数。这一条是本文件与 <c>XuguExpressions</c> 最实的一处差距：
/// 那一家有两份 shipped 产物替它的每一行签字。</item>
/// <item><c>str.replace</c>：同上，取<em>不</em>注册。判据与上一条同一条，而它多一句代价：
/// <c>Replace</c> 在另五家全都有，所以本家是六家里唯一一颗，那条链在这里会红。</item>
/// </list>
/// <b>整张表今天没有任何一行在一台活库上跑过</b>，而且这一档比"未实测"还低半格：本文件里每一颗函数名
/// 都出自同一档（IBM Informix 手册）而未在本次逐页回读，标【文档·IBM】而不是【文档·厂商】——
/// 这一格差异是 §13.5 那本台账在本版新立的一档，写在 provider 类注释里。</para></summary>
public sealed class Gbase8sExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    /// <summary><c>||</c>【文档·IBM】。这一位与 <c>Fn.Concat/2</c> 那颗模板是同一条事实的两个出口，
    /// 而它在 <c>src/</c> 里<em>零消费者</em>（六家同一格，那一句判据在 <c>MySqlDialectTests</c> 里钉着），
    /// 所以这里填的不是行为而是台账。</summary>
    public string ConcatOperator => "||";

    /// <summary>反斜杠。共用层每次都显式发 <c>ESCAPE</c> 子句（<c>DialectBase</c> 那一颗），
    /// 所以这一位的真语义是"本家允许哪一个字符做转义符"而不是"库里默认是哪一个"【文档·IBM】。</summary>
    public char LikeEscapeCharacter => '\\';

    /// <summary>false。这一家找模式用的是 <c>MATCHES</c>，而它的通配形状（<c>*</c>、<c>()</c>、<c>|</c>）
    /// <b>不是 POSIX 正则</b>【文档·IBM】—— 与 §9.1 本行第四列那句"MATCH 正则"要连着读：
    /// 那一列写的是<em>有</em>模式匹配，没写它是正则。把 <c>SupportsRegex</c> 报真是让共用层
    /// 发一颗这一家不认的正则算子。</summary>
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

    /// <summary>键的取值集见类注释。<b>每一行的模板都过一遍"0,1,2… 各一次"这条自查</b>（本家特有）。
    /// 现场要补一颗函数时，注册进去的那一条也带着同一条约束 —— <see cref="Register"/> 在本家
    /// 不是"随便填一支模板"的口子，那一条纪律写在 <c>DialectBase.EmitFunction</c> 的抛里。</summary>
    static FrozenDictionary<string, FunctionMapping> Build() => new Dictionary<string, FunctionMapping>(StringComparer.Ordinal)
    {
        // 括起来是必需的：|| 在这一家的优先级与比较算子之间要靠括号钉住，
        // 而共用层把结果再拼进 WHERE 时不带自己的括号（§5.6 那一条优先级表在五家的形状不通用）。
        [Fn.Key(Fn.Concat, 2)] = new("({0} || {1})"),
        [Fn.Key(Fn.Length, 1)] = new("LENGTH({0})"),
        [Fn.Key(Fn.Upper, 1)] = new("UPPER({0})"),
        [Fn.Key(Fn.Lower, 1)] = new("LOWER({0})"),
        [Fn.Key(Fn.Trim, 1)] = new("TRIM({0})"),
        [Fn.Key(Fn.TrimStart, 1)] = new("LTRIM({0})"),
        [Fn.Key(Fn.TrimEnd, 1)] = new("RTRIM({0})"),

        // + 1：CLR 的 startIndex 从 0 起，SUBSTR 从 1 起（六家同一条换算）。
        // 两臂的实参编号都是 0,1(,2) 各出现一次且升序 —— 本家每一条模板都要过这一遍自查。
        [Fn.Key(Fn.Substring, 2)] = new("SUBSTR({0}, {1} + 1)", RequiresNonNegativeLength: true),
        [Fn.Key(Fn.Substring, 3)] = new("SUBSTR({0}, {1} + 1, {2})", RequiresNonNegativeLength: true),

        [Fn.Key(Fn.ToDate, 1)] = new("CAST({0} AS DATE)"),

        [Fn.Key(Fn.DateYear, 1)] = new("YEAR({0})"),
        [Fn.Key(Fn.DateMonth, 1)] = new("MONTH({0})"),
        [Fn.Key(Fn.DateDay, 1)] = new("DAY({0})"),

        // HOURS/MINUTES/SECONDS 是这一族的取值函数【文档·IBM】，而它们要求<em>带相应字段的</em>
        // DATETIME：本家 DDL 发的正是 YEAR TO FRACTION(3)（<c>Gbase8sTypes</c> 那一条），
        // 所以"能取到秒"这一格在自己的表上是齐的；库里别人建的 DATE 列上这三颗会红 ——
        // 那是契约与库不一致，不是翻译错。
        [Fn.Key(Fn.DateHour, 1)] = new("HOURS({0})"),
        [Fn.Key(Fn.DateMinute, 1)] = new("MINUTES({0})"),
        [Fn.Key(Fn.DateSecond, 1)] = new("SECONDS({0})"),

        // 句首括号是必需的：CURRENT 不带括号，而共用层会把它拼进算术里。
        [Fn.Key(Fn.Now, 0)] = new("(CURRENT YEAR TO SECOND)"),

        // UNITS 是本家的日期算术算子【文档·IBM】（另五家分成 DATEADD / INTERVAL / NUMTODSINTERVAL 三派，
        // 这里是第四派，而它是四派里唯一一颗"算子在前、单位在词尾"的）。
        [Fn.Key(Fn.AddYears, 2)] = new("ADD_MONTHS({0}, ({1}) * 12)"),
        [Fn.Key(Fn.AddMonths, 2)] = new("ADD_MONTHS({0}, {1})"),
        [Fn.Key(Fn.AddDays, 2)] = new("({0} + {1} UNITS DAY)"),
        [Fn.Key(Fn.AddHours, 2)] = new("({0} + {1} UNITS HOUR)"),
        [Fn.Key(Fn.AddMinutes, 2)] = new("({0} + {1} UNITS MINUTE)"),
        [Fn.Key(Fn.AddSeconds, 2)] = new("({0} + {1} UNITS SECOND)"),

        // 这一家的 ROUND 在手册里是双参的，所以一元那一臂补一颗字面量 0 而不是发 ROUND(x)
        // —— 与 <c>Fn.Key</c> 那段注释点的同一类错（少一个实参的模板会静默少一截）。
        [Fn.Key(Fn.Round, 1)] = new("ROUND({0}, 0)"),
        [Fn.Key(Fn.Round, 2)] = new("ROUND({0}, {1})"),
        [Fn.Key(Fn.Abs, 1)] = new("ABS({0})"),
        [Fn.Key(Fn.Sqrt, 1)] = new("SQRT({0})"),

        [Fn.Key(Fn.Coalesce, 2)] = new("COALESCE({0}, {1})"),
        [Fn.Key(Fn.NullIf, 2)] = new("NULLIF({0}, {1})"),

        [Fn.Key(Fn.Count, 1)] = new("COUNT({0})"),
        [Fn.Key(Fn.CountDistinct, 1)] = new("COUNT(DISTINCT {0})"),
        [Fn.Key(Fn.Sum, 1)] = new("SUM({0})"),
        [Fn.Key(Fn.Average, 1)] = new("AVG({0})"),
        [Fn.Key(Fn.Min, 1)] = new("MIN({0})"),
        [Fn.Key(Fn.Max, 1)] = new("MAX({0})"),
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
