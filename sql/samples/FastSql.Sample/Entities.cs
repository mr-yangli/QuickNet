using FastSql.Metadata;

namespace FastSql.Sample;

// ── 实体定义 ────────────────────────────────────────────────────────────

/// <summary>客户：主表。</summary>
[Table("Customers")]
public class Customer
{
    public int Id { get; set; }

    [Column(Length = 64)]
    public string? Name { get; set; }

    [Column(Length = 32)]
    public string? City { get; set; }
}

/// <summary>订单：主表，同时是 OrderItem 的父表。</summary>
[Table("Orders")]
[Index("IX_Orders_CustomerId", nameof(CustomerId))]
public class Order
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    /// <summary>引用导航：建立 Order → Customer 的外键边，SaveChanges 会据此排写入先后。</summary>
    public Customer? Customer { get; set; }

    public decimal Amount { get; set; }

    public DateTime CreatedAt { get; set; }

    [Column(Length = 256)]
    public string? Note { get; set; }

    /// <summary>集合导航：今天不是关系边（§8.2），但形态上表达"一个订单有多行明细"。</summary>
    public List<OrderItem>? Items { get; set; }
}

/// <summary>订单明细：子表，外键 OrderId 指向 Order.Id。</summary>
[Table("OrderItems")]
public class OrderItem
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    /// <summary>引用导航：建立 OrderItem → Order 的外键边。</summary>
    public Order? Order { get; set; }

    [Column(Length = 32)]
    public string? Sku { get; set; }

    public int Quantity { get; set; }

    public decimal Price { get; set; }
}

/// <summary>分组查询的 DTO 投影目标（示例 §8.3）：分组链上的 <c>Select</c> 与不分组时用的是同一套投影翻译，
/// 所以具名类型、<c>init</c> 与构造器那几种形状在分组侧一样能写。</summary>
public class SkuTotal
{
    public int OrderId { get; set; }

    public string? Sku { get; set; }

    public int Units { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>原生 SQL 的报表行（示例 §9.1）。形状要求只有「公共无参构造 + 公共可写属性」这一条：
/// 手写 SQL 交回来的列序由那句 SQL 决定，所以这一族口按<em>列名对属性名</em>装配，不按位置。</summary>
public class CustomerReport
{
    public int CustomerId { get; set; }

    public string? Name { get; set; }

    public int Orders { get; set; }

    public decimal Total { get; set; }
}

/// <summary>构造器 DTO（示例 §9.6）：record 没有无参构造，原生口的名字装配不接它，
/// 所以那一条走低层口自己给行委托。</summary>
public record CustomerTop(int CustomerId, decimal Total);

// ── 分表（示例 §12）：两张靶子，一档时间、一档哈希 ──────────────────────
//
// 两件事先记在这里，因为它们决定了下面那一段能写什么、不能写什么：
//   ① 类上没有 [Table] 时表名走约定，而这里<em>必须</em>写 [Table]，且写的是<em>带占位符的模板</em>
//      （`{year}{month}` / `{shard}`）—— 类型名从此不是表名，一句 DML 拿到模板名是要抛的。
//   ② 主键故意是 Guid 而不是自增 int：两张片各自发号能撞出同一个数，而"什么时候撞"要到合并统计
//      才看得见，所以分表实体上自增主键是契约层就禁掉的（示例因此不演示那一种写法）。

/// <summary>访问日志：按<em>月</em>分片的靶子，分片键是 <see cref="OccurredAt"/>。</summary>
[Table("AccessLog_{year}{month}")]
[SplitTable(SplitKind.Month, nameof(OccurredAt))]
[Index("IX_AccessLog_Status", nameof(Status))]
public class AccessLog
{
    public Guid Id { get; set; }

    [Column(Length = 128)]
    public string? Path { get; set; }

    public int Status { get; set; }

    /// <summary>分片键。它同时是一列 —— 契约层要求它映射得到，指向一颗没映射的属性当场抛。</summary>
    public DateTime OccurredAt { get; set; }
}

/// <summary>工单：按 <see cref="TenantId"/> <em>哈希取模</em>分四个桶的靶子。</summary>
[Table("TicketRow_{shard}")]
[SplitTable(SplitKind.Hash, nameof(TenantId), Buckets = 4)]
public class TicketRow
{
    public Guid Id { get; set; }

    /// <summary>分片键。整数直接取模，不经 <c>GetHashCode()</c>（那个随进程变，会把同一行在两次运行里送到两张表）。</summary>
    public int TenantId { get; set; }

    [Column(Length = 64)]
    public string? Title { get; set; }
}

// ── 读写分离与分库（示例 §13）：一张声明在另一座库上的靶子 ────────────────

/// <summary>销售汇总行：类上那一句 <c>[DataSource("report")]</c> 把它钉在 report 那座集群上。
/// <b>这一句只说"住在哪座库"，不说"走哪条连接"</b>：写与事务落那座集群的主库，缺省读只在
/// <em>那座集群配了</em>从库时才走从库 —— 这里没给它配，所以它每一句都落自己的主库。
/// 名字必须与 <c>FastSqlOrmOptions.Clusters</c> 里那一条对上：对不上是启动期一句带在册清单的抛，
/// 而不是"到库里才发现表建在没人看的那座"。</summary>
[Table("SalesTotals")]
[DataSource("report")]
public class SalesTotal
{
    public int Id { get; set; }

    [Column(Length = 64)]
    public string? Metric { get; set; }

    public decimal Value { get; set; }
}
