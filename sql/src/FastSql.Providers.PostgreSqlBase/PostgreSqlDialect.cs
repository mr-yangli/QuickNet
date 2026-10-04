using FastSql.Metadata;
using FastSql.Sql;
using FastSql.Sql.Ast;

namespace FastSql.Providers.PostgreSqlBase;

/// <summary>PG 系的 SQL 文本层（§9.4）。与 T-SQL 的分岔逐条对照，每一条都是一个覆写点：
/// <list type="bullet">
/// <item>标识符双引号且大小写敏感（<see cref="IdentifierPolicy.PostgreSQL"/> 的 <c>CaseSensitive</c> 让每个
/// 标识符都带引号）—— 不带引号会被折成小写，于是 <c>"FastSqlOrder"</c> 与 <c>fastsqlorder</c> 是两张表。</item>
/// <item>分页 <c>LIMIT/OFFSET</c> <b>不要求</b> <c>ORDER BY</c>（§9.4 那三套实现里的第一套），
/// 所以 <see cref="PagingRequiresOrdering"/> 保持 false，共用层那句占位序在这里永远不触发。
/// <b>三套的套数到今天没变，变的是每套几家</b>：第四家（v0.30）与第五家（v0.31）都落进这一套，
/// 于是第一套从一家变三家，而"光写 <c>OFFSET</c> 也合法"这一格<b>仍然只有这一族</b> ——
/// 那两家各补一颗"无上限"哨兵，两颗数还不同源（<c>2^64−1</c> 对厂商写死的 <c>2147483647</c>），
/// 分布与出处见 <c>MySqlDialect.NoLimitSentinel</c> 与 <c>XuguDialect.NoLimitSentinel</c>。</item>
/// <item>布尔是真类型：谓词位置写 <c> IS TRUE</c> 而不是 <c> = 1</c>（§10.5 那一类事故的源头）。
/// 这一格从 v0.23 的"一家对两家"一路走到 v0.31 的<b>五家里的一家对四家</b>：Oracle 血统、MySQL 血统与
/// 第五家都落在 <c> = 1</c> 那一边（那两家的布尔列是 <c>NUMBER(1)</c> 与 <c>TINYINT</c>，第五家是
/// <c>TINYINT</c> 而库里另有真 <c>BOOLEAN</c> 型 —— 它仍取 <c> = 1</c> 的理由是三处一致，
/// 见 <c>XuguValues</c> 类注释第二条），所以"多数派在这一格不可信"从这句起是四条证据而不是一条。</item>
/// <item><c>RETURNING</c> 在整句末尾（<see cref="DialectBase.ReturningPrecedesValues"/> 的默认值就是这一家）。
/// v0.31 的第五家站同一边，于是"末尾形"从孤例变成两家，而<em>前置形</em>（T-SQL 的 <c>OUTPUT</c>）仍然是孤例。</item>
/// <item><c>UPDATE … FROM</c> 与 <c>DELETE … USING</c> 只吃<b>逗号分隔的表源</b>：连着目标表的
/// <c>JOIN … ON</c> 在这一家不合法，所以连接条件要折进 <c>WHERE</c>（覆写点在
/// <see cref="EmitJoinedSources"/>，只对 INNER 折得动）。</item>
/// <item>空集合的恒假谓词是 <c>false</c> 而不是 <c>1 = 0</c>：这一家的 <c>WHERE</c> 要求布尔表达式，
/// 而 <c>1 = 0</c> 恰好也合法，于是写错了不会报错 —— 那种"两家都过而只有一家对"的形状必须留在方言里，
/// 不能靠人记。（"都过而只有一家对"这一档到今天也没有第二种形状：五家里<em>只有这一族</em>改写了
/// <see cref="EmptySetPredicate"/> 与 <see cref="AlwaysTruePredicate"/>，其余四家都吃共用层的 <c>1 = 0</c> /
/// <c>1 = 1</c>，所以这一格是 1 ∶ 4，而 4 那一边是"没覆写"不是"不支持"。）</item>
/// </list>
/// 类不是 <c>sealed</c>：金仓/openGauss/瀚高在它上面只覆写真正分岔的那几处（§4 的能力继承链）。
/// <b>本类的产出文本没有在一台真库上跑过</b> —— 现场没有授权的 PG 系实例，见 §13.5 的出处台账。</summary>
public class PostgreSqlDialect(DbKind kind, PostgreSqlTypes types, PostgreSqlKeys keys, PostgreSqlValues values, PostgreSqlExpressions expressions)
    : DialectBase(IdentifierPolicy.PostgreSQL, values, expressions)
{
    public override DbKind Kind { get; } = kind;

    public override char ParameterPrefix => '@';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>恒假/恒真都写成布尔字面量。合并语句的守卫与空集合的 <c>IN ()</c> 特化都落在这里。
    /// <b>后半句在 v0.31 复核时被推翻了一半</b>（照录不改，判据换成可重数的那一句）：这两颗里今天只有
    /// 恒假那颗有调用点 —— <c>ExpressionTranslator.cs:456</c> 的空集合 <c>IN ()</c> 特化，1 处；
    /// <c>AlwaysTruePredicate</c> 在 Core 里<b>0 处消费</b>（<c>grep -rn AlwaysTruePredicate src/
    /// --include=*.cs | grep -v "///"</c> 只有接口声明、基类默认、这一行覆写三份）。"合并语句的守卫"
    /// 是 <c>ISqlDialect</c> 那一条<em>接口注释</em>里写的用途（"<c>IN</c> 全集与 <c>Any()</c> 空集合用"），
    /// 不是已接线的路径。五家同一格，缺口记 §14（v0.31 那一行）。</summary>
    public override string EmptySetPredicate => "false";

    public override string AlwaysTruePredicate => "true";

    protected override string DeleteJoinKeyword => " USING ";

    /// <summary><c> IS TRUE</c> / <c> IS FALSE</c>：可空布尔列上 <c>= FALSE</c> 匹配不到 NULL，
    /// 而 <c>IS FALSE</c> 也匹配不到 —— 两侧同义，但这一家的 <c>= 0</c> 是类型错误而不是三值逻辑问题，
    /// 所以换的是写法而不是语义（§10.5）。</summary>
    protected override void EmitBooleanEquality(SqlWriter writer, bool value)
        => writer.Append(value ? " IS TRUE" : " IS FALSE");

    /// <summary>转交给 <see cref="PostgreSqlKeys.EmitReturning"/>：主键回填那一族（<c>OUTPUT</c> / <c>RETURNING</c>）
    /// 在 T-SQL 归方言、在金仓这一系归 <c>IKeyStrategy</c>，因为它的形状与"怎么拿回自增值"是同一件事。
    /// 间距由调用方补，这里只落地那一段。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary><c> LIMIT n OFFSET m</c>：两个子句各自可缺省（§9.4 的第一套）。与 T-SQL 那一族的关键差别是
    /// <c>OFFSET</c> 不是 <c>FETCH</c> 的前置条件，所以缺省偏移不必落成常量 0，也不必补占位序。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is { } limit)
        {
            writer.Append(" LIMIT ");
            EmitExpression(limit, writer);
        }

        if (select.Offset is { } offset)
        {
            writer.Append(" OFFSET ");
            EmitExpression(offset, writer);
        }
    }

    /// <summary>目标表之外的表源用逗号并列，连接条件折进 <c>WHERE</c>（判据与"为什么只有 INNER 折得动"
    /// 在 <see cref="DialectBase.EmitJoinedSources"/> 那一格）。</summary>
    protected override void EmitJoinedSources(SqlWriter writer, string keyword, IReadOnlyList<JoinClause> joins, out SqlExpression? folded)
    {
        folded = null;

        for (var i = 0; i < joins.Count; i++)
        {
            var join = joins[i];

            if (join.Kind != JoinKind.Inner)
                throw new FastSqlException(
                    $"{Kind} 的 {keyword.Trim()} 子句不接受 {join.Kind.ToString().ToUpperInvariant()} JOIN：这一家只吃逗号分隔的表源，"
                    + "连接条件要并进 WHERE，而外连接那样做会把未匹配行整个筛掉（语义就变了）。");

            if (join.On is not { } on)
                throw new FastSqlException($"{Kind} 的 {keyword.Trim()} 子句里 {join.Source.Ref.Alias} 缺少 ON 条件：折进 WHERE 需要它本体。");

            writer.Append(i == 0 ? keyword : ", ");
            EmitSource(join.Source, writer);

            folded = folded is null ? on : new SqlBinaryExpression(BinaryOperator.And, folded, on);
        }
    }
}
