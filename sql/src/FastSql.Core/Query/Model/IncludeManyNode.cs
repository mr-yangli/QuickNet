namespace FastSql.Query.Model;

/// <summary>集合导航（to-many）的加载意图（§5.7）。与 <see cref="IncludeNode"/> 不同：
/// 不改写主查询 SQL，而是在主查询执行完后发第二条查询拿子实体，再在内存里缝回父实体。
/// FK 发现走约定（子实体上 <c>{父类型名}Id</c>）或子实体上指向父类型的 <c>RelationshipContract</c>。</summary>
public sealed class IncludeManyNode
{
    /// <summary>父实体上的集合属性名（如 "Items"）。</summary>
    public required string NavigationMemberName { get; init; }

    /// <summary>子实体的 CLR 类型。</summary>
    public required Type ChildType { get; init; }

    /// <summary>子实体上的 FK 列名（指向父实体 PK）。顺序与父实体 PK 列对齐。</summary>
    public required IReadOnlyList<string> ChildFkColumnNames { get; init; }
}
