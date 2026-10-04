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
