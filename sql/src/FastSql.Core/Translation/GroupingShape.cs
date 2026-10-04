using FastSql.Query.Model;
using FastSql.Sql.Ast;

namespace FastSql.Translation;

/// <summary>一条分组链在<em>翻译侧</em>的形状：分组来自哪张表、按哪些表达式分的组。
/// SQL 侧的真相（<c>GROUP BY</c> 的那几颗表达式）在 <see cref="QueryModel.GroupBy"/> 里，本记录不复述它们；
/// 它记的是「<c>g.Key</c> 该落回哪颗表达式」这一件翻译时才要用的事，所以它不进模型、也就不进
/// <see cref="FastSql.Query.Model.QueryModel.StableKey"/> 那套规范文本 ——
/// 同一条分组查询在 <c>Explain()</c> 里看到的文本，与分组键有没有被 <c>Select</c> 选出来无关。</summary>
/// <param name="Table">分组的那张表位。<c>g.Sum(o =&gt; o.Amount)</c> 里那个 <c>o</c> 就绑到它上面：
/// 组内元素本来就是这张表的行，所以聚合的实参是普通列，不需要相关子查询。</param>
/// <param name="Key">单列分组键的表达式（<c>g.Key</c> 的落点）。复合键时为 null。</param>
/// <param name="Members">复合键（<c>GroupBy(o =&gt; new { o.A, o.B })</c>）时按成员名索引的键表达式。
/// 判据是「<c>g.Key.A</c> 里那个 <c>A</c>」，所以它只到一层，且与 <see cref="Key"/> 互斥。</param>
internal readonly record struct GroupingShape(
    TableRef Table,
    SqlExpression? Key,
    IReadOnlyDictionary<string, SqlExpression>? Members)
{
    /// <summary><c>g.Key</c> 的落点。复合键在这里<em>不能</em>给一个值：那个匿名对象在库里没有任何对应物，
    /// 静默挑第一颗键列会让 <c>Select(g =&gt; new { K = g.Key })</c> 交回一列看起来对、其实少了一半条件的结果。</summary>
    public SqlExpression KeyOrThrow(string renderedLambda)
        => Key ?? throw new UntranslatableExpressionException(renderedLambda,
            "分组键是复合键（GroupBy 里给了 new { … }），g.Key 整体不是一个值：请选出具体成员 g.Key.<名字>");
}
