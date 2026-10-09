using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Linq.Expressions;
using QuickSql.Metadata;
using QuickSql.Query.Model;
using QuickSql.Schema;

namespace QuickSql.Materialization;

/// <summary>数据库返回值 → CLR 值的单点转换。各家驱动对同一 DbType 给的对象并不一致
/// （SQL Server 的 uniqueidentifier 回 <see cref="Guid"/>，DM 回 <c>byte[]</c>，金仓的 boolean 可能回 <c>t/f</c>），
/// 所以这一层必须存在，且必须只有一处。</summary>
public static class ValueConverter
{
    /// <summary>按列序号读一颗值。<b>这是物化路径唯一的取数口</b>：实体、DTO 槽位、标量与聚合出口
    /// 都从这里走（实体那一档从 v0.38 第五笔起前面多了一条直读臂，而那条臂的非直读分支就是这一句，
    /// 见 <c>DirectRead</c>），所以"某一列的读法"只有一处可改（§12.7 C 第二条）。</summary>
    /// <remarks><b>这里有过一条强类型门，2026-09-21 按实测撤掉了</b>。它写的是
    /// <c>GetFieldType(ordinal) == typeof(T)</c> 时改走 <c>IsDBNull + GetFieldValue&lt;T&gt;</c>，
    /// 于是真驱动（Microsoft.Data.SqlClient，LocalDB）上一颗 <c>int</c> 列的读从 24 B 降到 0 B ——
    /// 但每次读从 95.2 ns 涨到 136.7 ns —— <b>门的边际是 +41.5 ns，买的是那 24 B</b>。别拿单独读数相加来算这道门
    /// （<c>GetFieldType</c> 27.4 + <c>IsDBNull</c> 53.2 + <c>GetFieldValue&lt;int&gt;</c> 71.4 = 152，比实测的 136.7 多出一截：
    /// 那些数是各自单独测的，<b>这张表只能取两行之差</b>，逐格数字与这条限定都在 §12.7 C 末尾那份）。
    /// 假驱动（门禁那片 <c>DataTableReader</c>）更差：它没实现强类型路径，装箱一分不省（§12.8 第 3 行仍是 736 B/行），
    /// 而第 2 行的物化时间从 748 µs 涨到 1 145 µs。<b>两侧同号：换的是 GC 压力、赔的是 CPU，所以这一刀不成立。</b>
    /// 它要成立的条件记在 §16.21 —— 把"这列究竟是什么类型"的探测从<b>每值</b>提升到<b>每场次</b>，
    /// 并且落到 <c>GetInt32</c> / <c>GetDecimal</c> 这类原始访问器上（实测 49 / 53 ns，比 <c>GetValue</c> 的 91 ns 还快），
    /// 那条路才谈得上"零装箱且不掉速"。
    /// <para><b>那一格今天按它自己给的条件落了（v0.38 第五笔丙）</b>，而落点不在这一句里：
    /// 探测与臂都在 <c>ReadOps</c>/<c>DirectRead</c>，每场次一张 <c>ReadPlan</c>，每值只剩
    /// <c>IsDBNull</c> + 原始访问器（89.0 ns / 0 B 那一格）。这一句本身一字未动，仍是
    /// <c>To(reader.GetValue(ordinal))</c>，理由是它现在服务的是剩下的那一半：
    /// 空值列、枚举、<c>byte[]</c>、<c>DateOnly</c>/<c>TimeOnly</c>/<c>DateTimeOffset</c>、
    /// 驱动交回类型与声明类型不同型的列、DTO 槽位，以及标量与聚合出口里落不了臂的那一半
    /// （第八笔起那一档也先领票，见 <see cref="Materializer.ScalarRows{T}"/>，而 NULL 那一格仍回到这一句）。
    /// <c>TypedReadTests.The_read_door_is_byte_for_byte_the_converter</c> 钉的就是这一句没有被偷偷换成别的东西；
    /// 直读那一半由同册的 <c>ZeroBoxingTests</c> 判（有臂的列确实不走装箱、且两条路读出来的值逐格相同）。</para></remarks>
    public static T Read<T>(DbDataReader reader, int ordinal)
        => To<T>(reader.GetValue(ordinal));

    public static T To<T>(object? value)
    {
        if (value is null or DBNull)
            return default!;

        if (value is T exact)
            return exact;

        var target = typeof(T);
        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        if (underlying.IsEnum)
            return (T)Enum.ToObject(underlying, Convert.ToInt64(value, CultureInfo.InvariantCulture));

        if (underlying == typeof(Guid))
            return (T)(object)ToGuid(value);

        if (underlying == typeof(bool))
            return (T)(object)ToBoolean(value);

        if (underlying == typeof(decimal))
            return (T)(object)Convert.ToDecimal(value, CultureInfo.InvariantCulture);

        if (underlying == typeof(DateOnly) && value is DateTime dateOnly)
            return (T)(object)DateOnly.FromDateTime(dateOnly);

        if (underlying == typeof(TimeOnly) && value is DateTime timeOnly)
            return (T)(object)TimeOnly.FromDateTime(timeOnly);

        if (underlying == typeof(TimeSpan) && value is not TimeSpan)
            return (T)(object)TimeSpan.FromTicks(Convert.ToInt64(value, CultureInfo.InvariantCulture));

        if (underlying == typeof(string))
            return (T)(object)ToText(value);

        return (T)Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
    }

    static string ToText(object value) => value switch
    {
        string text => text,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        Guid guid => guid.ToString(),
        _ => value.ToString() ?? string.Empty,
    };

    static bool ToBoolean(object value) => value switch
    {
        bool flag => flag,
        byte bit => bit != 0,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
    };

    public static Guid ToGuid(object value) => value switch
    {
        Guid guid => guid,
        byte[] bytes when bytes.Length == 16 => FromMixedEndian16(bytes),
        byte[] bytes when bytes.Length == 36 => Guid.Parse(System.Text.Encoding.ASCII.GetString(bytes)),
        string text => Guid.Parse(text),
        _ => throw new QuickSqlException($"无法把 {value.GetType().Name} 读成 Guid"),
    };

    /// <summary>MySQL 系与部分国产库的 <c>BINARY(16)</c> 是混合字节序：前 4 字节小端、
    /// 接着 2+2 小端、尾部 8 字节大端。能力位 <c>Guid_Raw16</c> 为真时读回来的形态就是这个。</summary>
    public static Guid FromMixedEndian16(ReadOnlySpan<byte> bytes)
    {
        Span<byte> normalized = stackalloc byte[16];
        bytes[..16].CopyTo(normalized);

        Reverse(normalized, 0, 4);
        Reverse(normalized, 4, 2);
        Reverse(normalized, 6, 2);

        return new Guid(normalized);

        static void Reverse(Span<byte> span, int start, int length) => span.Slice(start, length).Reverse();
    }
}

/// <summary>结果行 → 对象。形状由 <see cref="ProjectionPlan"/> 决定，所以它属于物化层而不是方言层（§3 第 6 层）。</summary>
public static class Materializer
{
    /// <summary>按投影形态取行读取器。<b>只有 DTO 分支需要反射与表达式树编译</b>：
    /// 实体走注册表里已编译的读取器、标量/聚合直读第 0 列，两条都是零反射（§12.7 C）。
    /// DTO 那一条按形状进 <see cref="DtoReaders"/>，所以每个形状只编译一次而不是每次执行一次。
    /// 标注打在分派口而不是整个类上，是为了让「AOT 只禁掉 DTO 投影」这件事在警告里看得见，
    /// 而不是让整条物化路径背上反射罪名 —— G1 接管 DTO 投影后这个标注就可以摘掉。
    /// <b>缓存不会让这两条标注变窄</b>：静态分析看的是"这里可能编译"，不是"这次命中了"。</summary>
    [RequiresUnreferencedCode("ObjectProjection 的组装按运行期 Type 反射构造器与成员。")]
    [RequiresDynamicCode("ObjectProjection 的组装用表达式树编译委托，AOT 下不可用。")]
    public static Func<DbDataReader, T> Create<T>(QueryModel model, IEntitySchemaRegistry registry)
    {
        switch (model.Plan)
        {
            case EntityProjection entity:
            {
                var table = model.Tables[entity.TableIndex];
                var contract = table.Contract ?? throw new QuickSqlException($"表位 {table.Alias} 没有实体契约，无法物化");

                // LINQ 的 join/let 只换「元素形状」，投影计划可以还是实体那一行，而调用方要的类型是
                // 编译器造的匿名类型。这里不挡就会走到 (T) 强转，吐一句 InvalidCastException 说不清是谁的错。
                if (!typeof(T).IsAssignableFrom(contract.ClrType))
                    throw new QuickSqlException(
                        $"查询返回的是 {contract.ClrType.Name} 的整行，而结果类型是 {typeof(T).Name}：把整行放进投影（select new {{ o, c }} 这类）" +
                        "要 §5.7 的嵌套对象加载，本版本未接入。请投影成列（select new { o.Id, c.City }），或只投影一个实体（select c）。");

                var read = registry.ReadDelegate(contract.ClrType);

                // 实体投影的选列就是契约列序（QueryCompiler.EntityColumns），所以 reader 序号 0..n-1 一一对应。
                return EntityRows<T>(read, contract);
            }

            case ValueProjection:
            case AggregateProjection:
                return ScalarRows<T>();

            case ObjectProjection projection:
            {
                // 键里只有委托体真正依赖的那四项：谓词/表/别名进不了委托体，把它们算进键就等于
                // 同一处投影按参数各编一次 —— 那正是要修的东西（论证在 §12.7 C 第一条）。
                var key = new ReaderKey(typeof(T), projection);

                if (DtoReaders.TryGetValue(key, out var cached))
                    return (Func<DbDataReader, T>)cached;

                var assembled = Assemble<T>(projection);

                DtoReaders[key] = assembled;

                return assembled;
            }

            default:
                throw new QuickSqlException("未知投影形态：" + model.Plan.GetType().Name);
        }
    }

    /// <summary>把注册表那条按票读委托包成"每场次探一次"的行读取器（§16.21 的落地处）。
    /// <para><b>探测的时机不是构建时，而是第一次拿到 reader 时</b>：<see cref="Create{T}"/> 在
    /// 连接还没打开的时候就要返回 <c>Func&lt;DbDataReader,T&gt;</c>，那时无从问驱动"这一列你交回什么类型"。
    /// 所以票据挂在闭包上，按 <em>reader 的引用</em> 判要不要重探：同一趟读里每行只多一次引用比较，
    /// 换一只 reader（下一次执行、下一条语句）就重探一张。引用而不是"第几张"这一维，是因为
    /// <see cref="DbDataReader.GetFieldType"/> 答的是那只 reader 的性质，而它在一趟读里不会变。</para>
    /// <para><b>并发口径</b>：这一张票是可变状态，前提是"一个返回的 Func 只在一趟顺序读的循环里被调"，
    /// 那是 <c>IAdo.QueryAsync/StreamAsync/FirstAsync</c> 的实际形状（逐行 await，不并发）。
    /// 谁要把同一个 Func 并发喂给两只 reader，要先把它换成每场次一只对象，而不是在这里加锁。</para>
    /// <para>标量与聚合那一档（<c>ValueProjection</c>/<c>AggregateProjection</c>）不在这里，它有自己的
    /// <see cref="ScalarRows{T}"/>。第五笔丙当时给它留的理由是「调用点是泛型的，<c>T</c> 在编译期不知道，
    /// 而臂是按型各一条的静态方法」——<b>那句理由在第八笔被推翻</b>：<c>ScalarRows&lt;T&gt;</c> 里
    /// <c>typeof(T)</c> 每一支实例化都是定死的，按型落一支臂只是把它从"编译期已知"换成"实例化期已知"。
    /// 真正留着的分别是空值守卫归谁问，写在 <see cref="ScalarRows{T}"/> 那一格。</para></summary>
    public static Func<DbDataReader, T> EntityRows<T>(Func<DbDataReader, ReadPlan, object> read, EntityContract contract, int startOffset = 0)
    {
        DbDataReader? probed = null;
        ReadPlan plan = default;    // 第一次调用必然重探（probed 还是 null，而 reader 不是），所以这一格永远是占位

        return reader =>
        {
            if (!ReferenceEquals(probed, reader))
            {
                probed = reader;
                plan = ReadOps.Build(reader, contract, startOffset);
            }

            return (T) read(reader, plan);
        };
    }

    /// <summary>标量与聚合那一档的行读取器：第 0 列也领一张票（第八笔，结 §14 债③ 的那一支）。
    /// <para><b>与实体那一档唯一的分别是空值守归谁问。</b>实体侧的成员声明自带可空性
    /// （<c>int</c> 与 <c>int?</c> 是两支臂，第七笔丁因此敢让非空那一族不再问 <c>IsDBNull</c>），
    /// 而这里的 <typeparamref name="T"/> 只是调用方要的形状：<c>Sum</c> 打在空集上、<c>Max</c> 打在
    /// 全空那一列上，库交回的就是 NULL，那是合法答案而不是模型在说谎。所以非空那一族的臂在这一档
    /// 要先在门外问一次空值、交回 <c>default</c> —— 与旧版那一句逐字 <c>ValueConverter.Read&lt;T&gt;(reader, 0)</c>
    /// 在同一格上等价（门对 NULL 给的答案也是 <c>default</c>）。<see cref="string"/> 与
    /// <c>Nullable&lt;T&gt;</c> 那两族的臂自己问，这一档不再问第二遍：那会用两趟驱动调用换一趟的答案。</para>
    /// <para>探测仍是每场次一颗 <c>GetFieldType</c>。聚合恒定一行，那一颗与它省下的一颗盒同场抵消，
    /// 读作「不赔」；真正值它的是多行的标量流（<c>Select(x =&gt; x.Name).ToListAsync()</c> 那一型）。
    /// 判据的形状照 §16.21：能落臂的列仍由 <c>GetFieldType</c> 决定，落不了的一律进那一扇门，
    /// 而这一档交回的委托与旧版那句 lambda 同一条契约——第 0 列、每场次一张票、值与门逐格相同。</para></summary>
    public static Func<DbDataReader, T> ScalarRows<T>()
    {
        DbDataReader? probed = null;
        byte op = ReadOps.Boxed;

        return reader =>
        {
            if (!ReferenceEquals(probed, reader))
            {
                probed = reader;
                op = ReadOps.Probe(reader, 0, typeof(T));
            }

            if (op == ReadOps.Boxed)
                return ValueConverter.Read<T>(reader, 0);

            return ScalarArm<T>.GuardsNull && reader.IsDBNull(0)
                ? default!
                : ScalarArm<T>.Read(reader, 0, op);
        };
    }

    /// <summary>标量出口按 <typeparamref name="T"/> <b>每一支实例化只落一次</b>的臂（静态泛型字段就是它的缓存）。
    /// <para>臂的句柄来自 <see cref="DirectRead.ArmFor"/>，取的是方法组转换出来的那只
    /// <c>Func&lt;DbDataReader,int,byte,T&gt;</c>：<c>typeof(T)</c> 是 <c>int</c> 的那一支实例化里，
    /// 表上那只 <c>Func&lt;DbDataReader,int,byte,int&gt;</c> 与目标型是<em>同一个运行期类型</em>，
    /// 所以这一次转换不是把值箱起来再拆，而是同名委托的原样通过——每值仍然是零装箱，只有多一次间接调用。
    /// 没有臂的那些型（枚举、<c>byte[]</c>、<c>DateOnly</c>/<c>TimeOnly</c>/<c>DateTimeOffset</c>/<c>TimeSpan</c>、
    /// <c>object</c>）交回门本身：它们的票由 <see cref="ReadOps.Probe"/> 恒定为
    /// <see cref="ReadOps.Boxed"/>，所以那一句实际读不到第三颗参数——留着它是为了守住
    /// 「挑错臂的方向是没有变快，不是读错值」那条既有约定。</para></summary>
    static class ScalarArm<T>
    {
        public static readonly Func<DbDataReader, int, byte, T> Read =
            DirectRead.ArmFor(typeof(T)) as Func<DbDataReader, int, byte, T> ?? Door;

        static T Door(DbDataReader reader, int ordinal, byte op) => ValueConverter.Read<T>(reader, ordinal);

        /// <summary>这一档需不需要替臂问那一次空值：只有<b>非空值类型</b>要。<see cref="string"/>
        /// 与 <c>Nullable&lt;T&gt;</c> 的臂自己问（<see cref="DirectRead.ReadString"/> 与那一族
        /// <c>ReadNullableXxx</c>），这里再问就是重复付一趟驱动调用。</summary>
        public static readonly bool GuardsNull =
            typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null;
    }

    // ── Include 物化（§5.6） ─────────────────────────────────────────────

    /// <summary>带急切加载的物化器（§5.6）。结果集被编译器按深度优先遍历展宽：根实体列在 0..n-1，
    /// 每个 Include 的列紧随其后（<see cref="IncludeNode.ColumnOffset"/>）。启动期一次性构建物化计划
    /// （读委托、列偏移、导航属性设置器），之后每行只走已编译路径。</summary>
    [RequiresUnreferencedCode("Include 物化按运行期 Type 解析导航属性并反射设置。")]
    [RequiresDynamicCode("Include 物化路径走 G2 兜底注册表。")]
    public static Func<DbDataReader, T> CreateWithIncludes<T>(QueryModel model, IEntitySchemaRegistry registry)
    {
        var entity = model.Plan as EntityProjection
            ?? throw new QuickSqlException("Include 物化只支持实体投影，当前投影形态：" + model.Plan.GetType().Name);

        var rootTable = model.Tables[entity.TableIndex];
        var rootContract = rootTable.Contract
            ?? throw new QuickSqlException($"表位 {rootTable.Alias} 没有实体契约，无法物化 Include");

        var rootRead = registry.ReadDelegate(rootContract.ClrType);
        var rootRows = EntityRows<T>(rootRead, rootContract);
        var childPlans = BuildIncludePlans(model.Includes, registry, rootContract.ClrType);

        return reader =>
        {
            var root = rootRows(reader);

            // 读取委托交回的永远是一只新建的实体，不会是 null；这里的 ! 只补可空性注解看不出来的那一件事
            // （T 上没有 class 约束，编译器不知道那条不变式）。
            for (var i = 0; i < childPlans.Length; i++)
                childPlans[i].Materialize(reader, root!);

            return root;
        };
    }

    [RequiresUnreferencedCode("Include 物化按运行期 Type 解析导航属性并反射设置。")]
    [RequiresDynamicCode("Include 物化路径走 G2 兜底注册表。")]
    static IncludePlan[] BuildIncludePlans(
        IReadOnlyList<IncludeNode> includes, IEntitySchemaRegistry registry, Type parentClrType)
    {
        var plans = new IncludePlan[includes.Count];

        for (var i = 0; i < includes.Count; i++)
        {
            var node = includes[i];
            var contract = node.TargetTable.Contract
                ?? throw new QuickSqlException($"Include 目标表 {node.TargetTable.Alias} 没有实体契约");

            var read = registry.ReadDelegate(contract.ClrType);

            var firstPk = contract.PrimaryKey[0];
            var firstPkIndex = Array.IndexOf(contract.Columns, firstPk);
            var nullCheckOrdinal = node.ColumnOffset + firstPkIndex;

            var setter = ResolveNavigationSetter(parentClrType, node.NavigationMemberName);
            var children = BuildIncludePlans(node.Children, registry, contract.ClrType);

            plans[i] = new IncludePlan(read, contract, node.ColumnOffset, nullCheckOrdinal, setter, children);
        }

        return plans;
    }

    [RequiresUnreferencedCode("Include 物化按运行期 Type 解析导航属性。")]
    [RequiresDynamicCode("Include 物化的导航设置器是编译出来的委托。")]
    static Action<object, object?> ResolveNavigationSetter(Type parentType, string memberName)
        => MemberAccessors.Setter(
            parentType, memberName,
            $"实体 {parentType.Name} 上没有名为 {memberName} 的导航属性。" +
            "Include 要求导航属性是公共可写属性。",
            $"实体 {parentType.Name}.{memberName} 没有公共写口。Include 要求导航属性是公共可写属性。");

    /// <summary>一个 Include 节点的预编译物化计划。持有读委托、契约、列偏移、空值检测序号、导航设置器
    /// 与子计划。Materialize 在每行上调用，全程零反射；直读票按 reader 引用探一次、这一趟复用（§16.21）。</summary>
    sealed class IncludePlan(
        Func<DbDataReader, ReadPlan, object> read,
        EntityContract contract,
        int startOffset,
        int nullCheckOrdinal,
        Action<object, object?> setter,
        IncludePlan[] children)
    {
        DbDataReader? probed;
        ReadPlan plan;

        public void Materialize(DbDataReader reader, object parent)
        {
            if (reader.IsDBNull(nullCheckOrdinal))
                return;

            if (!ReferenceEquals(probed, reader))
            {
                probed = reader;
                plan = ReadOps.Build(reader, contract, startOffset);
            }

            var child = read(reader, plan);

            setter(parent, child);

            for (var i = 0; i < children.Length; i++)
                children[i].Materialize(reader, child);
        }
    }

    /// <summary>DTO 读取委托的指纹缓存（§12.7 C 第一条："<c>(查询指纹, 列指纹) → Func</c> 编译一次"）。
    /// 进程级而不是挂在 <see cref="QuickSqlOrm"/> 上：委托与注册表、方言、连接串都无关，
    /// 挂到实例上只会让每个 <c>QuickSqlOrm</c> 各编译一份同一个形状。
    /// 用 <c>ConcurrentDictionary</c> 是这条路径的性质决定的 —— 形状随首次执行惰性出现，
    /// 与"一次构建完再冻结"（§12.7 B 的容器规则）不兼容，与 §12.8 第 5 行问的是同一个问题。</summary>
    static readonly ConcurrentDictionary<ReaderKey, Delegate> DtoReaders = new();

    /// <summary>缓存键。<b>相等判定与哈希都是手写的</b>：record 自动生成那一版对数组字段比的是引用，
    /// 于是形状相同、数组不同的两次查询永远判成不等 —— 缓存静默不命中，而它在基准上只表现为"没优化"，
    /// 不会有任何人来查这一格。手写版比的是逐项形状，代价是命中路径上多一个小数组键（三个槽位算下来约 72 B，没单独测），
    /// 换掉的是未命中那 1.8 ms 的表达式树编译。</summary>
    readonly record struct ReaderKey(Type TargetType, Type ResultType, bool ViaConstructor, (string Name, Type MemberType)[] Slots)
    {
        public ReaderKey(Type targetType, ObjectProjection projection)
            : this(targetType, projection.ResultType, projection.ViaConstructor,
                   [.. projection.Slots.Select(s => (s.Name, s.MemberType))])
        {
        }

        public bool Equals(ReaderKey other)
            => TargetType == other.TargetType && ResultType == other.ResultType
               && ViaConstructor == other.ViaConstructor && Slots.SequenceEqual(other.Slots);

        public override int GetHashCode()
        {
            var hash = new HashCode();

            hash.Add(TargetType);
            hash.Add(ResultType);
            hash.Add(ViaConstructor);

            foreach (var (name, memberType) in Slots)
            {
                hash.Add(name, StringComparer.Ordinal);
                hash.Add(memberType);
            }

            return hash.ToHashCode();
        }
    }

    [RequiresUnreferencedCode("按运行期 Type 找构造器与可写成员。")]
    [RequiresDynamicCode("编译装配 DTO 的表达式树。")]
    static Func<DbDataReader, T> Assemble<T>(ObjectProjection projection)
    {
        var reader = Expression.Parameter(typeof(DbDataReader), "reader");
        var read = typeof(ValueConverter).GetMethod(nameof(ValueConverter.Read), BindingFlags.Public | BindingFlags.Static, null, [typeof(DbDataReader), typeof(int)], null)!;

        var locals = new List<ParameterExpression>();
        var slotValues = new List<Expression>();
        var body = new List<Expression>();

        for (var i = 0; i < projection.Slots.Count; i++)
        {
            var slot = projection.Slots[i];
            var local = Expression.Variable(slot.MemberType, "slot" + i);

            locals.Add(local);
            slotValues.Add(local);
            body.Add(Expression.Assign(local, Expression.Call(read.MakeGenericMethod(slot.MemberType),
                reader, Expression.Constant(i))));
        }

        var result = Expression.Variable(projection.ResultType, "result");
        locals.Add(result);

        Expression creation;

        if (projection.ViaConstructor)
        {
            var ctor = projection.ResultType.GetConstructors()
                           .SingleOrDefault(c => c.GetParameters().Length == projection.Slots.Count)
                       ?? throw new QuickSqlException($"类型 {projection.ResultType.Name} 没有 {projection.Slots.Count} 参数的构造器");

            creation = Expression.New(ctor, slotValues);
        }
        else
        {
            var defaultCtor = projection.ResultType.GetConstructor(Type.EmptyTypes);

            if (defaultCtor is null && !projection.ResultType.IsValueType)
                throw new QuickSqlException($"类型 {projection.ResultType.Name} 需要无参构造器才能用成员初始化投影");

            // struct 那一侧反射通常拿不到"无参构造器"这颗（record struct 与不写构造器的 struct 都没有可查的
            // 那一个），而 C# 的 `new S { X = … }` 合法 —— 表达式树用无 ctor 的 New 就是 initobj，
            // 与那条语义同一件事。
            var seed = projection.ResultType.IsValueType
                ? Expression.New(projection.ResultType)
                : Expression.New(defaultCtor!);

            var bindings = new List<MemberBinding>();

            for (var i = 0; i < projection.Slots.Count; i++)
            {
                var slot = projection.Slots[i];
                MemberInfo? member = (MemberInfo?)projection.ResultType.GetProperty(slot.Name) ?? projection.ResultType.GetField(slot.Name);

                if (member is null)
                    throw new QuickSqlException($"{projection.ResultType.Name} 上没有可写的成员 {slot.Name}");

                var target = member switch
                {
                    PropertyInfo property => property.PropertyType,
                    FieldInfo field => field.FieldType,
                    _ => slot.MemberType,
                };

                bindings.Add(Expression.Bind(member, Expression.Convert(slotValues[i], target)));
            }

            creation = Expression.MemberInit(seed, bindings);
        }

        body.Add(Expression.Assign(result, creation));
        body.Add(projection.ResultType == typeof(T) ? result : (Expression)Expression.Convert(result, typeof(T)));

        return Expression.Lambda<Func<DbDataReader, T>>(Expression.Block(locals, body), reader).Compile();
    }
}
