using System.Collections.Frozen;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.MySqlBase;

/// <summary>C# 形状 → MySQL 系模板表（§5.8，第四家）。键与另四族同一套 <c>Fn.Key(符号, 元数)</c>，
/// 所以五家比的是同一批符号 —— 少一个键不是"少一条模板"，而是那一格在翻译器里抛
/// <c>UntranslatableExpressionException</c>，报错形状与"这一家没有这个函数"完全一样，得人再分一次。
/// 这一份的键集与金仓那一族<b>逐一对齐</b>（少一个键在五家对拍里就是这一族那四对各少一条可比项）。
///
/// <para><b>两处刻意与金仓那一族同形而不是"更地道的写法"</b>（同形不是巧合，是让跨库对读黄金样本时
/// 剩下的差异都真是差异）：<c>EXTRACT(year FROM x)</c> 与 <c>SUBSTRING(x FROM p FOR n)</c> 都是这一家
/// 认的标准语法，虽然它同时认 <c>YEAR(x)</c> 与 <c>SUBSTRING(x, p, n)</c>；<c>LOCALTIMESTAMP</c> 在
/// 这一家是一颗<b>不带括号</b>的函数而同 <c>NOW()</c> 同义。三处做不到同形：<c>TRIM</c>（那一家是 <c>BTRIM</c>）、
/// <c>UTC_TIMESTAMP()</c>（那一家没有 <c>AT TIME ZONE</c> 可用）、日期加法 <c>DATE_ADD(x, INTERVAL (n) YEAR)</c>
/// （那一家是 <c>x + n * INTERVAL '1 year'</c>，这一家没有"间隔乘一个数"）。</para>
///
/// <para><b>四处语义与落地另三家不逐字相同，是对拍查不出来的那种</b>（记在这里而不是藏在模板里）：
/// <list type="bullet">
/// <item><c>str.length</c> 落 <c>CHAR_LENGTH</c> 而不是 <c>LENGTH</c>：后者在 <c>utf8mb4</c> 下数的是<em>字节</em>，
/// 一个汉字算 3。选前者是因为契约那一格写的是"多少个字符"（§10.4 的第四行就是这一格）。
/// 与 T-SQL 的 <c>LEN</c> 仍然差一档：那一颗不计尾部空格，<c>CHAR_LENGTH</c> 计。</item>
/// <item><c>str.concat</c> 与 <c>math.greatest</c>/<c>math.least</c>：<b>任一实参为 NULL 则整式 NULL</b>。
/// 这一档这一家与 T-SQL 同边、与金仓那一族分岔（那两家的 <c>CONCAT</c>/<c>GREATEST</c> 跳过 NULL，
/// 除非全空）。同一条 C# 表达式在三家可以给出"一个值"与"NULL"两个答案，它进 §14 的跨库差异表。
/// <b>【v0.37 改判 —— 上面三行原文照抄在前，改判在后】</b>：那一段里 <c>GREATEST</c>/<c>LEAST</c> 那一半是对的
/// （一台 8.4.8 上 <c>GREATEST(NULL, 5)</c> ∶ <c>LEAST(NULL, 5)</c> 各交 <c>NULL</c>，同式不带 NULL 的那颗交 <c>5</c>
/// 【实测·P-16，出处 <c>obj/logs/v37-my-recon-3.log</c> P 段】），<c>CONCAT</c> 那一半<em>两处都不对</em>：
/// 一是这一家 <c>CONCAT</c> 确实传染 NULL（<c>CONCAT(NULL, '/尾')</c> 交 <c>NULL</c>【实测·P-08】，
/// 列上同一形【实测·P-07】），这一句量到了；错的是"<em>与 T-SQL 同边</em>"——
/// 共用层折出来的模板在 T-SQL 侧发的也是 <c>CONCAT</c> 函数而不是 <c>+</c>，而那一颗<em>跳过</em> NULL，
/// 两边恰恰<em>分岔</em>。写这一段时把 <c>+</c> 的语义安到了 <c>CONCAT</c> 头上，而 <c>CONCAT</c> 出现在这份文件里
/// 的唯一理由（<c>SqlServerValueTests</c> 那句"C# 把 null 当空串，T-SQL 的 + 会让整个表达式变 NULL：
/// 不折成 CONCAT 就是静默错结果"）在这一家<em>不成立</em>，于是折形照发、结果静默变 <c>NULL</c> ——
/// 见证是集成册那一颗红：<c>SET [Note] = [Note] + '/尾'</c> 在 <c>Note</c> 为 <c>NULL</c> 的行上
/// 交回 <c>"NULL"</c> 而契约要 <c>"/尾"</c>（<c>obj/logs/v37-my-int-1.log</c> 第一条红）。
/// 今天这一格改的是<em>模板</em>而不是台账：<c>CONCAT</c> 那两颗 <c>IFNULL</c> 包装与第七家那颗
/// <c>(COALESCE({0}, '') || COALESCE({1}, ''))</c> 是同一条处置的两个实例 ——
/// <b>"这一家的拼接原语传染 NULL"⇒ 逐颗实参自己包一层</b>，包装而不是换函数，
/// 因为 <see cref="ConcatOperator"/> 那一格交回的仍是 <c>CONCAT</c>（<c>XuguDialectTests</c> 反向钉着那颗）。
/// 两颗候选（<c>CONCAT_WS('', …)</c> 与这一形）在四组问句上给出同一批数【实测·P-09 ∶ P-10 三行同串、
/// P-11 ∶ P-11b 嵌套同串、P-12 ∶ P-12b 全 NULL 都给空串而<em>不是</em> NULL、P-13 ∶ P-13b 数值实参同为
/// <c>1.5000/尾</c>、P-26b ∶ P-27c 进 <c>UPDATE SET</c> 之后同一行都是 <c>/尾</c>】，
/// 取 <c>IFNULL</c> 那一支的理由就是上面那一句：它把"哪一颗实参被折成空串"写在文本里，
/// 而 <c>CONCAT_WS</c> 把同一件事藏在<em>分隔符位</em>那一颗常量里。</item>
/// <item><c>agg.average</c>：整数上的 <c>AVG</c> 返回 <c>DECIMAL</c> 不截断 —— 与金仓同边、与 T-SQL 分岔，
/// 于是同一个 DTO 槽位两家可以差在小数点后。同一格判断已在 §9.4 那一条里记过第二家。</item>
/// <item><c>math.round</c> 与 <c>Math.Round</c>：这一家（与落地另三家一样）half away from zero，而
/// <c>Math.Round(2.5)</c> 是 <c>2</c>（银行家舍入）。四家都这样，所以它是共用层的语义而不是方言分岔，
/// 但它是<em>四家一起</em>与 C# 不一致，只在第一家时容易被当成"这一家的毛病"
/// （第五家的舍入模式两档出处都没读到【无出处】，所以这句"都这样"没替它签字）。</item>
/// </list></para>
///
/// <para><c>LikeEscapeCharacter</c> 取 <c>~</c> 而<b>不是</b>另四家的 <c>\</c>，这是五家里唯一一处"转义符本身换字符"：
/// 共用的发射口只会把这颗字符原样夹进引号里（<c>DialectBase</c> 的 <c>ESCAPE '&lt;字符&gt;'</c>），
/// 而这一家默认模式下<b>反斜杠在字符串字面量里就是转义符</b>，于是 <c>ESCAPE '\'</c> 是一条未闭合的串。
/// 换字符两边都躲不掉：把那一颗改成 <c>'\\'</c> 要动 <see cref="IExpressionRules"/> 的形状（字符串而不是字符），
/// 那是接口契约，归定规格的人；这里取"换一颗在串内串外都没有歧义的字符"。<b>代价是可数的</b>：
/// 用户输入里含通配符的 <c>StartsWith</c>/<c>Contains</c> 会转义成 <c>a~%b</c> 而不是 <c>a\%b</c>，
/// 五家对读 LIKE 参数值时这一格不同 —— 那是"各家各按自己的转义符"的正常结果，不是缺陷。
/// 重开条件在 §14：谁接了 <c>sql_mode</c> 建模（那一颗 <c>NO_BACKSLASH_ESCAPES</c>）就可以把这一格换回 <c>\</c>。</para>
///
/// <para><see cref="SupportsRegex"/> 留 <see langword="false"/>：这一家确实有 <c>REGEXP</c>（8.0 起是 ICU 引擎，
/// 与 5.7 的 POSIX 实现在字符类上不等价），而<em>共用层没有任何一处消费这颗位</em>（五家同一格），
/// 所以它今天仍然只是给 <c>doctor</c> 的一句话。与另四家同一立场。</para></summary>
public sealed class MySqlExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    /// <summary>写 <c>||</c> 是错的：这一家默认把它当逻辑 <c>OR</c>（<c>PIPES_AS_CONCAT</c> 不在默认
    /// <c>sql_mode</c> 里），于是 <c>a || b</c> 在布尔上下文里恒真或恒假而不是拼接。今天这一格没有消费方
    /// （拼接一律走 <c>Fn.Key(Fn.Concat, 2)</c>），所以它是台账上的一个 <c>CONCAT</c> 而不是产出差异 ——
    /// 记成"如果哪天有人接它，这一家给的必须是函数名"。
    /// <b>v0.37 补一颗读数</b>：这一段此前是推断，现在在一台 8.4.8 上量过了——同一张靶表上三颗问句
    /// <c>('a' || 'b')</c> ∶ <c>(1 || 0)</c> ∶ <c>('' || 'b')</c> 交回 <c>0</c> ∶ <c>1</c> ∶ <c>0</c>
    /// 【实测·P-15】，也就是两颗字符串都被折成数值 <c>0</c> 再进 <c>OR</c>（字符串→数值这一层是隐式的，
    /// 所以它比"恒假"更糟：<em>连实参是什么都不看</em>）。这一颗同时也钉死了上面那条改判的另一半：
    /// 拼接不能换成 <c>||</c>，与"CONCAT 要包 IFNULL"是同一格的两侧。</summary>
    public string ConcatOperator => "CONCAT";

    public char LikeEscapeCharacter => '~';

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
        // 本家的 CONCAT 传染 NULL【实测·P-07 ∶ P-08】，而共用层折成函数拼接的全部理由是
        // "C# 把 null 当空串"，所以这一族的模板必须逐颗实参自己包 —— 与第七家那两颗 COALESCE 同一处置。
        [Fn.Key(Fn.Concat, 2)] = new("CONCAT(IFNULL({0}, ''), IFNULL({1}, ''))"),

        // LENGTH 在这一家是字节数（utf8mb4 下一个汉字 3），量纲要跟着契约走（§10.4）。
        [Fn.Key(Fn.Length, 1)] = new("CHAR_LENGTH({0})"),
        [Fn.Key(Fn.Upper, 1)] = new("UPPER({0})"),
        [Fn.Key(Fn.Lower, 1)] = new("LOWER({0})"),
        [Fn.Key(Fn.Trim, 1)] = new("TRIM({0})"),
        [Fn.Key(Fn.TrimStart, 1)] = new("LTRIM({0})"),
        [Fn.Key(Fn.TrimEnd, 1)] = new("RTRIM({0})"),

        // 与金仓那一族同形（那一家的 TRIM 叫 BTRIM）。这里的 '' 是真·空串：
        // 与 Oracle 血统那一档正好相反，那一家只能写 (TRIM(x) IS NULL)，因为它的 '' 就是 NULL。
        [Fn.Key(Fn.Blank, 1)] = new("({0} IS NULL OR TRIM({0}) = '')"),

        // C# 的 Substring 是 0 基、这一家是 1 基，所以 +1 与另四家同一形（同形的理由见类注释第二段）。
        // 这一家同时认逗号形式的 SUBSTRING(x, p, n)，而"FROM/FOR 两支能吃多复杂的表达式"没有一台库能证：
        // 取标准那一支是"与金仓同形"与"少一处可指认的实测依据"之间的交易，记 §13.5。
        [Fn.Key(Fn.Substring, 2)] = new("SUBSTRING({0} FROM {1} + 1)", RequiresNonNegativeLength: true),
        [Fn.Key(Fn.Substring, 3)] = new("SUBSTRING({0} FROM {1} + 1 FOR {2})", RequiresNonNegativeLength: true),

        // 元数含接收者：REPLACE 三槽在 /3、LOCATE 两槽在 /2 —— 与另四家同一套键，抄错元数的后果
        // 在金仓那一族已经演示过一次（永远查不到，而不是渲染错）。
        [Fn.Key(Fn.Replace, 3)] = new("REPLACE({0}, {1}, {2})"),

        // LOCATE 是"子串在前、串在后"，与另四家那颗（CHARINDEX 子串在前 / STRPOS 与 INSTR 串在前）不同：
        // 这一家跟的是 STRPOS 的实参顺序而不是 CHARINDEX 的，所以模板里 {0} 与 {1} 要交换。
        // 返回值同样是 1 基且找不到给 0，-1 那一档换算与另四家一致。
        [Fn.Key(Fn.IndexOf, 2)] = new("(LOCATE({1}, {0}) - 1)"),
        [Fn.Key(Fn.ToDate, 1)] = new("CAST({0} AS date)"),
        [Fn.Key(Fn.CastText, 1)] = new("CAST({0} AS char)"),

        [Fn.Key(Fn.DateYear, 1)] = new("EXTRACT(year FROM {0})"),
        [Fn.Key(Fn.DateMonth, 1)] = new("EXTRACT(month FROM {0})"),
        [Fn.Key(Fn.DateDay, 1)] = new("EXTRACT(day FROM {0})"),
        [Fn.Key(Fn.DateHour, 1)] = new("EXTRACT(hour FROM {0})"),
        [Fn.Key(Fn.DateMinute, 1)] = new("EXTRACT(minute FROM {0})"),
        [Fn.Key(Fn.DateSecond, 1)] = new("EXTRACT(second FROM {0})"),

        // 与金仓那一族同形（这一家的 LOCALTIMESTAMP 不带括号，与 NOW() 同义）。
        // UTC 那一档换写法：这一家没有 AT TIME ZONE，但有原生的一颗 UTC_TIMESTAMP()，
        // 它给的是不带时区的 UTC 时刻，与落地另三家那一档同一个语义（第五家今天没有 UTC 那一颗的出处）。
        [Fn.Key(Fn.Now, 0)] = new("LOCALTIMESTAMP"),
        [Fn.Key(Fn.UtcNow, 0)] = new("UTC_TIMESTAMP()"),

        // 这一家有原生 DATE_ADD，但它不吃"间隔乘一个数"（那是 PG 系的形状），所以六档各写一颗单位。
        // 括号包着乘数：与金仓那一族同一条理由 —— {1} 若是表达式，没有它就变成 DATE_ADD(x, INTERVAL a - b YEAR)。
        [Fn.Key(Fn.AddYears, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) YEAR)"),
        [Fn.Key(Fn.AddMonths, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) MONTH)"),
        [Fn.Key(Fn.AddDays, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) DAY)"),
        [Fn.Key(Fn.AddHours, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) HOUR)"),
        [Fn.Key(Fn.AddMinutes, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) MINUTE)"),
        [Fn.Key(Fn.AddSeconds, 2)] = new("DATE_ADD({0}, INTERVAL ({1}) SECOND)"),

        // 不需要金仓那一层 CAST(… AS numeric)：那一层是"9.x 之前这些函数只吃 numeric"的版本原因，
        // 这一家的 ROUND/CEIL/FLOOR 从 Double 起就一直在。减一层包装会得到与那一族不同的文本，是真实分岔。
        [Fn.Key(Fn.Round, 1)] = new("ROUND({0})"),
        [Fn.Key(Fn.Round, 2)] = new("ROUND({0}, {1})"),
        [Fn.Key(Fn.Abs, 1)] = new("ABS({0})"),
        [Fn.Key(Fn.Ceiling, 1)] = new("CEIL({0})"),
        [Fn.Key(Fn.Floor, 1)] = new("FLOOR({0})"),
        [Fn.Key(Fn.Power, 2)] = new("POWER({0}, {1})"),
        [Fn.Key(Fn.Sqrt, 1)] = new("SQRT({0})"),

        // 原生 GREATEST/LEAST，与金仓同档（T-SQL 要用 CASE 展开）。NULL 传染那一档与那一家相反，见类注释。
        [Fn.Key(Fn.Greatest, 2)] = new("GREATEST({0}, {1})"),
        [Fn.Key(Fn.Least, 2)] = new("LEAST({0}, {1})"),
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
