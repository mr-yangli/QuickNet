using System.Collections.Frozen;

namespace QuickSql.Metadata;

/// <param name="Split">非 null 说的是<b>这一格是一个分表模板，不是一个能直接发出去的名字</b>：
/// <see cref="Name"/> 里带着 <c>{year}</c> 一类的占位符，要经 <see cref="SplitRouting"/> 换算成某一张
/// 物理表才能发射。发射口按这一格<em>在场与否</em>决定要不要报错，所以"忘了换算就把模板原样发出去"
/// 这种事故有一份结构性的兜底（<c>DialectBase.EmitTableName</c>）。</param>
/// <param name="Source">这一张表属于哪座具名集群（<see cref="DataSourceRouting"/>）。
/// <b>null 说的是"没声明"，不是"没有归属"</b>：没声明即 <see cref="DataSourceRouting.DefaultCluster"/>，
/// 而那一座就是 <c>QuickSqlOrmOptions.ConnectionString</c> 那条串。留着 null 而不是填上 <c>main</c>，
/// 为的是让"用户一个字没写"与"用户显式写了 main"在换算前后留得住区别。</param>
/// <param name="Comment">表备注，来自 <c>[Description]</c>（System.ComponentModel）或 Fluent 的 <c>HasComment</c>（优先级同表名那一条链）。
/// 挂源生成器那一条路径上还多一颗兜底：项目开着 <c>QuickSqlXmlDocComments</c> 时，两层都没写的那一格取类型上的
/// <c>/// &lt;summary&gt;</c>（反射侧没有这份来源，见 §13.11 的 <c>comment=</c> 那一格）。
/// <b>null = 没有任何一层说过</b>，与空串（说过而说的是"没有备注"）不是一回事：§9.5 的结构同步比的就是这一格，
/// 把两者折成一格会让"清掉备注"与"从来没写过备注"在计划里长同一个样。</param>
public sealed record TableInfo(
    string? Schema,
    string Name,
    SplitFacts? Split = null,
    DataSourceFacts? Source = null,
    string? Comment = null);

/// <summary>一条引用导航（to-one 关系）的已解析事实。<b>只有依赖侧</b>：外键列就在这张表上，
/// 而主端那一半要另一份契约才算得出来（<see cref="TargetType"/> 是唯一的跨实体线索）。
/// 消费者：<c>SaveChanges</c> 那句「先插被依赖的、再删依赖方」的写序、<c>Include</c> 的 JOIN、
/// 以及临时主键改写时那一格外键列。<b>级联不看这一格</b>，它看的是父端那根集合导航
/// （<see cref="EntityContract.Collections"/>）；FK 约束文本仍未接线（§14）。</summary>
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

/// <summary>一根<em>集合</em>导航（父端 → 子端）解析出来的事实，级联唯一的来源。与
/// <see cref="RelationshipContract"/> 分两格是因为外键住在不同那张表上：这一格的
/// <see cref="DeclaredForeignKeyMembers"/> 说的是<em>子端</em>的成员名，要换算成列得拿子端的契约，
/// 而子端的契约在这一格建立时未必已经存在（注册表按需解析），所以换算推迟到级联真要用那一刻，
/// 推不出就响（<c>ChangeTracker</c> 那一处）。</summary>
/// <param name="MemberName">父实体上那根集合导航的属性名，它自己不是列。</param>
/// <param name="ChildType">子端实体的 CLR 类型。</param>
/// <param name="DeclaredForeignKeyMembers"><c>[ForeignKey]</c> 打在集合导航上时给的那几个子端成员名；
/// <b>空 = 没有声明</b>，运行期按「子端的 to-one 边 → 约定 {父类型名}Id」两档推。</param>
/// <param name="NavigationReader">读父实体那一格集合位的委托（编译一次、级联每行只调用）。</param>
/// <param name="CascadeEnter">级联的入口那一格：<c>TChild</c> 在写这一格的代码里是编译期已知的，
/// 会话才不必 <c>MakeGenericType</c>（理由见 <see cref="QuickSql.Tracking.ICascadeSink"/>）。</param>
public sealed record CollectionEdgeContract(
    string MemberName,
    Type ChildType,
    IReadOnlyList<string> DeclaredForeignKeyMembers,
    Func<object, object?> NavigationReader,
    Action<QuickSql.Tracking.ICascadeSink, object> CascadeEnter)
{
    public required OriginTrace Origin { get; init; }

    /// <summary>级联开关，缺省开；<c>[NoCascade]</c> 折在这一格。</summary>
    public bool Cascade { get; init; } = true;
}

/// <summary>一个实体的解析结果。启动期一次性构建，之后不可变（§10.3）。</summary>
public sealed class EntityContract
{
    public EntityContract(Type clrType, TableInfo table, ColumnContract[] columns, IReadOnlyList<IndexContract>? indexes,
        IReadOnlyList<RelationshipContract> relationships, IReadOnlyList<CollectionEdgeContract>? collections = null)
    {
        ClrType = clrType;
        Table = table;
        Columns = columns;
        Indexes = indexes ?? [];
        Relationships = relationships;
        Collections = collections ?? [];
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

    /// <summary>集合导航解析出来的父端边（级联的来源，§8.2 那张状态机图上「顺着导航带人」那一条）。
    /// 顺序 = 实体里的声明序，与 <see cref="Columns"/> 同一纪律。<b>空 = 这个实体没有集合导航</b>，
    /// 于是级联那两步（<c>Add</c> 带人、<c>Remove</c> 删子行）一次都不跑。</summary>
    public IReadOnlyList<CollectionEdgeContract> Collections { get; }

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
        => new(ClrType, shard, Columns, [], Relationships, Collections);
}
