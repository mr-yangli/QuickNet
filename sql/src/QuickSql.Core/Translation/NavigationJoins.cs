using System.Diagnostics.CodeAnalysis;
using QuickSql.Metadata;
using QuickSql.Query;
using QuickSql.Query.Model;
using QuickSql.Sql.Ast;

namespace QuickSql.Translation;

/// <summary>一条「按引用导航补进 FROM 的那张表」的事实（§5.2 的自动联表）。
/// <see cref="HostIndex"/> 用宿主表位在 FROM 序列里的<em>位置</em>当身份，而不是 <see cref="TableRef"/> 实例：
/// 同一个表位在派生出来的每段模型里确实是同一个对象，但两句模型之间"位置相同"才是那句 SQL 里读得出来的判据
/// （别名由位置生成，§5.1），拿实例相等当判据会把"位置对上了但对象是新造的那一份"这一形误判成没联过。</summary>
/// <param name="Member">宿主契约上那根引用导航的属性名。判同要它：<c>BillingCustomer</c> 与
/// <c>ShippingCustomer</c> 的宿主同一张、目标类型同一个，只差就是这个名字。</param>
internal readonly record struct NavigationJoin(int HostIndex, string Member, TableRef Table, JoinClause Clause);

/// <summary>引用导航读成员时补的那一条 LEFT JOIN（§5.2 的自动联表那一档）。
/// 这一颗是<em>每颗 lambda 一份</em>的翻译现场：<see cref="_joined"/> 是这条链上已经进 FROM 的那些
/// （只读，来自 <c>QueryEngine</c>），<see cref="_pending"/> 是这一颗 lambda 里新补的，等算子把它们落进模型。
///
/// <para><b>为什么发 LEFT 而不是 INNER</b>：这一档的来路是"读那条边的某一列"，而把主表行筛掉不是任何人
/// 要的意思 —— <c>o.Customer.City</c> 在 Customer 那一格空着的时候读出来的是 NULL，与 <c>Include</c>
/// 交回 <c>null</c> 导航同一件事。INNER 会让同一句投影在"外键为 NULL"和"外键指向已删的行"两种数据上
/// 少行，而少行在投影结果里读不出来。</para>
///
/// <para><b>为什么 ON 直接用 Include 那一份 <c>BuildJoinCondition</c></b>：一条导航怎么联，全仓库只许有一个答案
/// （外键列序 ↔ 目标主键列序那一格尤其脆，复合键错一位就是一句语法正确、结果错列的 SQL）。</para>
///
/// <para><b>同一根边不联第二张</b>：<c>Include(o =&gt; o.Customer)</c> 之后再读 <c>o.Customer.City</c>，
/// 这里<em>收养</em> Include 那一颗表位并把 JOIN 落进模型（<see cref="AdoptedInclude"/>），于是"一根边一张 JOIN"；
/// 反过来的那一序（先读导航、后 <c>Include</c>）由 <c>QueryEngine.Include</c> 收养已经落地的那一张。
/// 两个方向都要办，是因为"两张而不是复用"当初是按 Include 在前那一序裁的，判据是"复用会引用到一颗没进 FROM
/// 的表"——那一半今天由<em>收养时落成模型里的真 JOIN</em> 顶住（Include 那一条是编译期才决定的，模型那一条不是），
/// 剩下那一半只是贵，而贵在句子读得出来的时候就该省掉。</para>
///
/// <para><b>集合导航不在这里</b>：一根 <c>IEnumerable&lt;T&gt;</c> 边联进 FROM 会让主表行按明细行数重复，
/// 那是页码、行数、聚合三处一起改语义的事，仍归 <c>IncludeMany</c> 的两条查询（§5.7）。</para></summary>
/// <remarks>标注的理由与 <see cref="ExpressionTranslator"/> 同一份：它落在表达式翻译的调用链上，
/// 而那条链整条带 RUC/RDC（闭包常量求值按 <see cref="System.Reflection.FieldInfo"/> 读值）。</remarks>
[RequiresUnreferencedCode("按运行期 Type 向注册表取目标实体的契约。")]
[RequiresDynamicCode("目标实体的契约由注册表按 Type 解析（G2）。")]
internal sealed class NavigationJoinScout(IReadOnlyList<NavigationJoin> joined, int indexBase, Func<Type, int, TableRef> newTableRef,
    QueryModel model)
{
    /// <summary>欠着的这几张，<b>第一次补出联表时才新建</b>：绝大多数 lambda 一个导航都不读，
    /// 而这一颗是<em>每颗 lambda 一份</em>的，所以那只 List 的价钱要乘在整个读路径的算子次数上
    /// （§12.8 第 4 行「每谓词字节」钉的正是这种每算子常数项）。</summary>
    List<NavigationJoin>? _pending;

    /// <summary>这一颗 lambda 里补出来、还没进模型的那些。落模型由算子做，不是这里 ——
    /// <see cref="QueryModel"/> 是不可变的，只有手里握着 <c>Derive</c> 的那一层能决定"这一份新模型去哪儿"。</summary>
    internal IReadOnlyList<NavigationJoin> Pending => _pending ?? [];

    /// <summary>有没有欠账 = 有没有人读过引用导航，所以这一判不另付钱。</summary>
    internal bool HasPending => _pending is not null;

    /// <summary>把「<c>host</c> 上那根叫 <paramref name="member"/> 的引用导航」落成一张表位：
    /// 已经补过就交回同一张（链级那份 <c>joined</c> 与这一颗的 <see cref="_pending"/> 两处都要问，
    /// 后者是同一颗 lambda 里读了两次 <c>o.Customer.City</c> 那一形），没补过才新建表位 + 一条 LEFT JOIN。
    /// 找不到那条边时返回 null 而不是抛：调用方手里有三种"认不出"各自的理由（集合导航、未映射的计算成员、
    /// 目标实体没注册），而那些话在 <c>ExpressionTranslator.Navigation</c> 那一句里已经按类型分好了档。</summary>
    internal TableRef? Resolve(TableRef host, string member)
    {
        foreach (var join in joined)
            if (join.HostIndex == host.Index && join.Member == member)
                return join.Table;

        if (_pending is { } pending)
        {
            foreach (var join in pending)
                if (join.HostIndex == host.Index && join.Member == member)
                    return join.Table;
        }

        if (host.Contract is not { } contract
            || Relationship(contract, member) is not { } relationship)
            return null;

        // 这一根边 <c>Include</c> 已经登记过，就收养它那一颗表位（§5.6 与 §5.2 在同一根边上只留一张 JOIN）。
        // <b>收养的是表位，不是照抄 Include 那一条 JOIN</b>：这里落进 <see cref="_pending"/> 的那一条是
        // <em>模型里的真 JOIN</em>，而 Include 那一条只在"实体投影且没拆查"时才进这一句
        // （<c>QueryCompiler.UsesIncludeJoins</c>）—— 照抄过去，投影之后的 <c>Select</c>、<c>AsSplitQuery</c>
        // 与 count 自己包的那层内句都会把这一颗表位从 FROM 里摘掉，而欠着它的那句谓词还在，得到的是一句
        // 引用不存在表位的话。落成模型里的一张，三处派生句子都带着它，引用它的地方永远有着落。
        if (AdoptedInclude(host, member) is { } adopted)
        {
            (_pending ??= []).Add(adopted);

            return adopted.Table;
        }

        // 位置往后长：joined 那一份已经在 Tables 里，_pending 这一颗里的每一张也都将在，
        // 所以下一张的位置 = 进来时的表位数 + 已经欠着的那几张。
        var table = newTableRef(relationship.TargetType, indexBase + (_pending?.Count ?? 0));
        var clause = new JoinClause(JoinKind.Left, new SourceTable(table),
            QueryEngine.BuildJoinCondition(host, relationship, table));

        (_pending ??= []).Add(new NavigationJoin(host.Index, member, table, clause));

        return table;
    }

    /// <summary>宿主这一格的这根边是不是 <c>Include</c> 树里已经登记过的一根，是就把它折成一条待落地的
    /// <see cref="NavigationJoin"/>。<b>按<em>宿主表位的位置</em>配而不是按实例</b>，与
    /// <see cref="NavigationJoin.HostIndex"/> 那条纪律同一把尺（<c>Shard()</c> 会把根表位换成一个新实例，
    /// 按实例配在这一档就会漏认）。<c>ThenInclude</c> 的宿主是它父节点那一颗表位，所以树是一层一层往下走的。
    /// 只问 <see cref="QueryModel.Includes"/>：<c>IncludeMany</c> 那一家挂在
    /// <see cref="IncludeManyNode.References"/> 上、两侧索引天生是 0，它不进这一句的 FROM，收养不得。</summary>
    NavigationJoin? AdoptedInclude(TableRef host, string member)
        => model.Includes.Count == 0 || model.Plan is not EntityProjection entity
            ? null
            : FindIncludeEdge(model.Includes, model.Tables[entity.TableIndex], host, member);

    static NavigationJoin? FindIncludeEdge(IReadOnlyList<IncludeNode> nodes, TableRef parent, TableRef host, string member)
    {
        foreach (var node in nodes)
        {
            if (parent.Index == host.Index && node.NavigationMemberName == member)
                return new NavigationJoin(host.Index, member, node.TargetTable,
                    new JoinClause(JoinKind.Left, new SourceTable(node.TargetTable), node.OnCondition));

            if (FindIncludeEdge(node.Children, node.TargetTable, host, member) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>把欠着的那几条联表落进模型。<b>顺序 = 补出来的顺序</b>（<c>AddJoin</c> 一颗一颗追加），
    /// 于是同一颗 lambda 里 <c>o.Customer.City</c> 与 <c>o.Manager.City</c> 谁先出现，FROM 后面就谁先站。
    /// <b>收养来的那一张只加 JOIN、不再加表位</b>：那颗 <see cref="TableRef"/> 是 <c>AddInclude</c> 早就放进
    /// <see cref="QueryModel.Tables"/> 的同一颗，再放一遍 <c>Tables</c> 里就出现两个同 Index 的槽位，
    /// 而按位置绑形参的那一道（<c>Scope.Bound</c>）与往后长的别名编号都读的是这一份清单。</summary>
    internal QueryModel Flush(QueryModel model)
    {
        if (_pending is { } pending)
            foreach (var join in pending)
                model = Has(model.Tables, join.Table.Index)
                    ? model with { Joins = [.. model.Joins, join.Clause] }
                    : model.AddJoin(join.Clause, join.Table);

        return model;
    }

    static bool Has(IReadOnlyList<TableRef> tables, int index)
    {
        foreach (var table in tables)
            if (table.Index == index)
                return true;

        return false;
    }

    static RelationshipContract? Relationship(EntityContract contract, string member)
    {
        foreach (var relationship in contract.Relationships)
            if (relationship.MemberName == member)
                return relationship;

        return null;
    }
}
