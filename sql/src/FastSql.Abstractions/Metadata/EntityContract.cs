using System.Collections.Frozen;

namespace FastSql.Metadata;

public sealed record TableInfo(string? Schema, string Name);

/// <summary>一条引用导航（to-one 关系）的已解析事实。<b>只有依赖侧</b>：外键列就在这张表上，
/// 而主端那一半要另一份契约才算得出来（<see cref="TargetType"/> 是唯一的跨实体线索）。
/// 今天的消费者只有一个：§8.2 那句「先插被依赖的、再删依赖方」的写序。级联、缝合读取、
/// FK 约束文本都从这一格长出去，但都还没接线（§14）。</summary>
/// <param name="MemberNames">依赖侧的外键属性名，与 <see cref="ColumnContract.MemberName"/> 同一口径。</param>
/// <param name="ColumnNames">换算后的列名，顺序与 <paramref name="MemberNames"/> 对齐（复合外键才有多条）。</param>
public sealed record RelationshipContract(
    string MemberName,
    Type TargetType,
    IReadOnlyList<string> MemberNames,
    IReadOnlyList<string> ColumnNames)
{
    public required OriginTrace Origin { get; init; }
}

/// <summary>一个实体的解析结果。启动期一次性构建，之后不可变（§10.3）。</summary>
public sealed class EntityContract
{
    public EntityContract(Type clrType, TableInfo table, ColumnContract[] columns, IReadOnlyList<IndexContract>? indexes,
        IReadOnlyList<RelationshipContract> relationships)
    {
        ClrType = clrType;
        Table = table;
        Columns = columns;
        Indexes = indexes ?? [];
        Relationships = relationships;
        ByMember = columns.ToFrozenDictionary(c => c.MemberName, StringComparer.Ordinal);
        ByColumn = columns.ToFrozenDictionary(c => c.ColumnName, StringComparer.Ordinal);
        PrimaryKey = Array.FindAll(columns, c => c.IsPrimary);
    }

    /// <summary>非泛型路径（跟踪器、批量、物化委托）按它取读取器。契约自带 Type，
    /// 才不会出现「实体类型与列契约来自两处」的分叉。</summary>
    public Type ClrType { get; }

    public TableInfo Table { get; }

    /// <summary>顺序即物化与批量绑定时的列序，由 G1 生成代码依赖，不得按名字排序。</summary>
    public ColumnContract[] Columns { get; }

    public IReadOnlyList<IndexContract> Indexes { get; }

    /// <summary>引用导航解析出来的关系边（§8.2 的写序读它）。顺序 = 实体里的声明序，
    /// 与 <see cref="Columns"/> 同一纪律：两侧生成的是同一份顺序，对拍才比得出「谁漏了一条」。</summary>
    public IReadOnlyList<RelationshipContract> Relationships { get; }

    public IReadOnlyDictionary<string, ColumnContract> ByMember { get; }

    public IReadOnlyDictionary<string, ColumnContract> ByColumn { get; }

    public ColumnContract[] PrimaryKey { get; }

    public ColumnContract this[int ordinal] => Columns[ordinal];
}
