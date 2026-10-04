using FastSql.Metadata;
using Da = System.ComponentModel.DataAnnotations;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;

namespace FastSql.TestInfra;

/// <summary>订单：P0 读路径的靶子实体。<b>列序就是契约列序</b>（§10.3），物化按位置读，
/// 所以这里的属性声明顺序是测试的一部分，重排会让黄金样本整片变红 —— 那是故意的，
/// 因为"列序变了"在真实项目里就是一个要显式承认的变更。</summary>
[Table("FastSqlOrder")]
[Index("IX_FastSqlOrder_CreatedAt", nameof(CreatedAt))]
public class Order
{
    /// <summary>约定命中 <c>Id</c> ⇒ 主键 + 自增（整型主键才自动自增，§10.2 的约定层）。</summary>
    public int Id { get; set; }

    /// <summary>显式长度 ⇒ <c>nvarchar(64)</c>。不给长度的字符串是 <c>nvarchar(max)</c>（见 <see cref="Note"/>）。</summary>
    [Column(Length = 64)]
    public string? CustomerName { get; set; }

    public int CustomerId { get; set; }

    /// <summary><c>decimal(18,4)</c>：精度与标度都由 provider 兜底，跨库对齐时这里必须显式写。</summary>
    public decimal Amount { get; set; }

    /// <summary>枚举存 INT（§9.3）。放一个枚举列是为了让"驱动不会替我们转枚举"这条在绑定层被真的走到。</summary>
    public OrderState State { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>C# 可空 ⇒ 可空列（约定层）。<c>datetime2(3)</c> 而不是遗留 <c>datetime</c>（§9.3）。</summary>
    public DateTime? PaidAt { get; set; }

    public string? Note { get; set; }

    /// <summary>引用导航：不是列（一条 <c>WHERE CustomerId = ...</c> 里不会平白多出一个 JOIN 的出处），
    /// 但它<em>是一根关系边</em> —— 外键列按约定 <c>CustomerId</c> 命中 <see cref="CustomerId"/>，
    /// 于是这一格长在契约的 <c>Relationships</c> 上而不是 <c>Columns</c> 上（§8.2 的写序读它）。</summary>
    public Customer? Customer { get; set; }

    /// <summary>集合导航：形态上不可映射，今天也<em>不是</em>边 —— 一个 <c>Customer</c> 对应多少条 <c>Order</c>
    /// 由反方向的引用导航表达，这里两头都记不出先后（§10.3）。留一列<em>不</em>成为边，是这条判据的负样本。</summary>
    public List<OrderItem>? Items { get; set; }
}

public enum OrderState
{
    Draft = 0,
    Placed = 1,
    Paid = 2,
    Shipped = 3,
    Cancelled = 9,
}

[Table("FastSqlOrderItem")]
public class OrderItem
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    [Column(Length = 32)]
    public string? Sku { get; set; }

    public int Quantity { get; set; }

    public decimal Price { get; set; }
}

[Table("FastSqlCustomer")]
public class Customer
{
    public int Id { get; set; }

    [Column(Length = 64)]
    public string? Name { get; set; }

    [Column(Length = 32)]
    public string? City { get; set; }

    /// <summary>中文要能原样存进去：字符串默认 <c>nvarchar</c> 就是因为 <c>varchar</c> 的下场<b>看排序规则</b> ——
    /// 库的默认排序规则是 <c>SQL_Latin1_General_CP1_CI_AS</c>（代码页 1252）时中文会<b>静默写成 '?'</b>，
    /// 是 <c>Chinese_PRC_CI_AS</c>（936）时一个字节不丢、但每个汉字占 2 字节（§10.4 实测两行）。
    /// 两类实例的这个分叉现在由 <c>CollationTests</c> 断言住，不再是注释里的说法（§13.4）。</summary>
    [Column(Length = 32)]
    public string? Remark { get; set; }
}

/// <summary>主键不是自增列的实体（<c>Guid</c> 由调用方生成）。整条写路径上它是<b>唯一的负样本</b>：
/// 「没有自增列 ⇒ 列清单含主键、回填那一档被拒」这两条都只在它身上可测（§12.4 第 1 项的另一半）。</summary>
[Table("FastSqlSessionToken")]
public class SessionToken
{
    public Guid Id { get; set; }

    [Column(Length = 64)]
    public string? Value { get; set; }

    public DateTime ExpiresAt { get; set; }
}

/// <summary>构造器形态的 DTO（<c>ObjectProjection.ViaConstructor = true</c> 的那条分支）。
/// 只有属性、没有无参构造器，所以它只能走构造器组装 —— 分支选错会在运行时报"需要无参构造器"。</summary>
public sealed record OrderSummary(int Id, string? CustomerName, decimal Amount);

/// <summary>只用 DataAnnotations 的实体：走生成器的<b>第二条</b>流水线（<c>Schema.TableAttribute</c>），
/// 顺带把层序摆在一个现场里 —— 列名来自 DataAccessAnnotation 层（<c>carrier_code</c>）、非空来自
/// <c>[Required]</c>、长度来自 <c>[StringLength]</c>，而 <c>Weight</c> 那一列仍然落到「未声明，交给 provider 默认」。
/// <b><c>Weight</c> 是踩过之后留下来的</b>：Roslyn 默认的符号显示格式把 decimal 打成关键字 <c>decimal</c>，
/// 而可映射判定比的是全名，于是这一列会被当成导航属性<b>静默少掉</b>——两侧契约逐字段相等才把它抓住（§13.11）。</summary>
[DaSchema.Table("FastSqlLegacyShipment")]
public class LegacyShipment
{
    /// <summary><c>[Key]</c> 只说明主键；是否自增仍由「整型主键 ⇒ 自增」约定在收尾时判（§10.2）。</summary>
    [Da.Key]
    public int ShipmentId { get; set; }

    [Da.Required]
    [DaSchema.Column("carrier_code")]
    [Da.StringLength(32)]
    public string? CarrierCode { get; set; }

    [DaSchema.Column(TypeName = "decimal(18,4)")]
    public decimal Weight { get; set; }

    public DateTime? DispatchedAt { get; set; }

    /// <summary><c>[NotMapped]</c>：与 <see cref="Order.Customer"/> 同一个「跳过」语义，但跳过的手段不同。</summary>
    [DaSchema.NotMapped]
    public string? TrackingUrl { get; set; }
}

/// <summary>带<em>并发列</em>的实体（§8.2 的乐观锁，v0.18）。它存在的理由是 <see cref="Order"/> 给不了的那一格：
/// <c>Order</c> 的写路径样本已经被黄金样本钉死了，往里加一列会让几十份样本整片变红 ——
/// 而"没有并发列的实体一条语句都不多"这条主张，需要一个<em>有</em>并发列的类型来判，又不能拿别的类型陪葬。
///
/// <para><c>RowVersion</c> 在库里是 <c>rowversion</c>（8 字节、值由库给、不接受写），CLR 侧<em>必须</em>是可空的
/// <c>byte[]?</c>：新增那一趟调用方手里没有值，而 §10.6 的 required 尺子量的是整个实体、不是即将进列清单的那几列 ——
/// 写成 <c>byte[]</c> 会让"插一行新库存"这句话必抛。这是这一格今天真实的形状，不是疏忽。</para></summary>
[Table("FastSqlInventoryItem")]
public class InventoryItem
{
    public int Id { get; set; }

    [Column(Length = 32)]
    public string? Sku { get; set; }

    public int Quantity { get; set; }

    /// <summary>只比较、不书写：不进 <c>SET</c>、不进 INSERT 的列清单，进 <c>WHERE</c> 与回填集（§8.2）。</summary>
    [ConcurrencyToken]
    [Column(DbTypes = "rowversion")]
    public byte[]? RowVersion { get; set; }

    /// <summary>第二个并发列，而且是<em>可空</em>的那一档：库里没有任何东西写它，所以它一直是 NULL。
    /// 这不是凑数 —— <c>[列] = @p</c> 在 NULL 上是 UNKNOWN，照那个形状拼谓词就是<em>每次保存都假冲突</em>，
    /// 所以"NULL 折成 <c>IS NULL</c>"这一档必须有现场才判得动（集成层 <c>ConcurrencyPathTests</c>）。
    /// 两列同时存在还顺带钉住一件事：<c>WHERE</c> 与 <c>OUTPUT</c> 里并发列的次序必须是契约列序，
    /// 回包按位置落进实体，次序错了就是把 <c>RowVersion</c> 塞进 <c>CheckedAt</c>。</summary>
    [ConcurrencyToken]
    public DateTime? CheckedAt { get; set; }
}

/// <summary>完全没标注的实体：G1 不产出它（生成器只认带 <c>[Table]</c> 的类型），运行期必须退回 G2。
/// 这条负样本存在的意义是：「命中 G1」的断言不能只靠生成器在场就自称通过。</summary>
public class BareReport
{
    public int Id { get; set; }

    public string? Text { get; set; }
}

/// <summary><b>没有主键的一张表</b>：连 <c>Id</c> 都没有，所以约定层与标注层都找不到主键。
/// 它存在的理由是 §8.2 那句「无 PK 自动不跟踪」——今天的形态是<em>拒绝跟踪并指路</em>而不是静默不跟（§12.2），
/// 而读路径不受影响：身份映射与 <c>UPDATE</c>/<c>DELETE</c> 的 <c>WHERE</c> 都建立在主键上，只读不需要它们。
/// 两条元数据路径对"找不到主键"的处理是否同一个答案，也只有它能一次问清（§13.11）。</summary>
[Table("FastSqlAuditRow")]
public class AuditRow
{
    [Column(Length = 64)]
    public string? Actor { get; set; }

    public DateTime At { get; set; }

    [Column(Length = 128)]
    public string? Detail { get; set; }
}

/// <summary>主键由<em>调用方</em>给的引用导航靶子（v0.20）。它与 <see cref="SessionToken"/> 同属「<c>Guid</c> 键、
/// 库里没有 IDENTITY」那一档，多出来的那一格是：<b>键在保存之前就已经在手里</b>。
///
/// <para>这一格是 <c>SaveChanges</c> 排外键先后的<em>全部</em>前提：把子行的外键填对被引用行的主键，
/// 今天只有两种手段 —— 键本来就在手里（这一档），或者先插一行空外键、拿到库给的自增键再回填（那是 <c>Tp</c> 临时主键，
/// §8.2 的 v0.21）。所以这一对实体的集成见证能绿，而「<c>Order</c> 挂到一个新的 <c>Customer</c> 上」<em>不能</em>：
/// 后者的 <c>Customer.Id</c> 是 IDENTITY，插入前没有值可填。</para></summary>
[Table("FastSqlProject")]
public class Project
{
    public Guid Id { get; set; }

    [Column(Length = 64)]
    public string? Name { get; set; }
}

/// <summary><see cref="Project"/> 的子行：<b>主键是 IDENTITY、外键列指向那只 <c>Guid</c> 键的父行</b> ——
/// 两种键形态混在一条依赖上，正是「先父后子」这句话最难判的形状。
/// 两个名字都故意不重合：<c>Project</c>（导航）与 <c>ProjectId</c>（外键列）之间隔着一个 <c>I</c>，
/// 而 <c>Milestone</c> 里没有任何列叫 <c>Id</c> 之外的键形态线索。</summary>
[Table("FastSqlMilestone")]
public class Milestone
{
    public int Id { get; set; }

    public Guid ProjectId { get; set; }

    [Column(Length = 64)]
    public string? Title { get; set; }

    /// <summary>引用导航：约定外键 <c>ProjectId</c> 命中上一行那个 <c>Guid</c> 列 ⇒ 一根 <c>Milestone → Project</c> 的边。</summary>
    public Project? Project { get; set; }
}

/// <summary>自引用：约定外键 <c>ManagerId</c> 命中自己的表。<b>它必须不成边</b> —— 同一张表内部的先后
/// 不是类型序能表达的（那一行得先插自己再补自己的上级，那是另一件事），把它当边就等于把自己判成环。</summary>
[Table("FastSqlSelfRef")]
public class SelfRef
{
    public int Id { get; set; }

    public int? ManagerId { get; set; }

    public SelfRef? Manager { get; set; }
}

/// <summary>两个外键列、零个引用导航：<b>契约里没有边</b>。这一格要钉住的是反向的误判 ——
/// 光凭一个名字叫 <c>CustomerId</c> 的列就推出一条依赖，会把"这张表其实和被引用表无关"（NoSQL 式冗余列、
/// 或者列名撞车）当成环来拒。有列没有导航 ⇒ 不是边。</summary>
[Table("FastSqlTwoKeys")]
public class TwoKeys
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public int OrderId { get; set; }
}

/// <summary>「一张表<em>是不是</em>软删表」这件事的接口形态（§10.7 示例第一句的 <c>ISoftDelete</c>）。
/// <b>它存在的理由是那一档注册形态，不是这一格声明</b>：<c>orm.Filters.Add&lt;ISoftDelete&gt;(e =&gt; !e.Deleted)</c>
/// 要的是"按接口注册一次、所有实现类都带上这一条"，而这件事在 <c>Scope.ForFilter</c> 那一格才成立
/// （形参声明在接口上，而表位是 <see cref="Ticket"/>，按相等判就贴不上）。</summary>
public interface ISoftDelete
{
    bool Deleted { get; set; }
}

/// <summary>§10.7 全局过滤器的靶子：<b>既有 <c>deleted</c> 那一格、也有一根租户列</b>，于是软删与多租户
/// 那两种过滤器在同一张表上各有一个现场。
///
/// <para>它为什么不复用 <see cref="Order"/>：<c>Order</c> 的列序已经被几十份黄金样本钉死，往里加一列会让整片
/// 样本变红（<see cref="InventoryItem"/> 的注释写的就是同一件事）。而过滤器这一档<em>必须</em>有一张带
/// <c>Deleted</c>/<c>TenantId</c> 的表才能判"文本里多出的是哪一格"，所以按同一个先例另起一张。</para>
///
/// <para><see cref="TenantId"/> 与 <see cref="Deleted"/> 都故意<em>不</em>做成 <c>[Required]</c> 之类：
/// 这一张表要判的是谓词形状，不是校验档位，多一根尺子只会让失败消息分不清是谁。</para></summary>
[Table("FastSqlTicket")]
public class Ticket : ISoftDelete
{
    public int Id { get; set; }

    [Column(Length = 64)]
    public string? Title { get; set; }

    /// <summary>租户列。过滤器 <c>t =&gt; t.TenantId == Tenant.Current</c> 读的就是它，
    /// 而"每次执行重新求值"那句判据要靠它取值的那颗参数来验。</summary>
    public int TenantId { get; set; }

    /// <summary>软删列。<b>它是普通列，不是 <c>[ConcurrencyToken]</c></b>：那一格的形状已经被
    /// <see cref="InventoryItem"/> 钉过了，这里要判的是"谓词进不进 WHERE"，与"值归谁给"无关。</summary>
    public bool Deleted { get; set; }

    /// <summary>置位时间。<c>Behaviors.SoftDelete(deleted:, at:)</c> 那一格将来要写的就是这两列，
    /// 今天没有那个入口，所以这一列在离线用例里只是"存在但不被动"。</summary>
    public DateTime? DeletedAt { get; set; }
}

/// <summary>实现了 <see cref="ISoftDelete"/> 却把那一列<em>标成不映射</em>的实体：接口键的过滤器
/// 在它身上<b>必须贴不上，并且要说清是谁贴不上谁</b>。这一格是约束 4 那一半（接口键在注册期查不出来）
/// 唯一的现场 —— 没有它，"实现类缺列"这件事就只会以一句翻译器的原话露出来，而那句话里没有注册来源。</summary>
[Table("FastSqlLooseNote")]
public class LooseNote : ISoftDelete
{
    public int Id { get; set; }

    [Column(Length = 64)]
    public string? Text { get; set; }

    /// <summary>在 CLR 侧是真的实现了接口（所以编译期看不出问题），在库里这一列不存在。</summary>
    [DaSchema.NotMapped]
    public bool Deleted { get; set; }
}
