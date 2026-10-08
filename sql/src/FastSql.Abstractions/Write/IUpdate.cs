using System.Linq.Expressions;
using FastSql.Aop;
using FastSql.Providers;
using FastSql.Sql;
using FastSql.Sql.Ast;

namespace FastSql.Write;

/// <summary>UPDATE 链（§5.4 写侧的第二条、§6 示例里那句 <c>orm.Update&lt;Item&gt;().Set(...)</c>）。
/// 与 <see cref="IInsert{T}"/> 同一种形状：算子只攒模型，出口才生成文本，
/// 所以 <c>ToSql()</c> 与紧随其后的 <c>ExecuteAffrowsAsync()</c> 是同一条语句。
/// <b>这一条链上没有自增主键那一档</b>：UPDATE 回填要的是<em>改后整行</em>，
/// 那是 <c>OUTPUT deleted/inserted</c> 的另一套形状，随 P1 的 <c>SaveChanges</c> 一起设计（§14）。</summary>
public interface IUpdate<T> where T : class
{
    /// <summary>把一列写成给定的值。<b>值走参数而不是字面量</b>（与插入链同一纪律），
    /// 所以 <c>Set(o =&gt; o.Note, "a'b")</c> 不可能拼出注入来。列由 lambda 决定，
    /// 因此改字段名/重命名之后编译就报错，而字符串列名的库要到运行期才发现写错了列。</summary>
    IUpdate<T> Set<TProp>(Expression<Func<T, TProp>> column, TProp value);

    /// <summary>把一列写成由同一行的列算出来的值：<c>Set(o =&gt; o.Amount, o =&gt; o.Amount + 1)</c>。
    /// <b>这是"绕开跟踪器"的那条路</b>（§8 的表达式自增）—— 读改写如果在应用侧做，
    /// 两个并发请求会互相覆盖，而 <c>SET [Amount] = [Amount] + @p</c> 在库里是原子的。</summary>
    IUpdate<T> Set<TProp>(Expression<Func<T, TProp>> column, Expression<Func<T, TProp>> value);

    /// <summary>条件。<b>可以叠加</b>（多次 <c>Where</c> 按 AND 短路折叠成一条链，与读侧 <c>QueryCompiler.Fold</c>
    /// 同一个形状），但<em>不能没有</em>：一次 <c>Where</c> 都不叠的 UPDATE 会在构建时抛
    /// （<see cref="Explain"/>/<see cref="ToSql"/>/<see cref="ExecuteAffrowsAsync"/> 三处同一句话），
    /// 因为"整表每一行改成同一个值"没有一种是有意的默认行为。要清全表就显式写 <c>.Where(o =&gt; true)</c>。<b>全局过滤器不算这里说的"条件"</b>（§10.7 那一格从 v0.26 起会往这一句的 <c>WHERE</c> 里叠条件，但它叠进去的是"你没资格改那些行"，不是"你要改这些行" —— 这道闸门判的是后者）。</summary>
    IUpdate<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>分表实体的 UPDATE 要打的那一片（§16.4）。<b>一次一片，不是范围</b>：一条 UPDATE 只有一个目标表，
    /// 给范围就要悄悄执行 N 条，而这条链的契约写的是「<see cref="ToSql"/> 与紧随其后的
    /// <see cref="ExecuteAffrowsAsync"/> 是同一条语句」。参数是<em>那一片里的一个日期</em>，它下整到本档的周期就是片名。
    /// <b>必须放在链首</b>：SET 与 WHERE 的列引用是翻译时绑到当时的目标上的，事后换片会让前缀停在模板名上。
    /// 不分表的实体上调它直接抛。</summary>
    IUpdate<T> SplitTable(DateTime at);

    /// <summary>分表实体的 UPDATE 要打的那一片（<see cref="FastSql.Metadata.SplitKind.Hash"/> 档）：给桶号，
    /// 越界的桶号抛。同一句"一次一片、必须放在链首"的纪律。</summary>
    IUpdate<T> SplitTable(int bucket);

    UpdatePlan Explain();

    string ToSql();

    string ToSql(out IReadOnlyList<AdoParameter> parameters);

    /// <summary>逐参内联字面量的那一份文本（给 DBA / 日志）。UPDATE 不分块，所以没有 <c>-- 块 n</c> 分节。</summary>
    string ToLogSql();

    /// <summary>受影响行数。<b>库里匹配到但值没变的行也算</b>（T-SQL 的 <c>@@ROWCOUNT</c> 就是谓词命中数），
    /// 所以"返回 0"只证明没匹配上，不证明没改到东西 —— §12.4 第 2 项那条变更守卫（<c>IS DISTINCT FROM</c>）
    /// 是批量保存那一支的事，这里没接。</summary>
    Task<int> ExecuteAffrowsAsync(CancellationToken ct = default);
}

/// <summary>一次 UPDATE 的完整决策记录。与 <see cref="InsertPlan"/> 不同，这里没有分块：<b>一条 UPDATE 就是一条语句</b>，
/// 参数预算的账落在 <c>Where</c> 里的集合上（见 <see cref="UpdatePlan.Reason"/> 的那句提醒）。</summary>
public sealed record UpdatePlan(
    UpdateStatement Ast,
    DialectSql Sql,
    string Reason,
    IReadOnlyList<HookTrace> HookReplay)
{
    /// <summary>与 <see cref="InsertPlan.Provider"/> 同理：内联字面量要问那一家方言的 binder。</summary>
    public required IDbProvider Provider { get; init; }

    public string ToSql() => Sql.Text;

    public string ToLogSql() => Sql.ToLogSql(Provider);

    public override string ToString() => Reason + Environment.NewLine + ToLogSql();
}

/// <summary>DELETE 链（§5.4 写侧的第三条、§6 示例里那句 <c>orm.Delete&lt;Cart&gt;().Where(...)</c>）。
/// <b>这一句发出去的是 <c>DELETE</c> 还是 <c>UPDATE</c>，看这张表上有没有在册的软删行为</b>：
/// <c>orm.Behaviors.SoftDelete(...)</c>（§10.7 约束 3，v0.38 第十七笔）注册过的实体，这一句在 AST 层就被换成
/// 「置位那一格写 true + 时间那一格记一颗」的 UPDATE，<c>WHERE</c> 逐字不动；没注册的仍发 DELETE。
/// 两条都发得动这件事不许靠猜：<see cref="DeletePlan.Reason"/> 每一支都按<em>实际发出去的那一句</em>自陈
/// （三支：物理删除 / 本库软删改写 / 被调用方的 <c>CudBuilding</c> 改写过）。
/// <b>库里预置的那一颗只改形状，不占文本改写的口子</b>：调用方自己的 <c>CudBuilding</c> 订阅者看到的是
/// 改写后的整句文本，仍能接着改。
/// <b>这一句的 <c>WHERE</c> 从 v0.26 起可能不止调用方写的那几句</b>：<c>orm.Filters</c> 上在册的
/// 全局过滤器会叠进来（§10.7 约束 1 的写侧那一半），叠了几条也写在 <see cref="DeletePlan.Reason"/> 里。
/// 软删<em>不</em>往 WHERE 里加「且尚未置位」那一支，加了就要先有人裁"被挡住的行算不算一次并发冲突"（§14 的 P1 余项）。</summary>
public interface IDelete<T> where T : class
{
    /// <summary>条件。<b>不能没有</b>：一次 <c>Where</c> 都不叠的 DELETE 在构建时抛，理由同 UPDATE
    /// （整表清空不该是一个漏写条件的默认结果）。</summary>
    IDelete<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>分表实体的 DELETE 要打的那一片，口径与 <see cref="IUpdate{T}.SplitTable(DateTime)"/> 逐字相同：
    /// 一次一片（一条 DELETE 只有一个目标表），且必须放在链首。</summary>
    IDelete<T> SplitTable(DateTime at);

    /// <summary>分表实体的 DELETE 要打的那一片（哈希档）。</summary>
    IDelete<T> SplitTable(int bucket);

    DeletePlan Explain();

    string ToSql();

    string ToSql(out IReadOnlyList<AdoParameter> parameters);

    string ToLogSql();

    Task<int> ExecuteAffrowsAsync(CancellationToken ct = default);
}

/// <summary>一次 DELETE 的决策记录。<see cref="Reason"/> 必须自陈两件事：<b>最终文本落在"物理删除"、
/// "本库的软删改写"还是"已被 CudBuilding 改写"哪一支</b>，以及<b>这一句的 <c>WHERE</c> 里叠了几条全局过滤器</b> ——
/// 因为这条链在开了软删或多租户的项目里<em>看起来</em>应该产出 UPDATE、而且它的命中范围比调用方写的那几句窄。
/// <remarks><b><see cref="Ast"/> 是这条链<em>要的那一句</em>（永远是 DELETE），<see cref="Sql"/> 是<em>真正发出去的那一句</em>
/// —— 两者只在软删在册时不一致，而那一格是这一档的语义而不是 bug：调用方说的是"删掉这些行"，
/// 库里执行的是"把这些行的那一格置位"。<c>Reason</c> 必须把这一件事说明白，读日志的人才有能对得上库的那一句。
/// 要在改写后的形状上做文章（比如按列名核对置位对不对）请读 <c>Sql.Text</c> 或订 <c>CudBuilding</c>，
/// 而不是猜 <c>Ast</c> 有没有被换过。</remarks></summary>
public sealed record DeletePlan(
    DeleteStatement Ast,
    DialectSql Sql,
    string Reason,
    IReadOnlyList<HookTrace> HookReplay)
{
    public required IDbProvider Provider { get; init; }

    public string ToSql() => Sql.Text;

    public string ToLogSql() => Sql.ToLogSql(Provider);

    public override string ToString() => Reason + Environment.NewLine + ToLogSql();
}
