using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using FastSql.Tracking;

namespace FastSql.Materialization;

/// <summary>「加载可以反射、调用不行」（§12.7 C）在急切加载与跟踪路径上的兑现处。
/// 取一个成员的当前值、给一个成员写值、往一个集合里追加一颗 —— 三件事都只在<b>建计划</b>那一次
/// 按名字定位成员，之后每行拿到的是已编译委托：调用它是普通的方法调用，不是反射调度。
/// 之所以单独立一支而不是就地各写一份表达式树：同一段体在五个地方各写过一遍
/// （取键的两对、引用导航与集合导航各一处、Include 的导航设置器），
/// 每一遍都只记得反射定位、忘了反射调用，于是每行每颗键留一次反射调度（归属见 §12.8 第 8 行那一格）。</summary>
/// <remarks>标注打在类上：这一支的存在就是「运行期按名字找成员」这件事的边界，
/// 与 <c>EntitySchemaRegistry</c> 同一取向（G1 生成的实现自带这些委托的等价物，不需要这里）。
/// 编译一次的代价在首行或启动期，摊到每行是零；它换掉的反射调度按实测是每次数十纳秒的量级。
/// 报错文本一律由调用方给：这一支不知道自己是在找主键属性还是在外键列对应的属性，
/// 而「哪一列、哪个实体、为什么找不到」那句话只有调用方说得准。</remarks>
[RequiresUnreferencedCode("按运行期类型与成员名定位属性并取它的读口与写口。")]
[RequiresDynamicCode("把成员读写与集合追加编译成委托。")]
public static class MemberAccessors
{
    /// <summary>读一颗公共实例属性的值，返回装箱后的 <c>object</c>（当字典键用）。
    /// 属性挂在基类上时委托里转的是那个基类而不是派生型 —— 与「按名字找成员」的原写法同形。</summary>
    /// <param name="missingMessage">属性不存在时的报错文本。</param>
    /// <param name="noGetterMessage">属性存在但没有公共读口时的报错文本。</param>
    public static Func<object, object> Getter(Type declaringType, string memberName, string missingMessage, string? noGetterMessage = null)
    {
        var read = (declaringType.GetProperty(memberName)
                    ?? throw new FastSqlException(missingMessage)).GetGetMethod()
                   ?? throw new FastSqlException(noGetterMessage ?? missingMessage);

        var entity = Expression.Parameter(typeof(object), "o");

        return Expression
            .Lambda<Func<object, object>>(
                Expression.Convert(
                    Expression.Call(Expression.Convert(entity, read.DeclaringType!), read),
                    typeof(object)),
                entity)
            .Compile();
    }

    /// <summary>给一颗公共实例属性写值。写入前把 <c>object</c> 转成成员的声明类型，
    /// 所以传错类型进来当场抛 <c>InvalidCastException</c> —— 与原先那句反射写入同一行为，
    /// 差别只是那次「问成员是什么型」挪到了建计划时，不在每行问。</summary>
    /// <param name="missingMessage">属性不存在时的报错文本。</param>
    /// <param name="noSetterMessage">属性存在但没有公共写口时的报错文本。</param>
    public static Action<object, object?> Setter(Type declaringType, string memberName, string missingMessage, string? noSetterMessage = null)
    {
        var property = declaringType.GetProperty(memberName) ?? throw new FastSqlException(missingMessage);
        var write = property.GetSetMethod() ?? throw new FastSqlException(noSetterMessage ?? missingMessage);
        var entity = Expression.Parameter(typeof(object), "o");
        var value = Expression.Parameter(typeof(object), "v");

        return Expression
            .Lambda<Action<object, object?>>(
                Expression.Call(
                    Expression.Convert(entity, write.DeclaringType!), write,
                    Expression.Convert(value, property.PropertyType)),
                entity, value)
            .Compile();
    }

    /// <summary>集合导航的级联入口：把「拿这只集合送进子端会话」那一次泛型调用编译成委托。
    /// <b>这是 G2 兜底侧的形状</b> —— <c>MakeGenericMethod</c> 只在这里出现一次（建契约那一次，启动期），
    /// 级联每行拿到的是已编译委托；G1 侧由生成器直接写 <c>sink.Enter(parent, parent.Items, "Items")</c>，
    /// 运行期零反射（§12.7 A 的「写侧 0 反射」）。之所以把这一格交给 <c>ICascadeSink</c> 而不是返回
    /// <c>IEnumerable&lt;object&gt;</c>：跟踪器是按类型分格的（<c>For&lt;TChild&gt;()</c>），
    /// 那一格的类型只有在编译期或这一次泛型方法绑定里才拿得到。</summary>
    /// <param name="missingMessage">属性不存在或没有公共读口时的报错文本。</param>
    public static Action<ICascadeSink, object> CascadeEnter(Type parentType, Type childType, string memberName, string missingMessage)
    {
        var read = (parentType.GetProperty(memberName)
                    ?? throw new FastSqlException(missingMessage)).GetGetMethod()
                   ?? throw new FastSqlException(missingMessage);

        var enter = typeof(ICascadeSink).GetMethod(nameof(ICascadeSink.Enter))!
            .MakeGenericMethod(parentType, childType);

        var sink = Expression.Parameter(typeof(ICascadeSink), "sink");
        var owner = Expression.Parameter(typeof(object), "o");
        var parent = Expression.Convert(owner, parentType);

        return Expression
            .Lambda<Action<ICascadeSink, object>>(
                Expression.Call(
                    sink, enter,
                    parent,
                    Expression.Convert(Expression.Call(parent, read), typeof(IEnumerable<>).MakeGenericType(childType)),
                    Expression.Constant(memberName, typeof(string))),
                sink, owner)
            .Compile();
    }

    /// <summary>集合导航的一整套访问器：读集合位当前的值、写回一只新集合、往里追加一颗、
    /// 以及按元素型建一只空集合。四件事共用同一次成员定位，所以一个节点只反射一次。
    /// 元素型在建计划时就知道（它是导航属性那个 <c>T</c>），于是运行期不再按每颗子行的实际类型
    /// 去找追加口 —— 原先找不到是静默不加，这里改成建计划时就抛：静默丢一颗子行不是可接受的失败形状。</summary>
    public static CollectionBindings BindCollection(Type declaringType, string memberName, Type elementType, string missingMessage)
    {
        var property = declaringType.GetProperty(memberName) ?? throw new FastSqlException(missingMessage);
        var listType = typeof(List<>).MakeGenericType(elementType);

        // 声明类型自己收得到追加口就用声明类型（ICollection<T> 与 List<T> 都在这一臂），
        // 收不到（IEnumerable<T> 那一型）才退到当场新建的 List<T>：那条路上集合位为空的父可以正常缝，
        // 而集合位已经装着别的形状的父会当场抛，不是静默少一颗。
        var target = property.PropertyType.GetMethod("Add", [elementType]) is not null ? property.PropertyType : listType;

        var add = target.GetMethod("Add", [elementType])
                  ?? throw new FastSqlException($"{missingMessage}（{target.Name} 上找不到接受 {elementType.Name} 的追加口）");

        return new CollectionBindings(
            Getter(declaringType, memberName, missingMessage),
            Setter(declaringType, memberName, missingMessage),
            Adder(target, elementType),
            listType);
    }

    /// <summary>往集合里追加一颗，集合与元素都从 <c>object</c> 进来；转成目标类型这一步由委托里的
    /// 一次显式转换承担，所以运行期没有方法查找。</summary>
    static Action<object, object> Adder(Type collectionType, Type elementType)
    {
        var add = collectionType.GetMethod("Add", [elementType])!;
        var collection = Expression.Parameter(typeof(object), "c");
        var item = Expression.Parameter(typeof(object), "i");

        return Expression
            .Lambda<Action<object, object>>(
                Expression.Call(
                    Expression.Convert(collection, add.DeclaringType!), add,
                    Expression.Convert(item, elementType)),
                collection, item)
            .Compile();
    }

    /// <summary>按类型建一只实例（用它的无参构造器），编译期就把 <c>Activator</c> 那次查找换成一次 new。</summary>
    static Func<object> New(Type type)
    {
        var body = Expression.Convert(Expression.New(type), typeof(object));

        return Expression.Lambda<Func<object>>(body).Compile();
    }

    /// <summary>集合导航的四把委托，挂在节点计划上：每行只调用、不反射。</summary>
    [RequiresUnreferencedCode("按运行期类型与成员名定位集合属性。")]
    [RequiresDynamicCode("集合的四把委托是编译出来的表达式树。")]
    public sealed class CollectionBindings(
        Func<object, object> getter,
        Action<object, object?> setter,
        Action<object, object> add,
        Type listType)
    {
        /// <summary>集合位当前的值（未初始化时为 null）。</summary>
        public Func<object, object> Getter { get; } = getter;

        /// <summary>把一整只集合写回导航属性。</summary>
        public Action<object, object?> Setter { get; } = setter;

        /// <summary>往集合里追加一颗。</summary>
        public Action<object, object> Add { get; } = add;

        /// <summary>建一只空的元素型表，供「先攒后写回」那一档用。</summary>
        public Func<object> NewCollection { get; } = New(listType);

        /// <summary>追加一颗；集合位还是空的就先建一只、写回去、再追加。</summary>
        public void Attach(object parent, object child)
        {
            var collection = Getter(parent);

            if (collection is null)
            {
                collection = NewCollection();
                Setter(parent, collection);
            }

            Add(collection, child);
        }
    }
}
