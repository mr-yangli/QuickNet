using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using FastSql.Metadata;

namespace FastSql.Materialization;

/// <summary>原生 SQL 的结果集 → CLR 对象：<b>按列名对属性名</b>装配。
///
/// <para>与 <see cref="Materializer"/> 那三条路（实体契约、投影槽位、标量直读）的分工是
/// <b>这一口没有模型可读</b>：手写 SQL 交回来的列序与列名不在 <c>QueryModel</c> 里，
/// 所以那一侧的"槽位顺序即投影顺序"在这里不成立，只能按名字问。</para>
///
/// <para><b>反射按<em>型</em>一次，名字对位按<em>场次</em>一次</b>：属性清单、读口与写口的编译只发生在第一次
/// 见到这个 <c>T</c> 的时候（<see cref="Plans"/> 按型存），之后每次执行只带一张自己的一次性对照表 ——
/// 第一行读列名、填序号，剩下的行走已编译委托。这与 <c>Materializer.DtoReaders</c> 给链式投影做的那件事同形，
/// 而它在这里不是可选项：这一口是报表出口，一句 SQL 套在循环里跑几百次是常态用法，
/// 「每执行一次问一遍属性」会把 §12.7 C 那条「加载可以反射、调用不行」在这一支上破掉。</para>
///
/// <para><b>两条匹配规则，一条报错规则</b>：结果集里对不上任何属性的列<em>忽略</em>（手写 SQL 常带辅助列、
/// 计数列、临时 join 列）；属性拿不到列的<em>留默认值</em>（那是那句 SQL 少选了一列的正当结果，看得见的是 SQL 作者）；
/// 而<b>一颗属性都没对上时抛</b> —— 整行全默认值的对象是一句"看起来完全合理"的空壳，
/// 与 §5.8 禁静默退化同一条判据。</para>
///
/// <para>形状要求是「公共无参构造 + 公共可写属性」。<b>构造器 DTO 与 record 不支持</b>：
/// 它们的参数顺序在这一口里没有对应物（列序由 SQL 决定），低层口
/// <c>AdoExecutor.QueryAsync&lt;T&gt;(DialectSql, Func&lt;DbDataReader, T&gt;, …)</c> 才是那种形状的落点 ——
/// 自己给委托，那里什么形状都装得下。</para></summary>
public static class RawRowReader
{
    static readonly ConcurrentDictionary<Type, object> Plans = new();

    /// <summary>给一句原生 SQL 造一行读取器。两条形状分支的分工见本节的注释：
    /// 可直读的标量形状读第 0 列，其余按「无参构造 + 公共可写属性」装配。
    /// <b>每次执行调一次</b>：<c>AdoExecutor</c> 那几个原生口就是这么用的，而按型缓存的那一份
    /// 只存与结果集无关的东西，列序号活在返回的这张一次性对照表里。</summary>
    [RequiresUnreferencedCode("行出口按运行期类型找公共可写属性，并为每颗属性建一次读口。")]
    [RequiresDynamicCode("行出口用表达式树编译属性读口、写口与构造函数委托。")]
    public static Func<DbDataReader, T> Create<T>()
    {
        var target = typeof(T);
        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        if (IsColumnShape(underlying))
            return reader => ValueConverter.Read<T>(reader, 0);

        if (target.IsValueType)
            throw new FastSqlException(
                $"原生 SQL 的行出口不接 {target.Name} 这种形状：它既不是可直读第 0 列的标量，也不是可建实例的类型。" +
                "要那种形状请用低层口自己给 Func<DbDataReader, T>。");

        var plan = (Plan<T>) Plans.GetOrAdd(target, _ => new Plan<T>());

        // 每次执行一张一次性对照表：-2 表示"还没问"，-1 表示"问过而没有这一列"。
        var ordinals = new int[plan.Names.Length];
        Array.Fill(ordinals, Unbound);

        return reader => plan.Materialize(reader, ordinals);
    }

    const int Unbound = -2;

    /// <summary>按型存的那一份：<b>只含与结果集无关的东西</b>（属性清单、读口、写口、构造委托）。
    /// 列序号不能进这一份 —— 同一个 <c>T</c> 可以被两句列序不同的 SQL 用，把序号存进按型缓存
    /// 就是让第二句读到第一句的对位。</summary>
    sealed class Plan<T>
    {
        public string[] Names { get; }

        readonly Func<DbDataReader, int, object>[] readers;
        readonly Action<object, object?>[] setters;
        readonly Func<T> factory;

        [RequiresUnreferencedCode("按运行期类型找公共无参构造与公共可写属性。")]
        [RequiresDynamicCode("把构造与属性读写编译成委托。")]
        public Plan()
        {
            var type = typeof(T);

            var constructor = type.GetConstructor(Type.EmptyTypes)
                ?? throw new FastSqlException(
                    $"{type.Name} 没有公共无参构造：原生 SQL 的行出口只能装配「无参构造 + 可写属性」的形状。" +
                    "构造器 DTO 与 record 请走低层口自己给 Func<DbDataReader, T>（列序由 SQL 决定，参数顺序在这里没有对应物）。");

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .ToArray();

            if (properties.Length == 0)
                throw new FastSqlException(
                    $"{type.Name} 上没有公共可写属性，结果集没有地方落。" +
                    "要么给属性加公共 setter，要么走低层口自己给 Func<DbDataReader, T>。");

            Names = [.. properties.Select(p => p.Name)];
            readers = [.. properties.Select(BoxedColumnReader)];
            setters = [.. properties.Select(p => MemberAccessors.Setter(
                type, p.Name,
                $"{type.Name} 上没有属性 {p.Name}（刚在册，不该走到这里）",
                $"{type.Name}.{p.Name} 没有公共写口"))];
            factory = Expression.Lambda<Func<T>>(Expression.New(constructor)).Compile();
        }

        public T Materialize(DbDataReader reader, int[] ordinals)
        {
            if (ordinals[0] == Unbound)
                Bind(reader, ordinals);

            var row = factory();

            for (var i = 0; i < ordinals.Length; i++)
            {
                if (ordinals[i] >= 0)
                    setters[i](row!, readers[i](reader, ordinals[i]));
            }

            return row;
        }

        void Bind(DbDataReader reader, int[] ordinals)
        {
            var columns = new string[reader.FieldCount];

            for (var i = 0; i < columns.Length; i++)
                columns[i] = reader.GetName(i);

            var matched = 0;

            for (var i = 0; i < Names.Length; i++)
            {
                // 先按原样找，再按忽略大小写找：库里回什么形状由建表时那一份引号决定，
                // 而属性名只有一份写法，两种都可能对得上（达梦折大写、MySQL 系原样）。
                var ordinal = Array.FindIndex(columns, c => c == Names[i]);

                if (ordinal < 0)
                    ordinal = Array.FindIndex(columns, c => string.Equals(c, Names[i], StringComparison.OrdinalIgnoreCase));

                ordinals[i] = ordinal;
                matched += ordinal >= 0 ? 1 : 0;
            }

            if (matched == 0)
                throw new FastSqlException(
                    $"原生 SQL 的结果集里没有一列对得上 {typeof(T).Name} 的公共属性：" +
                    $"结果集是 [{string.Join("、", columns)}]，属性是 [{string.Join("、", Names)}]。" +
                    "整行默认值的对象不是结果，它只是一句选错了列名的 SQL。");
        }
    }

    /// <summary>一颗属性的「按序号读并装箱」委托：<c>ValueConverter.Read&lt;声明类型&gt;</c> 的泛型实例
    /// 在<b>建计划时</b>闭一次、编译成委托，之后每行不再问类型。</summary>
    [RequiresDynamicCode("把 ValueConverter.Read<TProp> 闭成按序号取值的委托。")]
    static Func<DbDataReader, int, object> BoxedColumnReader(PropertyInfo property)
    {
        var read = typeof(ValueConverter)
            .GetMethod(nameof(ValueConverter.Read), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(property.PropertyType);

        var reader = Expression.Parameter(typeof(DbDataReader), "reader");
        var ordinal = Expression.Parameter(typeof(int), "ordinal");

        return Expression.Lambda<Func<DbDataReader, int, object>>(
            Expression.Convert(Expression.Call(read, reader, ordinal), typeof(object)),
            reader, ordinal).Compile();
    }

    /// <summary>一列能装下的 CLR 形状：<see cref="ContractResolver"/> 那份可映射判定的同一闭合集合，
    /// 多加一颗 <c>byte[]</c>（原生 SQL 常直接选 <c>VARBINARY</c>/<c>BLOB</c> 列，而实体侧不认它是因为
    /// 实体属性到列名的映射另有渠道，不是这里也要跟着挡）。
    /// 落在这族之外的一律走对象装配那一条，于是 <c>struct</c> 里那种"三个字段"的形状会撞在
    /// 「没有公共无参构造/没有可写属性」那句可读的报错上，而不是被当成标量去 <c>Convert.ChangeType</c>。</summary>
    static bool IsColumnShape(Type type)
        => type.IsPrimitive
           || type.IsEnum
           || type == typeof(string)
           || type == typeof(byte[])
           || type == typeof(decimal)
           || type == typeof(Guid)
           || type == typeof(DateTime)
           || type == typeof(DateTimeOffset)
           || type == typeof(DateOnly)
           || type == typeof(TimeOnly)
           || type == typeof(TimeSpan);
}
