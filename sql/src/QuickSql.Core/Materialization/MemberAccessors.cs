using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using QuickSql.Tracking;

namespace QuickSql.Materialization;

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
/// 而「哪一列、哪个实体、为什么找不到」那句话只有调用方说得准。<b>唯一的例外是集合导航那一支里
/// 「写入口不是公共的」两格</b>：调用方那句断言的是「成员不存在」，而这一臂里成员明明在场 —— 继续沿用
/// 就成了谎话。写口的可见性、集合位当场空不空，这两件事只有定位成员的这一支知道，所以那两句由它现拼。</remarks>
[RequiresUnreferencedCode("按运行期类型与成员名定位属性并取它的读口与写口。")]
[RequiresDynamicCode("把成员读写与集合追加编译成委托。")]
public static class MemberAccessors
{
    // ── 编译产物的进程级缓存 ─────────────────────────────────────────────
    // 三张表的键都只有委托体真正依赖的那几项（声明类型、成员名、元素型），<b>不含调用方递进来的报错文本</b>：
    // 文本只在"成员找不到/写口不可绑"那一次抛里用到，而那一抛发生在进缓存之前 —— 失败不落缓存，
    // 成功落的那一份与哪个调用点先来无关。形状与 Materializer.DtoReaders 同一条纪律（§12.7 C 第一条），
    // 修的也是同一件事的执行期那一半：Include / IncludeMany / 拆分加载 / DbSet.Load 四条路都在<b>每次执行</b>
    // 调这里，没有缓存时每次执行各付一轮反射定位 + Expression.Compile（数十 µs 起）——
    // 「加载可以反射、调用不行」里那些调用点根本不是启动期。契约构建那一次（ContractResolver）
    // 从此与执行期共用同一份产物，编译只发生一次。

    static readonly ConcurrentDictionary<(Type DeclaringType, string MemberName), Func<object, object>> Getters = new();

    static readonly ConcurrentDictionary<(Type DeclaringType, string MemberName), Action<object, object?>> Setters = new();

    static readonly ConcurrentDictionary<(Type DeclaringType, string MemberName, Type ElementType), CollectionBindings> Collections = new();

    /// <summary>读一颗公共实例属性的值，返回装箱后的 <c>object</c>（当字典键用）。
    /// 属性挂在基类上时委托里转的是那个基类而不是派生型 —— 与「按名字找成员」的原写法同形。</summary>
    /// <param name="missingMessage">属性不存在时的报错文本。</param>
    /// <param name="noGetterMessage">属性存在但没有公共读口时的报错文本。</param>
    public static Func<object, object> Getter(Type declaringType, string memberName, string missingMessage, string? noGetterMessage = null)
    {
        var key = (DeclaringType: declaringType, MemberName: memberName);

        // 命中路径先 TryGetValue：值类型键不装箱、不分配，连 GetOrAdd 那颗工厂元组都不造。
        if (Getters.TryGetValue(key, out var hit))
            return hit;

        return Getters.GetOrAdd(key,
            static (k, messages) => GetterCore(k.DeclaringType, k.MemberName, messages.missingMessage, messages.noGetterMessage),
            (missingMessage, noGetterMessage));
    }

    static Func<object, object> GetterCore(Type declaringType, string memberName, string missingMessage, string? noGetterMessage)
    {
        var read = (declaringType.GetProperty(memberName)
                    ?? throw new QuickSqlException(missingMessage)).GetGetMethod()
                   ?? throw new QuickSqlException(noGetterMessage ?? missingMessage);

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
        var key = (DeclaringType: declaringType, MemberName: memberName);

        if (Setters.TryGetValue(key, out var hit))
            return hit;

        return Setters.GetOrAdd(key,
            static (k, messages) => SetterCore(k.DeclaringType, k.MemberName, messages.missingMessage, messages.noSetterMessage),
            (missingMessage, noSetterMessage));
    }

    static Action<object, object?> SetterCore(Type declaringType, string memberName, string missingMessage, string? noSetterMessage)
    {
        var property = declaringType.GetProperty(memberName) ?? throw new QuickSqlException(missingMessage);
        var write = property.GetSetMethod() ?? throw new QuickSqlException(noSetterMessage ?? missingMessage);
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
                    ?? throw new QuickSqlException(missingMessage)).GetGetMethod()
                   ?? throw new QuickSqlException(missingMessage);

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
    ///
    /// <para><b>写入口不是公共的那一岔（ABP/DDD 的标配形状）</b>：<c>public List&lt;TChild&gt; Certs { get; protected set; } = [];</c>
    /// 是一根完全合法的集合导航，缺的只是让外人看得见的那半颗 set。旧形状在这里抛出的是「没有这根导航属性」——
    /// 那句话两次不实：成员在场，集合导航也成立，真正的原因只是「写口不可绑」。如今按形状分岔：
    /// 属性类型自己收得到追加口 ⇒ <b>往 getter 当场交回的那只集合里追加</b>，缝合那一条路根本不碰写口；
    /// 两样都没有（没有公共写口、属性类型又收不了追加）⇒ 当场抛，文案说清缺的到底是哪一样形状。
    /// <b>公共写口那一臂一字不易</b>：已经能工作的实体不为这一改换任何东西 —— 逐算子的分配基线也钉在那一臂上。</para>
    public static CollectionBindings BindCollection(Type declaringType, string memberName, Type elementType, string missingMessage)
    {
        var key = (DeclaringType: declaringType, MemberName: memberName, ElementType: elementType);

        if (Collections.TryGetValue(key, out var hit))
            return hit;

        return Collections.GetOrAdd(key, static (k, message) => BindCollectionCore(k.DeclaringType, k.MemberName, k.ElementType, message), missingMessage);
    }

    static CollectionBindings BindCollectionCore(Type declaringType, string memberName, Type elementType, string missingMessage)
    {
        var property = declaringType.GetProperty(memberName) ?? throw new QuickSqlException(missingMessage);
        var listType = typeof(List<>).MakeGenericType(elementType);

        // 声明类型自己收得到追加口就用声明类型（ICollection<T> 与 List<T> 都在这一臂），
        // 收不到（IEnumerable<T> 那一型）才退到当场新建的 List<T>：那条路上集合位为空的父可以正常缝，
        // 而集合位已经装着别的形状的父会当场抛，不是静默少一颗。
        var appendable = property.PropertyType.GetMethod("Add", [elementType]) is not null;
        var target = appendable ? property.PropertyType : listType;

        var add = target.GetMethod("Add", [elementType])
                  ?? throw new QuickSqlException($"{missingMessage}（{target.Name} 上找不到接受 {elementType.Name} 的追加口）");

        var getter = Getter(declaringType, memberName, missingMessage);

        if (property.GetSetMethod() is not null)
        {
            return new CollectionBindings(
                getter,
                Setter(declaringType, memberName, missingMessage),
                Adder(target, elementType),
                listType);
        }

        // 没有公共写口。可追加的那一臂：Attach 往现成集合里追加，永远走不到写口这一格；写口委托唯一的活
        // 是<em>真被调用到的那一次</em>把当场的原因说准 —— 集合位是 null 就报 null 那一格（缝不上去的
        // 真实形状是「写口不可见 ∧ getter 交回空串」，两句都要点到），集合位有人就报「这一档要整只写回」
        // （补载那一条路的形状，就地追加够不着它）。两句话都不再谎称「成员不存在」。
        if (appendable)
        {
            return new CollectionBindings(
                getter,
                NullWriteMouth(getter,
                    $"{declaringType.Name}.{memberName} 缝不上子实体：写入口不是公共的（set 是 protected 或 private），" +
                    $"而此刻 getter 交回的集合位是 null，没有现成集合可以就地追加。出路二选一：把写口放开为公共的，" +
                    $"或建实体时把导航初始化成可追加集合，例如 {RequiredShape(elementType, memberName)}。",
                    $"{declaringType.Name}.{memberName} 写不回整只集合：这一档要按整串替换（补载），而写入口不是公共的" +
                    $"（set 是 protected 或 private）。出路二选一：给这根导航一个公共写口，或让它长成建实体时就初始化好的" +
                    $"可追加集合（{RequiredShape(elementType, memberName)} 那一形，IncludeMany 的缝合走就地追加那条路）。"),
                Adder(target, elementType),
                listType);
        }

        // 两样都没有：只读属性交回的又是一副收不了追加的形状（IReadOnlyList、数组、裸 IEnumerable 这些），
        // 写不回来也追加不进 —— 这一格在建绑定的当场就响，文案点名到底是哪副形状不对。
        throw new QuickSqlException(
            $"{declaringType.Name}.{memberName} 挂不上子实体：这根属性没有公共写口，而它的形状 {Readable(property.PropertyType)}" +
            $" 上也没有接受 {elementType.Name} 的追加口 —— 既写不回整只集合，也追加不进现成集合。需要的形状二选一：" +
            $"带公共写口的集合属性，或建实体时已初始化成可追加集合的导航（{RequiredShape(elementType, memberName)}）。");
    }

    /// <summary>没有公共写口那一臂的「写口」位：正常缝合（就地追加）永远碰不到它；被碰到即这一档确实需要
    /// 写回整只集合，此刻按 getter 交回的到底是什么来挑那句真话。</summary>
    static Action<object, object?> NullWriteMouth(Func<object, object> getter, string nullSlotMessage, string replacementMessage)
        => (owner, _) => throw new QuickSqlException(getter(owner) is null ? nullSlotMessage : replacementMessage);

    /// <summary>报错文本里替调用方把「该长成什么样」写出来 —— 这一形就是 ABP/DDD 实体的标准写法。</summary>
    static string RequiredShape(Type elementType, string memberName)
        => $"public List<{elementType.Name}> {memberName} {{ get; protected set; }} = [];";

    /// <summary><c>IReadOnlyList&lt;ShopCert&gt;</c> 这样的形状名：<c>Type.Name</c> 交回的是 <c>IReadOnlyList`1</c>，
    /// 报给人看之前把反引号那一截换成尖括号参数表。</summary>
    static string Readable(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var head = type.Name[..type.Name.IndexOf('`')];

        return $"{head}<{string.Join(", ", type.GetGenericArguments().Select(Readable))}>";
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
        var ctor = ParameterlessConstructor(type)
                   ?? throw new QuickSqlException(
                       $"{type.Name} 没有无参构造器，装配不出实例（公共与内部、受保护、私有都查过）。");

        var body = Expression.Convert(Expression.New(ctor), typeof(object));

        return Expression.Lambda<Func<object>>(body).Compile();
    }

    /// <summary>无参构造器：<b>装配用的那一颗，不问可见性</b>——公共查不到就退到内部/受保护/私有。
    /// <para>为什么这一格可以不问：物化要的形状是「造得出一个空壳、字段初始化器已就位」，
    /// 而 ABP/DDD 的实体恰恰是 <c>protected X()</c> + 有参创建构造器那一族——把可见性当门槛，
    /// 得到的不是"更安全"，是整颗实体读写两侧一起拒（实测：INSERT 与 SELECT 响同一句话）。</para>
    /// <para><b>带参的那一颗不在这一格</b>：构造器 DTO 与 record 走的是 <c>Materializer</c> 里
    /// <c>ViaConstructor</c> 那一臂，仍只认公共构造器——参数表是给人对位的协议，
    /// 把私有构造器当协议会在别人改内部实现时悄悄换掉装配形状，而那一族本来就该有公共构造器。</para>
    /// <para>两条实测边界记在这里，免得有人拿错工具：<c>Activator.CreateInstance&lt;T&gt;()</c> 对非公共无参构造
    /// <b>抛</b> <c>MissingMethodException</c>（它要的是 <c>new()</c> 那一档）；
    /// <c>RuntimeHelpers.GetUninitializedObject</c> 能出实例但<b>不跑构造器也不跑字段初始化器</b>，
    /// 于是 <c>Xxx { get; set; } = [];</c> 那一格留 null——正是集合导航最怕的形状。
    /// <c>Expression.New(非公共 ctor).Compile()</c> 两样都对，所以走这一条。</para></summary>
    public static ConstructorInfo? ParameterlessConstructor(Type type)
        => type.GetConstructor(Type.EmptyTypes)
           ?? type.GetConstructor(
               BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
               binder: null,
               types: Type.EmptyTypes,
               modifiers: null
           );

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
