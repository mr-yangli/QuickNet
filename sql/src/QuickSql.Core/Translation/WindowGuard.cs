using QuickSql.Query.Model;
using QuickSql.Sql.Ast;

namespace QuickSql.Translation;

/// <summary>行号节点的<em>形状</em>问句：这一棵子树里有没有 OVER、以及落在不收它的那一格里没有。
///
/// <para>它住在 <see cref="ExpressionTranslator"/> 之外，为的是那颗类头标注：<c>ExpressionTranslator</c>
/// 整颗挂着 <c>RUC/RDC</c>（闭包常量求值要按 <c>MemberInfo</c> 读字段、退路是 <c>Compile()</c>），
/// 而这里两问只走 AST 的 record 分支，一颗反射都不碰。留在里面就等于让 <c>QueryCompiler</c> 与
/// <c>QueryEngine</c> 那几处干净的调用点各自补一句 <c>RequiresUnreferencedCode</c> ——
/// 那是把"这一层不需要 AOT 豁免"这件真话改口。</para>
///
/// <para><b>为什么通篇手写循环而不是 LINQ</b>：谓词位那一问挂在 <c>Predicate</c> 上，也就是挂在
/// <em>每一条谓词的翻译</em>上（全局过滤器注册的颗数还乘在外面），这一格不该在每次翻译里长出对象来 ——
/// 写法先例是 <c>DialectBase.RenderArguments</c> 那句"手写循环而不是 LINQ"。
/// <b>另有一句要说明白</b>：v41 门禁首场里每谓词 +8 B ∶ 一颗过滤器 +56 B 的涨幅<em>不是</em>这里量出来的 ——
/// 把这一格闸门整个摘掉再跑一场，六格读数一字不动（<c>obj/logs/v41-gate-3-control-nogate.log</c>），
/// 那笔账在别处。对照实验记在变更记录，别拿本段当它的出处。</para></summary>
internal static class WindowGuard
{
    /// <summary>这一棵子树里有没有行号节点。<b>子查询那三形不下钻</b>：内层句子的投影位是内层自己的事
    /// （带行号的子查询正是"按行号筛"那条出路），而内层自己的谓词位在它自己那条链上早就过了这一道闸门。</summary>
    internal static bool HasRowNumber(SqlExpression node)
    {
        switch (node)
        {
            case RowNumberExpression:
                return true;

            case SqlBinaryExpression binary:
                return HasRowNumber(binary.Left) || HasRowNumber(binary.Right);

            case SqlUnaryExpression unary:
                return HasRowNumber(unary.Operand);

            case FunctionExpression function:
                return HasRowNumber(function.Arguments);

            case CastExpression cast:
                return HasRowNumber(cast.Operand);

            case InExpression inList:
                return HasRowNumber(inList.Target) || HasRowNumber(inList.Values);

            case CaseExpression @case:
                if (@case.Operand is { } operand && HasRowNumber(operand))
                    return true;

                var whens = @case.Whens;

                for (var i = 0; i < whens.Count; i++)
                    if (HasRowNumber(whens[i].When) || HasRowNumber(whens[i].Then))
                        return true;

                return @case.Else is { } elseValue && HasRowNumber(elseValue);

            default:
                return false;
        }
    }

    /// <summary>清单里有没有行号节点。null 交回 false：IN 的值清单与分堆清单都是可空的那一型。</summary>
    static bool HasRowNumber(System.Collections.Generic.IReadOnlyList<SqlExpression>? nodes)
    {
        if (nodes is null)
            return false;

        for (var i = 0; i < nodes.Count; i++)
            if (HasRowNumber(nodes[i]))
                return true;

        return false;
    }

    /// <summary>投影计划里有没有行号节点：选列那一族各档都问（值 ∶ DTO 槽位 ∶ 聚合的参数位），
    /// 实体投影不可能有（它的选列由列契约展开）。三个调用点共用这一把（<c>QueryEngine</c> 的
    /// <c>Distinct</c>/<c>Project</c> 与 <c>QueryCompiler</c> 的跨片那一格）：尺子写三遍，
    /// 就会有哪一遍漏掉 <c>ObjectProjection</c> 那一族。</summary>
    internal static bool HasRowNumber(ProjectionPlan plan)
    {
        switch (plan)
        {
            case ValueProjection value:
                return HasRowNumber(value.Node);

            case AggregateProjection aggregate:
                return HasRowNumber(aggregate.Node);

            case ObjectProjection obj:
                var slots = obj.Slots;

                for (var i = 0; i < slots.Count; i++)
                    if (HasRowNumber(slots[i].Node))
                        return true;

                return false;

            default:
                return false;
        }
    }

    /// <summary>库的规矩：<c>OVER</c> 出的值是把所有行都算完才有的，所以它进不了 <c>WHERE</c> 与 <c>GROUP BY</c>，
    /// 也进不了聚合的参数位——十八家都拒。<b>这一问在组合时就判</b>（与 §5.6 那条"抛点在组合时、不在 ToSql 时"
    /// 同一条纪律）：发出去等库报错的话，报错里没有用户的调用点，而这一格的改写（内层那句交 <c>WithSql</c>）
    /// 是两句而不是一句。</summary>
    internal static void RejectRowNumber(SqlExpression node, string position)
    {
        if (!HasRowNumber(node))
            return;

        throw new UntranslatableExpressionException(node.ToString(),
            $"{position}里没有行号的落点：OVER 出的值要等全部行算完才有，库里那一格本来就不收它（十八家都拒）。" +
            "两条出路：把内层那句带行号的 SQL 交进 WithSql 那一段（外面那层的选列与条件仍归库）；" +
            "或整句走原生 SQL（文档站 14 篇）。");
    }
}
