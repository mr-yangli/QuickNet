using QuickSql.Metadata;

namespace QuickSql.Query.Model;

/// <summary>集合导航（to-many）的加载意图（§5.7）。与 <see cref="IncludeNode"/> 不同：
/// 不改写主查询 SQL，而是在主查询执行完后发第二条查询拿子实体，再在内存里缝回父实体。
/// FK 发现走约定（子实体上 <c>{父类型名}Id</c>）或子实体上指向父类型的 <c>RelationshipContract</c>。
/// 一层之下可以再挂一层（<c>ThenIncludeMany</c>，见 <see cref="Children"/>），同一层旁边还可以挂引用导航
/// （<c>ThenInclude</c>，见 <see cref="References"/>）。</summary>
public sealed class IncludeManyNode
{
    /// <summary>父实体上的集合属性名（如 "Items"）。</summary>
    public required string NavigationMemberName { get; init; }

    /// <summary>子实体的 CLR 类型。</summary>
    public required Type ChildType { get; init; }

    /// <summary>子实体上的 FK 列名（指向父实体 PK）。顺序与父实体 PK 列对齐。</summary>
    public required IReadOnlyList<string> ChildFkColumnNames { get; init; }

    /// <summary>子节点（<c>ThenIncludeMany</c> 链）：挂在<em>本节点子型</em>上的再一层集合导航。
    /// 与 <see cref="IncludeNode.Children"/> 同一棵树形态，区别在每一层各发一条 SELECT，
    /// 而父端对象就是上一层刚缝出来的那一批（<c>Children</c> 不进主查询，所以主查询 SQL 一个字符不变）。</summary>
    public List<IncludeManyNode> Children { get; } = [];

    /// <summary>挂在本节点子型上的<em>引用</em>导航（<c>ThenInclude</c>，§5.7）。形状与
    /// <c>AsSplitQuery()</c> 那一档的 <see cref="IncludeNode"/> 完全一样：执行层按
    /// <c>WHERE 目标.PK IN (这一批子实体的 FK 值)</c> 另发一条 SELECT 再逐只缝回，
    /// 所以主查询 SQL 同样一个字符不变。与 <see cref="Children"/> 分两格是因为父端不同：
    /// 那一格挂的是<em>子实体的子实体</em>（往下走一层），这一格挂的是<em>子实体手里的另一只实体</em>（往旁边走一根）。</summary>
    public List<IncludeNode> References { get; } = [];

    /// <summary><c>OrderByMany</c> 落在<em>本层子行</em>上的序（§5.7 的子行排序口）。执行层把它灌进这一层
    /// 那条子 SELECT 的 <see cref="Sql.Ast.SelectStatement.OrderBy"/> 槽 —— <b>发射在库里，不在内存里</b>：
    /// 缝完再排只能在应用进程里付一次全量拷贝，而"每父取前 N"那一族句子离开 ORDER BY 根本发不出来。
    /// 空清单 = 没点过这颗口，那一档子 SELECT 与这颗口存在之前<em>逐字节同形</em>（方言对空序不发
    /// <c>ORDER BY</c>）。每一层各拿各的清单：父节点排父层子行，挂在 <see cref="Children"/> 上的那一层
    /// 排它自己的子行，一颗口不隔层生效。</summary>
    public List<IncludeManyOrdering> Orderings { get; } = [];
}

/// <summary>子行序的一档：<b>存的是接链那一刻就在子实体契约上认好的列</b>（<c>OrderByMany</c> 的认列
/// 与链级 <c>OrderBy(string)</c> 共用同一颗咽喉，认不出当场抛），而不是表达式本身。理由是这两笔账：
/// 其一，报错要响在<em>接上链的那一刻</em>而不是执行期缝子行时才发现排不出去（§5.8 的"响在书写处"纪律）；
/// 其二，执行层那条子 SELECT 每次执行都要现造 AST，让它拿着现成的 <see cref="ColumnContract"/> 直发，
/// 不必每跑一次就重认一遍名字。</summary>
/// <param name="Column">子实体契约上的那一颗列。</param>
/// <param name="Descending">降序位。<c>OrderByMany</c> 收的是表达式，方向没有第二种来源，只能由这一颗形参给。</param>
public sealed record IncludeManyOrdering(ColumnContract Column, bool Descending);
