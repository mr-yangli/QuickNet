using System.Linq.Expressions;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Behaviors;

/// <summary>库里预置的写侧行为（§10.7 约束 3 的那一句「<c>Delete</c> 语句改写为 <c>UPDATE</c> 置位」的入口子）。
/// 今天只有一件：<see cref="SoftDelete{TEntity}"/>。组装根上的 <c>orm.Behaviors</c> 就是这一只，
/// 一个 orm 实例一份，与 <see cref="Query.Filters.FilterRegistry"/> 同一个姿势（注册在启动期做完，之后每条语句读它）。
///
/// <para><b>它与 <c>orm.Filters</c> 是两只对象，各管一半，缺一不可</b>：过滤器管「读到的集合里有没有已删的行」，
/// 行为管「<c>Delete</c> 落库里的是哪一句」。只注册过滤器不注册行为，<c>orm.Delete&lt;T&gt;()</c> 仍然把行删掉，
/// 而那一格 <c>deleted</c> 永远不会有人置位；只注册行为不注册过滤器，置位是置了，读侧照样把那几行读出来。
/// 规格 §10.7 那四句示例把两句并排写，就是这个意思。</para>
///
/// <para><b>改写落在 AST 层而不是 <c>CudBuilding</c> 的文本层，这是本版对约束 3 那句「发生在 <c>CudBuilding</c>」
/// 的一处改判</b>，判的是那句话的<em>目的</em>而不是字面：目的写在括号里 ——「不会出现日志里写着 DELETE、
/// 库里其实是 UPDATE」，也就是<em>日志与审计看到的必须是真正发出去的那一句</em>。在文本层做这件事，
/// 改写者要自己重做一遍发射器：本家 UPDATE 的语法（<c>TOP</c>、<c>FROM</c> 的位置、别名挂不挂得上）、
/// 参数前缀与 <c>ParameterValue</c> 的登记顺序、<c>WriteTargetAlias</c> 那一家「目标的自引用名是表名」的分岔
/// —— 五族各一份，而钩子的契约是「拿到已成文的整句」。在 AST 层做则这三件事一次都不必重做：
/// 改的是语句形状，发射仍由方言那一份 <c>Emit</c> 完成，改出来的文本照样要过 <c>ApplyCud</c> 与
/// <c>ApplyBinding</c>（所以调用方自己的 <c>CudBuilding</c> 订阅者仍能在这句之后接着改），
/// <c>Executed</c> 与 <c>AuditDiff</c> 拿到的仍是最终文本。约束 3 的<em>后果</em>逐字成立，机制的位置挪了一格。
/// v0.18 那一份文本改写机制没有被动过：它还在原处，<c>SqlServerCudBuildingTests</c> 那几条仍钉着它。</para>
///
/// <para><b>三处删除出口都接了这一只</b>：链式 <c>orm.Delete&lt;T&gt;()</c>、批量 <c>orm.DeleteRange&lt;T&gt;(rows)</c>
/// 与 <c>SaveChanges</c> 里被摘掉的那些行。三家必须一起接，因为「这张表是软删表」是一个<em>按实体</em>成立的事实，
/// 不是按调用姿势成立的 —— 只接一家会留下「同一张表，走 A 留住行、走 B 丢行」那个洞，
/// 而 §10.7 约束 1 反对批量绕过过滤器用的正是同一条理由。</para></summary>
public sealed class BehaviorRegistry
{
    readonly QuickSqlOrm _orm;

    /// <summary>与 <c>FilterRegistry</c> 同一条读写比例：注册一次、每条删除语句读一次，所以写侧加锁换整表、
    /// 读侧不进锁（<see cref="HasAny"/> 落在热位上，进锁就等于让没注册行为的项目在每次删除上付一次 monitor）。</summary>
    readonly Lock _gate = new();

    volatile Rule[] _items = [];

    internal BehaviorRegistry(QuickSqlOrm orm) => _orm = orm;

    /// <summary>有没有任何一个行为在册。<b>三处删除出口靠它在没人注册时整段短路</b>：不短路的话
    /// 「物理删除」那条老路径多付一次数组遍历与两列解析，而 §12.8 的写侧基线会为一条没有行为的链多记账。</summary>
    internal bool HasAny => _items.Length > 0;

    /// <summary>把 <typeparamref name="TEntity"/>（可以是接口或抽象基类）的删除改写成「置位那一格 + 记时间那一格」的
    /// UPDATE。<b>注册之后这条链上不再有物理删除</b>，而 <c>DeletePlan.Reason</c> 与 <c>Executed</c> 日志
    /// 都按改写后的那一句自陈。规格 §10.7 示例第四句的那一只。</summary>
    /// <param name="deleted">标记列的<em>读法</em>（<c>t =&gt; t.Deleted</c>），不是谓词。库往那一格里写的值恒为
    /// <c>true</c>，所以那一列必须是 <c>bool</c>；要写 <c>1</c> / <c>"Y"</c> 那一类标记就不是这一档（消息里给了退路）。
    /// 注册这一趟会把它拆出成员名，并对<b>具体类键</b>当场向契约认一遍那一列（列在不在、类型对不对、
    /// 是不是自增或并发列）—— 接口与抽象基类键认不了（它们没有列契约），那一档的失败点挪到第一次真贴上某张表时，
    /// 与 <see cref="Query.Filters.FilterRegistry"/> 的约束 4 欠的是同一半。</param>
    /// <param name="at">置位时间列（<c>t =&gt; t.DeletedAt</c>）。<c>DateTime</c> 与 <c>DateTime?</c> 两种列都认
    /// （lambda 在 <c>Func&lt;T,DateTime?&gt;</c> 那个目标类型下会为前者插一层转换，拆成员时一起剥掉）。
    /// null 就是只置位、不记时间。</param>
    /// <param name="clock">时间那一格的值从哪儿来。缺省本地时钟（<c>DateTime.Now</c>）—— 本库不替调用方决定
    /// 本地还是 UTC，要 UTC 就递 <c>() =&gt; DateTime.UtcNow</c>，测试要一颗能钉住的时间就递一个常量。
    /// <b>读它的时机是「构建那一句」而不是「注册那一句」</b>：注册期把值冻下来，等于后面所有删除记同一个时间，
    /// 与过滤器那句「闭包每次执行重新求值」同一条理由。</param>
    public BehaviorRegistry SoftDelete<TEntity>(
        Expression<Func<TEntity, bool>> deleted,
        Expression<Func<TEntity, DateTime?>>? at = null,
        Func<DateTime>? clock = null) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(deleted);

        var key = typeof(TEntity);

        var rule = new Rule(
            key,
            MemberOf(deleted.Body, "deleted"),
            at is null ? null : MemberOf(at.Body, "at"),
            clock ?? LocalNow);

        // 具体类键在注册期就把两列各认一遍：认不出列、类型不对、撞上自增或并发列，都在这里响，
        // 不等第一次删。接口与抽象基类键<em>没有</em>契约可查（注册表也没有"列出全部实现类"那个口子），
        // 所以那一档把失败点挪到 Rewrite 里，与 FilterRegistry.Probe 欠的是同一半（§10.7 约束 4）。
        if (!key.IsInterface && !key.IsAbstract)
            rule.Columns(_orm.Schemas.Contract(key));

        lock (_gate)
        {
            // 同一把键注册两次当场拒。"两张册子对同一张表各说一种写法"时谁赢没有任何依据；
            // 而接口键与具体类键的重叠不在这一格里（那种重叠至少说得出"先注册的是哪一句"，见 RuleFor）。
            foreach (var existing in _items)
                if (existing.Key == key)
                    throw new QuickSqlException(
                        $"{key.Name} 的软删已经注册过一次（置位列 {existing.DeletedMember}，" +
                        (existing.AtMember is null ? "没有时间列" : $"时间列 {existing.AtMember}") +
                        "）：同一张表上注册两次不会后一次盖掉前一次，仍然生效的是第一次那一句。" +
                        "要换写法就换一只 orm 实例（册子挂在实例上），或者把两处合成一处。");

            _items = [.. _items, rule];
        }

        return this;
    }

    static DateTime LocalNow() => DateTime.Now;

    /// <summary>这一张表上生效的软删规则（按注册序取第一条匹配得上的，与过滤器的"注册序折叠"同一条纪律）。
    /// <b>匹配规则只有一句</b>：键 <c>IsAssignableFrom</c> 实体类型 ⇒ 按接口注册一次，所有实现类都带上。</summary>
    internal Rule? RuleFor(Type entity)
    {
        var items = _items;

        if (items.Length == 0)
            return null;

        foreach (var item in items)
            if (item.Key.IsAssignableFrom(entity))
                return item;

        return null;
    }

    /// <summary>取一个成员读法里的成员名。<b>只认 <c>参数.成员</c> 那一种形状</b>：这一档要回答的是"哪一列"，
    /// 不是"什么条件"，所以 <c>t =&gt; t.A.B</c>、<c>t =&gt; !t.Deleted</c>、方法调用都在注册期拒，
    /// 而不是翻成一个看不懂的列名。剥掉 <c>Convert</c> 那一层是为了让 <c>DateTime</c> 列配得上 <c>at:</c> 那颗
    /// <c>Func&lt;T,DateTime?&gt;</c> 形参（与 <c>UpdateEngine.ColumnOf</c> 同一条剥法）。</summary>
    static string MemberOf(Expression body, string which)
    {
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } widen)
            body = widen.Operand;

        if (body is not MemberExpression { Expression: ParameterExpression } member)
            throw new QuickSqlException(
                $"SoftDelete 的 {which} 那一格要的是一个成员的读法（形如 t => t.列名），当前写的是 {body}。" +
                "这一档不判条件、不算表达式，它只回答「置位哪一列、时间记在哪一列」这两件事。");

        return member.Member.Name;
    }

    /// <summary>一条注册。<b>内部类型</b>：与 <c>FilterRegistry.Registration</c> 同一个理由 ——
    /// 公开它等于公开「行为可以按运行期 Type 塞进来」那条路，而那条口要的是另一处决定。</summary>
    internal sealed record Rule(Type Key, string DeletedMember, string? AtMember, Func<DateTime> Clock)
    {
        /// <summary>把两格成员名落成那一张契约上的两列，并且把尺子量一遍。<b>注册期（具体类键）与每次构建
        /// 都走这一颗</b>：前者是启动期响，后者是「接口键只有贴上某张表才知道」的那一半。交出去的是
        /// <see cref="ColumnContract"/> 而不是名字，因为发射段要的正是契约里那一份（列名、可空性、绑定提示）。</summary>
        internal (ColumnContract Deleted, ColumnContract? At) Columns(EntityContract contract)
        {
            var deleted = Column(contract, DeletedMember, "置位");

            if (deleted.ClrType != typeof(bool))
                throw new QuickSqlException(
                    $"{contract.Table.Name} 的 {deleted.MemberName} 是 {deleted.ClrType.Name}，而软删往那一格里写的是 true：那一格必须是 bool 列。" +
                    "0/1 整数标记与 \"Y\"/\"N\" 字符标记不是这一档（值恒为 true，没有第二档可给），" +
                    "要么把列改成 bool，要么自己走 orm.Update<T>().Set(o => o.标记, 值).Where(...)，或者订一颗 CudBuilding 做文本改写。");

            if (AtMember is null)
                return (deleted, null);

            var at = Column(contract, AtMember, "时间");

            if (ReferenceEquals(at, deleted))
                throw new QuickSqlException(
                    $"{contract.Table.Name} 的 deleted 与 at 指的是同一列（{DeletedMember}）：一句 UPDATE 的 SET 里同一列出现两次，库里本来就执行不过。");

            if (Nullable.GetUnderlyingType(at.ClrType) != typeof(DateTime) && at.ClrType != typeof(DateTime))
                throw new QuickSqlException(
                    $"{contract.Table.Name} 的 {at.MemberName} 是 {at.ClrType.Name}，而那一格要写的是一个时间：at 只认 DateTime 或 DateTime? 列。" +
                    "要记字符串或数字时间戳就别给 at 这颗参数（只置位），或者自己走 orm.Update<T>().Set(...) 那一档。");

            return (deleted, at);
        }

        ColumnContract Column(EntityContract contract, string member, string which)
        {
            if (!contract.ByMember.TryGetValue(member, out var column))
                throw new QuickSqlException(
                    $"{contract.Table.Name} 没有名为 {member} 的列（{Key.Name} 上注册的软删把「{which}」那一格点的是它）：" +
                    "软删要写的两列都必须是映射进库的列，导航属性与 [NotMapped] 成员不算。" +
                    (Key.IsInterface || Key.IsAbstract
                        ? " 键是接口/基类，注册期查不出来（那一档没有列契约），只有贴上某张表时才知道 —— 这张就是查出来的那一张。"
                        : string.Empty));

            if (column.IsIdentity)
                throw new QuickSqlException(
                    $"{contract.Table.Name} 的自增列 {column.MemberName} 当不了「{which}」列：那一列不接受写（与 UPDATE 链的 Set 同一把尺子）。");

            if (column.IsConcurrencyToken)
                throw new QuickSqlException(
                    $"{contract.Table.Name} 的并发列 {column.MemberName} 只比较、不书写（§8.2）：把它当「{which}」列，产出的那句 UPDATE 在库里本来就执行不过。");

            return column;
        }

        /// <summary>那颗时钟。<b>同一批只取一次</b>由调用方管（三处删除出口各在批开始之前取这一颗）：
        /// 一次 <c>DeleteRange</c> 切成两句、两句各读一次时钟，会把同一批行记成两个删除时间。</summary>
        internal DateTime Stamp() => Clock();

        /// <summary>置了哪几列的名字。<b>两份读者共用这一颗</b>：<c>DeletePlan.Reason</c> 那句话要的是
        /// 一个连起来的字符串（"改了哪几格"），<c>SaveChangesOp.Columns</c> 要的是清单本身（那一格的口径是
        /// "这条语句自己点名了哪几列"）。与 <see cref="Rewrite"/> 各调一次 <see cref="Columns"/> 而不是缓存结果：
        /// 一次删除语句只构建一次，两格查表在这里不是热位，而缓存就要按 (规则, 契约) 记一份状态 ——
        /// 为省两次字典查找去养一张表不划算。</summary>
        internal IReadOnlyList<string> SetColumns(EntityContract contract)
        {
            var (deleted, at) = Columns(contract);

            return at is null ? [deleted.ColumnName] : [deleted.ColumnName, at.ColumnName];
        }

        internal string SetSummary(EntityContract contract)
            => string.Join("、", SetColumns(contract));

        /// <summary>那条 DELETE 的改写产物。<b>输入是已经叠完全局过滤器的 <c>WHERE</c></b>：软删不改条件，
        /// 只把「删掉这些行」换成「把这些行的那一格置位」，所以过滤器的命中范围在两版语句里是同一个。
        /// <paramref name="deletedAt"/> 是批级那颗时间，null 表示就地读时钟（单句出口用这一支，
        /// 批量与 <c>SaveChanges</c> 由调用方把同一颗传进来）。
        /// 两格值都走 <see cref="ParameterValue"/> 而不是 <c>InlineLiteral</c>：它们与调用方
        /// <c>Set(o =&gt; o.列, 值)</c> 写进去的东西同级，因此同样要过 <c>ParameterBinding</c> 那颗钩子，
        /// 也只有参数化才让五族的字面量形状不需要在这里各写一份。</summary>
        internal UpdateStatement Rewrite(
            EntityContract contract,
            TableRef table,
            SqlExpression where,
            DateTime? deletedAt = null,
            object? entity = null,
            Action<SoftDeleteValuesEvent>? extraValues = null)
        {
            var (deleted, at) = Columns(contract);

            List<SetClause> sets = [new SetClause(deleted, new ParameterValue(true, deleted))];

            if (at is not null)
                sets.Add(new SetClause(at, new ParameterValue(deletedAt ?? Clock(), at)));

            // 订阅者往上加的 SET 项（"谁删的"那一格是调用方的领域事实，库里替不了）。
            // 逐行触发：链式 Delete<T>().Where(...) 那一档没有行可给，那一档不触发（entity 为 null）。
            if (entity is not null && extraValues is { } hook)
            {
                var @event = new SoftDeleteValuesEvent(entity, contract.Table.Name);

                hook(@event);

                foreach (var (column, value) in @event.ExtraValues)
                {
                    // 与那两格同一把尺子：自增列不接受写，并发列只比较不书写。
                    if (column.IsIdentity)
                        throw new QuickSqlException(
                            $"{contract.Table.Name} 的自增列 {column.MemberName} 当不了软删的额外列：那一列不接受写（与 UPDATE 链的 Set 同一把尺子）。");

                    if (column.IsConcurrencyToken)
                        throw new QuickSqlException(
                            $"{contract.Table.Name} 的并发列 {column.MemberName} 只比较、不书写（§8.2）：把它当软删的额外列，产出的那句 UPDATE 在库里本来就执行不过。");

                    sets.Add(new SetClause(column, new ParameterValue(value, column)));
                }
            }

            return new UpdateStatement
            {
                Table = table,
                Sets = sets,
                Where = where,
            };
        }
    }
}
