using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace QuickSql.Query;

/// <summary>跟踪集合（§5.4 读侧契约的最后一档、§8.1 里那句 <c>DbSet&lt;T&gt; : ISet&lt;T&gt;</c>）。
/// <b>名字与原稿差一个词</b>：规格写的是 <c>ISet&lt;T&gt;</c>，而那与 <c>System.Collections.Generic.ISet&lt;T&gt;</c>
/// 同形 —— 开着隐式 using 的每一个消费方（<c>DbSet&lt;T&gt;</c> 自己就是第一个）写一句 <c>ISet&lt;Order&gt;</c>
/// 就得到 CS0104「不明确的引用」。一个要靠调用方加别名才写得出来的公开契约，等于把改名没改完的账摊给所有人，
/// 所以这里叫 <c>ITrackedSet&lt;T&gt;</c>（§8.1/§5.4 已按此回写）。
/// <b>它是 <see cref="IQuery{T}"/> 而不是 <c>IQueryable</c></b>（§16.16：跟踪集合也不是查询表达式树），
/// 所以链式全部可用、<c>from … in</c> 不可用。
/// 跟踪与否在这里<em>不是两套 API</em>：本接口与 <see cref="IQuery{T}"/> 共用同一个查询引擎与同一份
/// <c>QueryModel</c>（§5.5），区别只在引擎上有没有挂会话的跟踪器 ——
/// 于是 <c>orm.Select&lt;T&gt;()</c> 与 <c>db.Set&lt;T&gt;().AsNoTracking()</c> 产出的模型文本逐字节相等，
/// 那正是 §13.2 第 2 项 ② 的判据。<b>这条等价性在 v0.16 之前是不成立的</b>：
/// 那时 <c>NoTracking</c> 是 <c>QueryModel</c> 上的一个位，而模型文本会把它打成 <c>NO TRACKING</c> 一行，
/// 挪到引擎上（v0.16）才是 §8.1 那句「同一份模型，物化期决定跟不跟踪」的字面兑现。</summary>
public interface ITrackedSet<T> : IQuery<T> where T : class
{
    /// <summary>会话里已跟踪实例的内存视图（§5.4 的 <c>LocalView&lt;T&gt; Local</c>）。
    /// 只反映<em>本会话</em>见过的实体，不等于库里有什么，也不含别的会话的实体。</summary>
    LocalView<T> Local { get; }

    /// <summary>给已跟踪实例补载导航（§8.1 的 <c>db.Orders.Load(o =&gt; o.Items)</c>）。
    /// 发 SQL 把导航属性填上：引用导航（<c>o =&gt; o.Customer</c>）与集合导航（<c>c =&gt; c.Orders</c>）都走这一颗口。
    /// 返回 <c>this</c> 让链式继续。</summary>
    [RequiresUnreferencedCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    [RequiresDynamicCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    ITrackedSet<T> Load<TProp>(Expression<Func<T, TProp>> navigation);

    /// <summary>按主键找一个实体（§8.3 的 <c>db.Set&lt;T&gt;().Find(...)</c>）。
    /// <b>先查内存</b>（身份映射里有就直接返回），<b>再查库</b>（发一条 <c>SELECT ... WHERE pk = @p0</c>，
    /// 找到就纳入会话并返回，找不到返回 <c>null</c>）。与 <see cref="LocalView{T}.Find"/> 的分别是后者只查内存、不发 SQL。</summary>
    T? Find(params object?[] primaryKeyValues);

    /// <summary><see cref="Find"/> 的异步版。</summary>
    Task<T?> FindAsync(CancellationToken ct = default, params object?[] primaryKeyValues);

    /// <summary>从库重新加载一个实体的所有列（§8.3 的 <c>db.Set&lt;T&gt;().Refresh(o)</c>）。
    /// <b>库值覆盖实体当前值</b>，然后重拍快照、清脏。已标 <c>Added</c> 或 <c>Modified</c> 的实例不动
    /// （用户有未保存的改动，库值不该覆盖）；不在会话里的实例按 <c>Unmodified</c> 纳入。</summary>
    void Refresh(T entity);

    /// <summary><see cref="Refresh"/> 的异步版。</summary>
    Task RefreshAsync(T entity, CancellationToken ct = default);
}

/// <summary>已跟踪实例的内存视图（§5.4 的 <c>LocalView&lt;T&gt; Local</c>）。
/// <b>它是快照而不是活集合</b>：每次枚举取当时的那一份，所以 <c>foreach (var e in db.Set&lt;T&gt;().Local)</c>
/// 中途 <c>Remove</c> 不会炸，但也看不到自己刚删的那一步（<see cref="Tracking.EntityState.Deleted"/> 的实例仍在快照里直到写完成）。
/// 不实现 <c>INotifyCollectionChanged</c>：那是 UI 绑定的红利，而按 §1.2 原则 5（性能是门禁不是形容词），
/// 换它要付的是每行一次通知 —— 批量装载一万行的那条路上，这笔钱没有任何本项目内的消费者。</summary>
public sealed class LocalView<T> : IReadOnlyList<T> where T : class
{
    readonly Func<IReadOnlyList<T>> _snapshot;
    readonly Func<object?[], T?> _find;

    internal LocalView(Func<IReadOnlyList<T>> snapshot, Func<object?[], T?> find)
    {
        _snapshot = snapshot;
        _find = find;
    }

    /// <summary>当时那一份的<em>引用</em>，不是拷贝（每行拷一份数组等于给每次访问加一笔分配）。</summary>
    public IReadOnlyList<T> Entries => _snapshot();

    public int Count => Entries.Count;

    public T this[int index] => Entries[index];

    /// <summary>按主键在<b>内存里</b>找。<b>不发 SQL</b>，所以「找不到」只有两种含义：
    /// 本会话没读过/没 Add 过这个键，或者库里那一行已经不是这个键了。
    /// 想问「库里有没有」要用 <c>db.Set&lt;T&gt;().Find(...)</c>（先查内存再查库）。</summary>
    public T? Find(params object?[] primaryKeyValues)
    {
        ArgumentNullException.ThrowIfNull(primaryKeyValues);

        return _find(primaryKeyValues);
    }

    public IEnumerator<T> GetEnumerator() => Entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
