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
}
