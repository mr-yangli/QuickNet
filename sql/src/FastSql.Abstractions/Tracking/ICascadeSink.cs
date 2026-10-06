namespace FastSql.Tracking;

/// <summary>级联的落点：会话把"顺着这一根集合导航把子实体带进会话"这件事实现成一颗泛型方法，
/// 而<em>调用它的那一处</em>由实体的元数据交出来（G1 是生成器写死的 <c>Enter&lt;Order, OrderItem&gt;</c>，
/// G2 是解析器在启动期编译好的同一个委托）。
///
/// <para><b>为什么要这么绕一圈</b>：跟踪器是按实体类型切的（<c>EntityTracker&lt;T&gt;</c>），子端那一档要建出来
/// 就得有一个地方<em>在编译期知道</em> <c>TChild</c>。会话自己手里只有 <see cref="System.Type"/>，
/// 而它去 <c>MakeGenericType</c> 就是把「G1 路径下写侧 0 反射」那条不变量破了（§12.7），
/// 还要把 <c>[RequiresUnreferencedCode]</c> 一路糊到公开的 <c>Add</c>/<c>Remove</c> 上。
/// 生成器手里有编译期类型，所以这一步由它写；<c>Enter</c> 的实现只认 <c>TParent</c>/<c>TChild</c> 两个参数，
/// 带进来的是<em>已经装箱过一次的一串子实体</em>，热路径上是一次普通接口调用，不是反射调度。</para></summary>
public interface ICascadeSink
{
    /// <summary>父实体这一根集合导航里装着的子实体，请进会话。
    /// <paramref name="children"/> 为 <c>null</c> 说"集合位没装东西"：插入档那是"没有要带的人"，
    /// 删除档那是"捞不到子行"（会话据此拒，不留孤行）。</summary>
    /// <param name="memberName">那根集合导航的属性名，只用于报错文本里说清是哪一根。</param>
    void Enter<TParent, TChild>(TParent parent, IEnumerable<TChild>? children, string memberName)
        where TParent : class
        where TChild : class;
}
