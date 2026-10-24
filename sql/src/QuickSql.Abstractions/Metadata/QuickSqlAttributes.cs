namespace QuickSql.Metadata;

/// <summary>注解必须能区分「没写」和「写了 false」，否则优先级链无法判定（§10.2）。
/// <c>bool</c> 表达不了这件事，所以自研属性一律用三态。</summary>
public enum TriState
{
    Unset = 0,
    Yes = 1,
    No = 2,
}

/// <summary>自研强属性层（§10.2 优先级 2）。名字、类型名、排除、主键、必填、长度、备注这些
/// 系统注解（<c>[Table]</c>、<c>[Column]</c>、<c>[NotMapped]</c>、<c>[Key]</c>、<c>[Required]</c>、
/// <c>[StringLength]/[MaxLength]</c>、<c>[Description]/[Display]</c>）接得住的语义一律不在这里重复；
/// 这一层只收 DataAnnotations 表达不了的语义：<see cref="QuickColumnAttribute"/> 的尺寸与三态、
/// <see cref="IndexAttribute"/> 的复合索引（EF 同形）、以及分表/集群/并发/级联那四颗。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class QuickColumnAttribute : Attribute
{
    /// <summary>字符串列的长度（应为多少个字符）。与 DataAnnotations 的
    /// <c>[StringLength]/[MaxLength]</c> 同格、但优先级更高（§10.2：自研层赢过注解层）。</summary>
    public int Length { get; set; }

    public int Precision { get; set; }

    public int Scale { get; set; }

    /// <summary>三态：「没写」「写了 nullable」「写了 not-null」。bool 表达不了这件事，
    /// 优先级链无法判定（§10.2）；<c>[Required]</c> 只有"非空"一个方向，补不上另一半。</summary>
    public TriState IsNullable { get; set; } = TriState.Unset;

    public TriState IsPrimary { get; set; } = TriState.Unset;

    /// <summary>No = 有列但不是数据库自增（客户端主键、序列由 <c>IKeyStrategy</c> 决定）。
    /// 这一格没有 DataAnnotations 等价注解 —— <c>[Key]</c> 只管"谁是键"，管不到"值谁给"。</summary>
    public TriState IsIdentity { get; set; } = TriState.Unset;
}

/// <summary>复合索引，形状与 EF Core 的 <c>[Index]</c> 同一版：点成员名、不给索引名 ——
/// 名字由约定生成（<c>IX_{表}_{列1}_{列2}</c>，<see cref="EntityContractFactory"/> 那一份实现），
/// 与 <c>[ForeignKey]</c> 靠约定命名同一种取向。"唯一"与 EF 同名（<see cref="IsUnique"/>）；
/// <see cref="Clustered"/> 与 <see cref="Descending"/> 是 DataAnnotations 里没有的存储档，留给 provider
/// 扩展用（EF Core 同语义的那格只住在 Fluent 侧）。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class IndexAttribute(params string[] memberNames) : Attribute
{
    public string[] MemberNames { get; } = memberNames;

    public bool IsUnique { get; set; }

    public bool Clustered { get; set; }

    public bool Descending { get; set; }
}

/// <summary>把这一张<em>逻辑</em>表声明成分表：系统的 <c>[Table(Name)]</c>（或类型名）此时给的是<b>带占位符的模板</b>，
/// 真正的表名按 <paramref name="kind"/> 从 <paramref name="field"/> 那一列的值算出来
/// （占位符清单与各自的适用档在 <see cref="SplitRouting"/>）。</summary>
/// <remarks><b>这一格不落 Fluent 等价写法</b>：Fluent 侧看不到它就得换一套校验时机，而分表最不能有的
/// 是"两条路径算出两种表名"。要按运行期上下文改表名（影子表、租户表那一类）走的是另一格 ——
/// 规格 §10.1 的 <c>TableRemap</c> 钩子，那颗今天仍未落地。</remarks>
/// <param name="kind">分片算法档。</param>
/// <param name="field">分片键的属性名。它必须是一列（被 <c>[NotMapped]</c> 排除掉就当场抛），
/// 时间档要日期类型，<see cref="SplitKind.Hash"/> 接受整数与字符串。</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SplitTableAttribute(SplitKind kind, string field) : Attribute
{
    public SplitKind Kind { get; } = kind;

    public string Field { get; } = field;

    /// <summary>仅 <see cref="SplitKind.Hash"/> 需要，其余档忽略它。少于 2 个桶等于没分。</summary>
    public int Buckets { get; set; }
}

/// <summary>把这一张逻辑表声明成"住在某座具名集群里"（读写分离与分库共用这一颗名字，
/// 规格 §16.4①）。<b>注解里只有集群名、没有连接串</b>：连接串归 <c>QuickSqlOrmOptions</c> 那三颗
/// （<c>ConnectionString</c> 是缺省集群的主库、<c>Replicas</c> 是它的从库、<c>Clusters</c> 是其余具名集群），
/// 所以这一颗注解可以进版本库而那条串不能。</summary>
/// <remarks>与 <see cref="SplitTableAttribute"/> 同一分工：不落 Fluent，因为"两条路径算出两种归属"
/// 在分库上是把语句发到另一座库里去，比表名算错更贵。<b>没写这颗注解的实体全都在 <c>main</c></b>，
/// 而 <c>main</c> 就是那条唯一的连接串 —— 今天的全部调用方行为一字不变。</remarks>
/// <param name="cluster">集群名。空或全空白当场抛（启动期）。显式写 <c>main</c> 与不写这一颗同义，
/// 而 <c>Clusters</c> 那一列里不许再出现 <c>main</c> —— 那一座的名字由 <c>ConnectionString</c> 占着。</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class DataSourceAttribute(string cluster) : Attribute
{
    public string Cluster { get; } = cluster;
}

/// <summary>乐观并发的比较列（§8.2、§10.1 的乐观锁那一档）。DataAnnotations 里没有等价的注解
/// （<c>KeyAttribute</c> 是身份、<c>TimestampAttribute</c> 在 EF Core 的程序集里），所以这一格落在自研属性层。</summary>
/// <remarks><b>它的语义是「只比较、不书写」，四件事同时成立</b>：这一列不进 <c>UPDATE</c> 的 <c>SET</c>、
/// 不进 INSERT 的列清单（值由库给：SQL Server 的 <c>rowversion</c>、别家的触发器或默认值），
/// 但进 <c>UPDATE</c>/<c>DELETE</c> 的 <c>WHERE</c>（比较的是<em>物化时拿到的那个值</em>）
/// 与 INSERT/UPDATE 之后的<b>回填集</b>（不回读就是「手里的值永远是旧的」，下一次保存必然假冲突）。
/// <para><b>「不书写」不等于「不参与脏判定」</b>：往这一列里写值是有人绕开了"值归库给"这条，必须现形，
/// 所以脏判定照比，比出差异就在发射前<em>整句拒掉</em>（而不是剔掉那一列后照发）。
/// 剔掉才是错的：那等于把调用方写进去的值静默丢弃，而静默丢弃查不出来。</para>
/// <para>由<em>调用方</em>自己递增的版本号（<c>int Version</c> 那一派写法）不属于这一格：那一列要进 <c>SET</c>，
/// 而「谁负责 +1」还没定，缺口与判据记在 §14 的 P1 余项。</para>
/// <para><b><c>AppSetsValue = true</c> 开出第三族</b>（应用给值：ABP 的 <c>string ConcurrencyStamp</c> 那一派）：
/// 那一列<em>进</em> <c>SET</c> 与 INSERT 列清单（写出去的是实体当前值），<c>WHERE</c> 仍比快照旧值，
/// 不回读、也不自动 +1 —— 新值由应用在保存前生成好。「谁给新值」在这一档是定了的（调用方给），
/// 所以 §14 那句「谁负责 +1 还没定」说的是整型自增那一族，不挡这一族。</para></remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConcurrencyTokenAttribute : Attribute
{
    /// <summary>默认构造：只比较不书写的那一族（库给值或整型自增）。</summary>
    public ConcurrencyTokenAttribute() => AppSetsValue = false;

    /// <summary>应用给值那一族（第三族）：新值由应用在保存前生成，列进 SET 与列清单，WHERE 仍比快照。</summary>
    public ConcurrencyTokenAttribute(bool appSetsValue) => AppSetsValue = appSetsValue;

    /// <summary>这一并发列的新值由应用给（写出去），而不是库给（回读）或 SET 自增。见特性备注里第三族。</summary>
    public bool AppSetsValue { get; }
}

/// <summary>关掉级联：这一颗实体（打在类上）或这一根集合导航（打在属性上）在 <c>Add</c>/<c>Remove</c>
/// 顺导航图带人时<em>不出边</em>，子实体要自己 <c>Add</c>/<c>Remove</c>。</summary>
/// <remarks><b>级联默认是开的</b>，所以这一颗只有退出用途，没有开启用途：它给的是「这张表的子行不归父行管」
/// 那一类（多对多的中间表、由别的流程维护的从属行、以及库里根本不该被父行带着走的集合）。</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public sealed class NoCascadeAttribute : Attribute;
