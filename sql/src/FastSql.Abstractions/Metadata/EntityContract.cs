using System.Collections.Frozen;

namespace FastSql.Metadata;

/// <param name="Split">非 null 说的是<b>这一格是一个分表模板，不是一个能直接发出去的名字</b>：
/// <see cref="Name"/> 里带着 <c>{year}</c> 一类的占位符，要经 <see cref="SplitRouting"/> 换算成某一张
/// 物理表才能发射。发射口按这一格<em>在场与否</em>决定要不要报错，所以"忘了换算就把模板原样发出去"
/// 这种事故有一份结构性的兜底（<c>DialectBase.EmitTableName</c>）。</param>
public sealed record TableInfo(string? Schema, string Name, SplitFacts? Split = null);

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

    /// <summary>把<em>模板</em>契约绑到一张换算好的分片表上，供建片 DDL 用：列与关系两格原样带走，
    /// <b>索引那一格交回空的</b>。存在的理由是 <c>ICodeFirstDialect.EmitCreateTable</c> 收的是契约而不是表名，
    /// 而按范围建片要逐张发一句 <c>CREATE TABLE</c>；给它一颗口，省得 <c>SchemaSyncer</c> 自己复制一份
    /// 构造参数表——那份抄本会随契约的每一次加字段而漏改一处。</summary>
    /// <remarks><b>为什么索引不带过来</b>：索引名在 PG 与 Oracle 那一族是<em>模式内</em>唯一的（SQL Server 才是表内唯一），
    /// 同一个模板的十二张片各发一句 <c>CREATE INDEX IX_Order_UserId</c>，到第二张就撞。两条出路本家都不肯悄悄走：
    /// 按片改名会多出第六份命名口径（今天 <c>PK_</c> 已经跟着表名走，索引名却来自注解），
    /// 只给第一张发则是一句<em>看起来建好了</em>的假绿。所以这里不发，而 <c>SplitTablesResult.IndexesNotCreated</c>
    /// 每次都点名"模板上有几条索引没随片建"（规格 §16.4 记为已裁，随片索引的建档口径排在 P2）。</remarks>
    public EntityContract ForShardDdl(TableInfo shard)
        => new(ClrType, shard, Columns, [], Relationships);
}
