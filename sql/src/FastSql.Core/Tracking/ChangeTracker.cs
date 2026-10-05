using FastSql.Aop;
using FastSql.Metadata;

namespace FastSql.Tracking;

/// <summary>会话的跟踪器（§8.2 的 <c>db.ChangeTracker</c>），也就是 <see cref="EntityTracker{T}"/> 按实体类型
/// 切开之后的那层调度。
///
/// <b>这一层里没有一处 <c>MakeGenericMethod</c></b>：泛型参数 <c>T</c> 全部由编译期已知的调用点带进来
/// （<c>db.Set&lt;T&gt;()</c>、<c>db.Add&lt;T&gt;()</c>、<c>SaveChangesAsync</c> 里 <c>Rows&lt;TResult&gt;</c> 的那一次），
/// 运行期只按 <see cref="Type"/> 查一张表 —— §1.2 原则 3 那条「加载可以反射，调用不行」的分界线画在这里。
///
/// <b>非线程安全</b>：一个会话同一时刻只归一个执行流（§7 生命周期表里那句「<c>DbContext</c> 才需要 Scoped」的
/// 实现含义）。身份映射与 <c>_byRef</c> 都没有加锁，跨线程共用一个会话会得到「同一行两只实例」而不是异常。</summary>
sealed class ChangeTracker(FastSqlOrm orm) : DbChangeTracker
{
    /// <summary>按<b>调用点上的静态类型</b>（<c>Set&lt;T&gt;()</c> 的那个 <c>T</c>）分档。
    /// 键不是 <c>GetType()</c>：一只派生实例经 <c>Set&lt;Base&gt;()</c> 进会话时，它写出去的是基类那张表的列，
    /// 而 <see cref="Entries"/> 也得在同一条档里看得见它 —— 两边用同一个键才自洽。</summary>
    readonly Dictionary<Type, ITypeTracker> _byType = new();

    /// <summary>类型的<em>首次出现序</em>，报告与 <see cref="Entries"/> 都按它走。
    /// 字典的枚举序不保证，所以这一份列表不是可有可无的。</summary>
    readonly List<ITypeTracker> _order = [];

    /// <summary>三档的执行序（§8.2：先插、再改、最后删）。<b>档内的类型序另算</b>：见
    /// <see cref="OrderedByDependency"/>，那里才是「先插被依赖的、再删依赖方」。</summary>
    static readonly WriteOperation[] Phases = [WriteOperation.Insert, WriteOperation.Update, WriteOperation.Delete];

    /// <summary>§8.2 的那个开关。<b>关掉的只有「物化之前那一次」和 <see cref="HasChanges"/> 里的那一次</b>，
    /// 而 <c>SaveChanges</c> 那一步（<see cref="WritesAreDue"/>）不看这个开关、恒先侦测再写：
    /// 跳过这一步会让 <c>SaveChanges</c> 漏掉没被任何显式赋值覆盖的改动，
    /// 而「漏写」查不出来。</summary>
    internal bool AutoDetectChangesEnabled { get; set; } = true;

    public override int Count
    {
        get
        {
            var count = 0;

            foreach (var tracker in _order)
                count += tracker.Count;

            return count;
        }
    }

    /// <remarks><b>它不是只读的</b>：开着自动侦测时它会先比一轮。不这么做的话，「改了值、还没轮到侦测」
    /// 这一段里 <c>HasChanges</c> 会报 <c>false</c>，而调用方拿这个 <c>false</c> 去跳过 <c>SaveChanges</c>
    /// 就是丢数据 —— 报错方向上偏保守（多一次无用的 <c>UPDATE</c>）可以接受，偏乐观不行。
    /// 代价随会话里的实例数线性，所以它是一句诊断用的话，不是热路径上每次都问一遍的开关。</remarks>
    public override bool HasChanges
    {
        get
        {
            if (AutoDetectChangesEnabled)
                DetectChanges();

            return AnyDue();
        }
    }

    public override IEnumerable<EntityEntry> Entries()
    {
        foreach (var tracker in _order.ToArray())
            foreach (var node in tracker.Nodes)
                yield return tracker.EntryOf(node);
    }

    /// <remarks>返回的是「本次改了状态的实例数」（各类型相加），<c>0</c> 就是这一轮没有任何东西变过 ——
    /// §8.2 的哈希短路（没碰过的行不进列比较）还没接线，所以今天这一轮是全员逐列比，
    /// 那一格代价记在 §12.8 的 <c>ChangeTrackerDetect10k</c>（该门禁项本段未实现，理由写在 §14）。</remarks>
    public override int DetectChanges()
    {
        var changed = 0;

        foreach (var tracker in _order.ToArray())
            changed += tracker.DetectChanges();

        return changed;
    }

    /// <summary>清的是数据，留的是档：类型注册表留着，会话上那些已经发出去的 <c>DbSet&lt;T&gt;</c> 与查询引擎
    /// 才不会再撞上「档不存在」。之后 <c>Set&lt;T&gt;()</c> 拿到的还是同一份空跟踪器。</summary>
    public override void Clear()
    {
        foreach (var tracker in _order)
            tracker.Clear();
    }

    /// <summary>取（或建）某一个实体类型的那一份跟踪器。<b>只有泛型调用点用它</b>，
    /// 因为 <see cref="EntityTracker{T}"/> 的构造要 <c>orm.Schemas.Schema&lt;T&gt;()</c> 与
    /// <c>InsertEngine&lt;T&gt;</c>，这些是编译期参数而不是运行期能补的东西。</summary>
    internal EntityTracker<T> For<T>() where T : class
    {
        if (_byType.TryGetValue(typeof(T), out var known))
            return (EntityTracker<T>) known;

        var tracker = new EntityTracker<T>(orm, this);
        _byType[typeof(T)] = tracker;
        _order.Add(tracker);

        return tracker;
    }

    /// <summary>物化路径的身份映射入口（§8.2「同一上下文内重复读取返回同一实例」）。
    /// <paramref name="entityType"/> 是<em>静态</em>类型：<c>GetType()</c> 在这里会把「按基类纳入会话的派生实例」
    /// 错分到另一档去，而那种实例根本不可能被 <c>For&lt;Derived&gt;()</c> 建过档。</summary>
    internal object TrackEntity(object entity, Type entityType)
        => Tracker(entityType).Track(entity);

    /// <summary>整批登记。<b>就地写回列表元素</b>：去重之后「会话里的那一只」可能与物化出来的那一只不是同一只，
    /// 而调用方拿到的必须是会话认的那一只 —— 否则下一句 <c>db.Entry(拿到的那个)</c> 会说它不在会话里。</summary>
    internal void TrackAll<T>(List<T> rows, Type entityType)
    {
        var tracker = Tracker(entityType);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (row is null)
                continue;

            rows[i] = (T) tracker.Track(row).Entity;
        }
    }

    /// <summary>会话里有没有<em>任何</em>一档欠着临时主键（§8.2 的 <c>Tp</c>）。
    /// 这是那条改写路径的闸门：一轮保存通常一颗临时键都没有，而判"有没有"只扫几个类型档，
    /// 不必把每行的每个外键列都读一遍。<b>问的是全会话而不是本档</b>：发号在主端那一档，
    /// 拿着那颗值的是依赖方那一档，两边不是同一份账。</summary>
    internal bool HasPendingTemps
    {
        get
        {
            foreach (var tracker in _order)
                if (tracker.HasTempKeys)
                    return true;

            return false;
        }
    }

    /// <summary>按<em>边的目标类型</em>把临时键的问题送到主端那一档。<b>目标档不存在就是"不是临时键"</b>：
    /// 号只在 <c>Add</c> 时发，而 <c>Add</c> 必然先把档建好，所以没有档就没有号，不需要为它造一个空档。</summary>
    internal TempProbe ProbeTemp(Type targetType, object? value, out object? real)
    {
        if (!_byType.TryGetValue(targetType, out var tracker))
        {
            real = null;

            return TempProbe.NotTemp;
        }

        return tracker.ProbeTemp(value, out real);
    }

    /// <summary>一次 <c>SaveChanges</c> 的问句：<b>先侦测一遍，再问有没有一行欠着语句</b>。
    /// 与 <see cref="HasChanges"/> 的分别是它<em>不看开关</em> —— <c>SaveChanges</c> 恒定先比一轮，
    /// 关掉自动侦测不该把没被显式赋值覆盖的改动漏掉（那是丢数据，不是省一次比较）。
    /// 判据本身仍是逐行的 <c>WillWrite</c>，粘性位只用来跳过整档扫描。</summary>
    internal bool WritesAreDue()
    {
        DetectChanges();

        return AnyDue();
    }

    bool AnyDue()
    {
        foreach (var tracker in _order)
        {
            if (!tracker.MayHaveWrites)
                continue;

            foreach (var node in tracker.Nodes)
                if (tracker.WillWrite(node))
                    return true;
        }

        return false;
    }

    /// <summary>一次 <c>SaveChanges</c> 的写入：三档 → 结算。<b>前置条件是调用方刚跑过
    /// <see cref="WritesAreDue"/></b>（那里就是"侦测 → 再决定要不要开事务"这一步）：
    /// 这里不再比一遍，否则每次保存都要把全会话的字段读两遍，而两遍之间不可能有人插进来改状态 ——
    /// 本类比 <see cref="Entries"/> 那几处快照的余地都没有，非线程安全写在类注释上。</summary>
    internal async Task<SaveChangesReport> FlushDueAsync(CancellationToken ct)
    {
        var traces = new List<HookTrace>();
        var operations = new List<SaveChangesOp>();

        // 快照一份档列表：写的时候要是最开始那几档，中途不接纳新登记的类型。
        // 排序只动档内的类型先后，三档的先后（§8.2 那句"先插再改最后删"）不动。
        var trackers = OrderedByDependency(_order.ToArray());

        foreach (var phase in Phases)
            for (var i = 0; i < trackers.Length; i++)
            {
                // 删除走逆序：被依赖的那一行最后删，否则库里那条外键约束先把语句挡下来
                // —— 而"外键约束报错"是这一格里最难读的一种失败形状（§8.2）。
                var tracker = trackers[phase == WriteOperation.Delete ? trackers.Length - 1 - i : i];

                if (!tracker.MayHaveWrites)
                    continue;

                operations.AddRange(await tracker.WritePhaseAsync(phase, traces, ct).ConfigureAwait(false));
            }

        foreach (var tracker in trackers)
            tracker.PostFlush();

        return new SaveChangesReport(operations, traces);
    }

    /// <summary>这一批的关系边排不排得出先后 —— <b>只判、一条语句都不发</b>。<c>SaveChangesAsync</c>
    /// 把它放在开事务之前：排不出顺序是元数据的事，而 <c>BeginTransaction</c> 会先一步去碰驱动，
    /// 于是"连不上库"那句盖住了成因（离线场景更是永远测不到这一格）。</summary>
    internal void EnsureWriteOrderResolvable() => OrderedByDependency(_order.ToArray());

    /// <summary>分表实体与跟踪器互斥（§16.4 那一格的裁决）。<b>判据取"这一行此刻会不会被写出去"</b>，
    /// 与 <see cref="AnyDue"/> 同一颗 <see cref="ITypeTracker.WillWrite"/>：只把分表实体<em>读</em>进会话
    /// （Unattached/Unchanged）是无害的，那条路不发语句，没必要为此关掉整个会话的读侧身份映射。
    ///
    /// <para><b>为什么不是"实现一半"</b>：会话的账按主键记，而分表里"同键的两行"是能存在的两行
    /// （<c>Id</c> 自增而表按月切时，两张片各自的 1 号互不相干），身份映射因此不成立 ——
    /// 那不是补一条 WHERE 就能修的，要先定"跨片的一行由什么指认"。规格 §16.4 把这一格记为已裁，
    /// 出路写在消息里（写侧那三条链按行的分片键值定表，本来就不需要会话）。</para></summary>
    internal void EnsureNoSplitWrites()
    {
        foreach (var tracker in _order)
        {
            if (!tracker.MayHaveWrites || tracker.Contract.Table.Split is not { } split)
                continue;

            foreach (var node in tracker.Nodes)
                if (tracker.WillWrite(node))
                    throw new FastSqlException(
                        $"{tracker.EntityType.Name} 声明了分表（{split.Kind} 档，分片键列 {split.ColumnName}），跟踪器写不出去：" +
                        "会话的账按主键记，而同一个主键在分表里可以占着两张片上的各一行，身份映射认不出“要写的是哪一行”。" +
                        "分表的写请用 orm.Insert/InsertRange/Update/Delete 那几条链（它们按行的分片键值算目标表）。");
        }
    }

    /// <summary>这一批写要落的那座集群（§16.4 的分库那一档），<b>跨集群就整批拒</b>。
    /// 判据与 <see cref="EnsureNoSplitWrites"/> 同一颗 <see cref="ITypeTracker.WillWrite"/>：只把另一座
    /// 集群的实体<em>读</em>进会话不构成一批跨集群的写。
    ///
    /// <para><b>为什么是"整批拒"而不是"按集群拆成几场事务依次提交"</b>：后者看着是把同一件事做完了，
    /// 实际改了 <c>SaveChanges</c> 那句"一批=一场事务"的承诺 —— 第二批失败时第一批已经进了库，而报告里
    /// 只有一份 <c>Success=false</c>，调用方无从知道哪几行已经在库里。那种形状正是要避开的那一类
    /// "看着像原子，其实不是"。要分库写就自己分次 <c>SaveChanges</c>（每次都在一座集群上、每次都提交得明明白白），
    /// 或者按集群各开一条会话 —— 会话本来就是 Scoped 的。</para>
    ///
    /// <para>返回 <c>null</c> 说"这一批没有要写出去的行"，调用方按缺省集群开事务（那一批本来一条语句都不发，
    /// <c>SaveChangesAsync</c> 在更前面就交了空报告）。名字不叫 <c>EnsureSingleClusterWrites</c>：它除了判
    /// 还要<em>给出结论</em>，而那个结论是开事务那一格要的参数。</para></summary>
    internal string? WriteCluster()
    {
        string? cluster = null;

        foreach (var tracker in _order)
        {
            if (!tracker.MayHaveWrites)
                continue;

            var named = DataSourceRouting.ClusterOf(tracker.Contract.Table);

            foreach (var node in tracker.Nodes)
            {
                if (!tracker.WillWrite(node))
                    continue;

                if (cluster is null)
                {
                    cluster = named;
                    continue;
                }

                if (!string.Equals(cluster, named, StringComparison.Ordinal))
                    throw new FastSqlException(
                        $"这一批同时有集群 {cluster}（{tracker.EntityType.Name} 在集群 {named} 上）与集群 {named} 的写：一场事务只有一条连接，跨集群的保存拒。" +
                        "请按集群分两次 SaveChanges，或为每一座集群各开一条会话。");
            }
        }

        return cluster;
    }

    /// <summary>档内的类型序按外键依赖排：被依赖的（主端）在前。判据只有一个来源——契约上那条
    /// <see cref="RelationshipContract"/> 边，也就是 §10.3 那句「三处只读契约，不许各自去读注解」。
    ///
    /// <para><b>排得出先后，接下来那一半才排得出引用</b>：主端键要等库给的那一种（子实体先 <c>Add</c>、
    /// 父实体的键还空着）由 <c>Tp</c> 临时主键接住（§8.2，v0.21）—— 先按这里的顺序把主端写出去、
    /// 真实键回填进实体，再在依赖方建语句之前把它手里那颗临时值换掉。所以这一格合上了"先后"，
    /// 那一格合上"引用"，两句都要在才成立。</para>
    ///
    /// <para>没有任何关系边时原样返回（首次出现序），所以 v0.19 之前的行为一字未动；
    /// 拓扑只在会话真出现了跨实体关系时才付这一点代价。</para></summary>
    static ITypeTracker[] OrderedByDependency(ITypeTracker[] trackers)
    {
        // 快路径：会话里没有任何关系边就不排（绝大多数会话只有几个类型，但这一步在每次保存上都要走）。
        if (Array.TrueForAll(trackers, static t => t.Contract.Relationships.Count == 0))
            return trackers;

        var byType = new Dictionary<Type, int>(trackers.Length);

        for (var i = 0; i < trackers.Length; i++)
            byType[trackers[i].EntityType] = i;

        var edges = new List<Type>[trackers.Length];

        for (var i = 0; i < trackers.Length; i++)
            edges[i] = EdgesOf(trackers[i], byType);

        var ordered = new ITypeTracker[trackers.Length];
        var settled = new bool[trackers.Length];

        for (var emitted = 0; emitted < trackers.Length; emitted++)
        {
            // 同层之间保留首次出现序：稳定，所以"没有关系"的那些档不会因为字典枚举序而换位置。
            var next = -1;

            for (var i = 0; i < trackers.Length && next < 0; i++)
            {
                if (settled[i])
                    continue;

                if (Ready(i))
                    next = i;
            }

            if (next < 0)
                throw new FastSqlException(CycleMessage(trackers, settled));

            ordered[emitted] = trackers[next];
            settled[next] = true;
        }

        return ordered;

        bool Ready(int index)
        {
            foreach (var dependency in edges[index])
            {
                if (!settled[byType[dependency]])
                    return false;
            }

            return true;
        }
    }

    /// <summary>这一档依赖哪些<em>会话里在场的</em>类型。<b>自引用不算边</b>：同一张表内部的先后
    /// 不是类型序能解决的（那一行得先插自己再补自己的上级，那是另一件事），留着只会把自己判成环。</summary>
    static List<Type> EdgesOf(ITypeTracker tracker, Dictionary<Type, int> inSession)
    {
        var edges = new List<Type>();

        foreach (var relationship in tracker.Contract.Relationships)
        {
            if (relationship.TargetType == tracker.EntityType)
                continue;

            if (inSession.ContainsKey(relationship.TargetType) && !edges.Contains(relationship.TargetType))
                edges.Add(relationship.TargetType);
        }

        return edges;
    }

    static string CycleMessage(ITypeTracker[] trackers, bool[] settled)
    {
        var stuck = new List<string>();

        for (var i = 0; i < trackers.Length; i++)
        {
            if (!settled[i])
                stuck.Add(trackers[i].EntityType.Name);
        }

        return $"这些实体的外键依赖成环，排不出写入先后：{string.Join(" / ", stuck)}。" +
               "临时主键（Tp，§8.2）能接住「主端的键要等库给」那一种，前提是排得出谁先谁后；环上任何一条边都没有先后可排，" +
               "所以这一版不替它「先插一行空外键、再补一句 UPDATE」—— 那是延迟写入那一档（§14）。" +
               "要么把环拆开（其中一端改成可空外键、由第二次保存补上），要么让环上至少一条边的主端本轮不欠语句。";
    }

    /// <summary>这只实例<em>实际</em>在哪一档（按引用问，不按调用点推断的泛型参数）。
    /// 只服务于一条更准的拒绝话：<c>DbContext.Entry&lt;T&gt;(entity)</c> 的 <c>T</c> 是从实参推出来的，
    /// 传一只派生实例就推出一个从没建过档的类型，于是那句"先 Set&lt;派生&gt;().Attach"会把人指向
    /// 一条<em>根本不该存在</em>的档（继承映射没接线，§8.2）—— 而实例其实好好地待在基类那一档里。</summary>
    internal ITypeTracker? OwnerOf(object entity)
    {
        foreach (var tracker in _order)
            if (tracker.Owns(entity))
                return tracker;

        return null;
    }

    ITypeTracker Tracker(Type entityType)
        => _byType.TryGetValue(entityType, out var tracker)
            ? tracker
            // 不可达：引擎手里那份 ChangeTracker 只可能来自 Set<T>()，而它先把档建好。
            // 走到这里说明上面那条不变量破了，这是内核缺陷，不是用户写错 —— 消息要按这个口径写。
            : throw new FastSqlException($"{entityType.Name} 在会话里没有跟踪档：物化路径被挂上了一个没有对应档位的跟踪器，" +
                "这是内核的不变量被破（Set<T>() 应当先建档再交给引擎），不是调用方写错。请带这个查询的 Explain() 报缺陷。");
}
