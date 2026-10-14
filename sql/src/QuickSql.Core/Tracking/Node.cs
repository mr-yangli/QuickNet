using QuickSql.Aop;
using QuickSql.Metadata;

namespace QuickSql.Tracking;

/// <summary>跟踪器里一个实例的状态。放在 Abstractions 那几个公开类型<em>背后</em>（本类型 internal）：
/// 调用方要的是 <see cref="EntityEntry"/>，而这里三张按列下标对齐的数组一旦公开，
/// 就成了"改一下数组就能伪造脏判定"的后门 —— 与 §10.1 把事件构造器关进 internal 是同一条理由。
///
/// <b>三张数组都按 <see cref="EntityContract.Columns"/> 的下标对齐</b>，不按名字：跟踪一万行时
/// 每次比较都要查一次字典的话，<c>DetectChanges</c> 的开销就从"读一遍字段"变成"读一遍字段 + 一万次哈希"。
/// 列名只出现在出入口（<see cref="EntityEntry.ModifiedMembers"/> 与报告）。</summary>
sealed class Node(object entity, int columnCount, int collectionCount)
{
    /// <summary>被跟踪的实例本体。身份映射与去重判的都是它的<b>引用</b>，所以这张表用的是引用相等比较器。</summary>
    public readonly object Entity = entity;

    /// <summary>最近一次"承认"的值（物化那一刻、<c>Attach</c>、或 <c>SaveChanges</c> 之后）。
    /// 比较判脏读的是它，<see cref="PropertyEntry.OriginalValue"/> 交的也是它。</summary>
    public object?[] Snapshot { get; } = new object?[columnCount];

    /// <summary>调用方对每一列<em>表过态</em>的 <c>IsModified</c>：<c>null</c> = 没说过，比较结论作数；
    /// <c>true</c>/<c>false</c> = 强制进/不出 <c>SET</c>。这就是 <see cref="PropertyEntry"/> 那句"三态"的存储形态。</summary>
    public bool?[] Overrides { get; } = new bool?[columnCount];

    /// <summary>本次要进 <c>SET</c> 的列（比较结论与覆盖的合成）。只在 <c>DetectChanges</c> 里被写。</summary>
    public bool[] Dirty { get; } = new bool[columnCount];

    /// <summary>每一根<em>集合边</em>上一次看见的那一串子实体的引用，按 <see cref="EntityContract.Collections"/>
    /// 的下标对齐（与上面三张数组按列下标对齐是同一条理由）。<b>没有集合边的类型上是 <c>null</c></b>，
    /// 所以跟踪一万行标量实体不会为这一格多付一只数组。
    ///
    /// <para>它是摘行那一档（裁决③）唯一的判据：<c>collection.Remove(x)</c> 既不发事件也不留痕迹，
    /// 会话要说出"这一只刚从那一串里被摘掉"，就只能拿自己上一次看见的那一串跟现在这一串比。
    /// <b>某一格为 <c>null</c> 是"这一串我从来没见过"</b> —— 与"见过且为空"（一只空列表）严格不同：
    /// 前者不判（不替没看见的东西编一份缺席的账），后者才判得出摘掉的是谁。裁决①那一条"捞不到就拒"
    /// 在这里翻过来用：捞不到也就<em>不摘</em>，而不是把整串当成没人。</para></summary>
    public List<object>?[]? Baselines { get; } = collectionCount > 0 ? new List<object>?[collectionCount] : null;

    public EntityState State { get; set; } = EntityState.Detached;

    /// <summary>它登记在身份映射里用的那个键；<b>null = 没登记</b>（主键还没值，或这个实体类型没有自增键可登记）。
    /// 存着它是为了能从字典里精确摘掉 —— 按当前值重算一次键的话，中途改过主键的实例会摘掉别人的那一条。</summary>
    public IdentityKey? Registered { get; set; }
}

/// <summary>跟踪器按实体类型切开的内部接缝。<b>存在的唯一理由是 <see cref="ChangeTracker"/> 手里只有
/// <see cref="Type"/> 而工作要按 <c>T</c> 做</b>：<c>InsertEngine&lt;T&gt;</c> 与
/// <c>IEntityTypeSchema&lt;T&gt;</c> 都是泛型的，而非泛型的调度层要是也带这些成员，就得为它们开
/// <c>MakeGenericMethod</c> —— 那正是 §1.2 原则 3 允许"加载期反射"但不允许"调用期反射"的分界线。
/// 所以本接口上<em>没有</em>一个成员需要知道 <c>T</c>，除了返回契约名字那几条。</summary>
interface ITypeTracker
{
    Type EntityType { get; }

    EntityContract Contract { get; }

    /// <summary>当前跟踪的实例数（含各状态）。</summary>
    int Count { get; }

    /// <summary>跟踪序那张表<em>本身</em>（不是拷贝）。<b>这一格只给内核的只读遍历用</b>：
    /// <c>DetectChanges</c> 一类的循环每轮都要按会话规模过一遍，而这里每拷一份就是一个 N 元素的引用数组 ——
    /// 三个调用点各拷一份的代价实测在 1 000 只那一档上是 24 KB 一次保存（§12.8 第 13 行的 ③ − ① 那一截）。
    /// <para><b>不拷贝换来的那条约束要一起读</b>：枚举期间不许改这张表。内核的六个调用点都满足 ——
    /// <c>DetectChanges</c> / <c>AnyDue</c> / <c>EnsureNoSplitWrites</c> / <c>WriteCluster</c> 只读节点，
    /// 而摘行那一档把「判」与「动手」分成两趟（理由写在 <c>ChangeTracker.DiffDetachedChildren</c> 上），
    /// 动手那一趟枚举的是自己攒的那份 <c>due</c> 而不是这张表。</para>
    /// <para>真需要"当时的快照"那一维的调用方自己拷：<c>ChangeTracker.Entries()</c> 要的是「调用方可以在枚举里
    /// <c>Remove</c>」这个公开承诺（<c>PostFlush</c> 用的也是同一手），<c>SnapshotEntities()</c> 要的是
    /// <c>Local</c> 那句"快照而不是活集合"。两处都付拷贝，因为它们<em>答的就是快照那道题</em>。</para></summary>
    IReadOnlyList<Node> TrackedNodes { get; }

    /// <summary>这个类型有没有"还没写出去或写的时候必须算一次"的东西。<c>SaveChanges</c> 用它跳过空桶，
    /// 于是只读过数据的会话不会为每个类型各扫三遍。</summary>
    bool MayHaveWrites { get; }

    int DetectChanges();

    void Clear();

    /// <summary>物化路径的登记口（§8.2 的身份映射）。<b>参数是 <see cref="object"/> 而不是 <c>T</c></b>：
    /// 调度层手里只有一个 <see cref="Type"/>，而它要在拿到 <c>EntityTracker&lt;T&gt;</c> 之前就把这一步分发出去。
    /// 实现侧第一句就是 <c>(T) entity</c> —— 静态类型不匹配是内核的不变量破了，不是调用方写错。</summary>
    Node Track(object entity);

    /// <summary>这一行在本档里<em>已经</em>是哪只实例；<b>没登记过返回 null，而且不登记</b>。
    /// 它挂在 <see cref="Track"/> 旁边是为了补一条 Track 补不到的问句：补载（<c>Load</c> /
    /// <c>IncludeMany</c>）把子实体缝进导航时只想知道"会话里那一行是哪一只"，
    /// 而<b>要不要把这一行收进会话不是这一步决定的</b>（那是 Track 的职权，见 §8.2 的建档口径）——
    /// 拿 Track 办这件事会顺手把没建档的类型撞响。</summary>
    object? Canonical(object entity);

    /// <summary>这一档里有没有<em>这个引用</em>（不取入口，所以不构造对象）。
    /// 存在的理由只有一条：<see cref="ChangeTracker"/> 要能说出"这只实例在哪一档"，
    /// 而不是让 <c>Entry</c> 拿推断出来的泛型参数去猜（派生实例会猜错，见 <c>DbContext.Entry{T}</c>）。</summary>
    bool Owns(object entity);

    /// <summary>这个引用在这一档<em>此刻</em>的状态；<b>null = 不在这一档里</b>。
    /// 摘行那一档用它把「已经在删的」与「从没进过会话的」分开：前者不必再判第二次（它本来就归这次删除），
    /// 后者才是要新登记成 <see cref="EntityState.Deleted"/> 的那一只 —— 拿 <see cref="Owns"/> 判分不开这两格。</summary>
    EntityState? StateOf(object entity);

    /// <summary>读一只实例的主键值（级联落键那一步要拿父的键）。挂在接口上又是那条「泛型参数只住在类型上」
    /// 的产物：读键要走 <c>IEntityTypeSchema&lt;T&gt;</c>，而调度层手里只有 <see cref="Type"/> 与一只装箱实例。</summary>
    object?[] KeyValuesOf(object entity);

    /// <summary>把父的键写进子行手里那几列外键（级联插入，§8.2）。<b>型不合要在这里说</b>：
    /// schema 的 setter 是强转的装箱写入，而主键列 <c>bigint</c> 配外键列 <c>int</c> 是合法 DDL ——
    /// 装不下的现场本来是一次 <c>InvalidCastException</c>，不是一句话。</summary>
    void SetForeignKeys(object entity, int[] ordinals, object?[] values);

    /// <summary>把已有的节点包成入口。<b>不做查找</b>：调度层已经按引用表拿到了节点，这里只补 <c>T</c>。
    /// 挂在接口上是因为 <see cref="ChangeTracker.Entries"/> 手里只有 <see cref="Node"/>，
    /// 而 <c>TrackedEntry&lt;T&gt;</c> 必须是泛型的 —— 又一条"泛型参数留在类型上"的产物。</summary>
    EntityEntry EntryOf(Node node);

    /// <summary>这一行此刻会不会被写出去（<see cref="EntityEntry.IsModified"/> 的口径）。</summary>
    bool WillWrite(Node node);

    /// <summary>本档有没有发出去、还没换成真实键的临时主键（§8.2 的 <c>Tp</c>）。
    /// 依赖方那一次改写<em>先</em>问这一句，所以零个临时键的会话一颗值都不必读。</summary>
    bool HasTempKeys { get; }

    /// <summary>依赖方手里那颗外键值问主端档：它是不是本档发的临时键、换出去的真实键是什么。
    /// 挂在接口上而不是让调用方自己查表，是因为读主端实体的键要 <c>IEntityTypeSchema&lt;T&gt;</c> ——
    /// 又一条"泛型参数只住在类型上"的产物（调度层手里只有 <see cref="Type"/> 与一个装箱值）。</summary>
    TempProbe ProbeTemp(object? value, out object? real);

    /// <summary>把本类型在这一档（Insert / Update / Delete）里欠的语句发出去，返回报告条目。
    /// <paramref name="traces"/> 是共享的钩子回放缓冲（<c>SaveChanges</c> 没有 <c>Explain()</c> 口，
    /// 这些条目今天没有消费者，但钩子必须照 §10.1 硬规则 1 跑，回放要落在同一条路径上）。</summary>
    Task<IReadOnlyList<SaveChangesOp>> WritePhaseAsync(WriteOperation phase, List<HookTrace> traces, CancellationToken ct);

    /// <summary>整批写成功之后结算：删的摘出会话，插的把库给的主键登记进身份映射，所有活着的重拍快照并转
    /// <see cref="EntityState.Unmodified"/>。分开成一步而不是写在 <see cref="WritePhaseAsync"/> 里，
    /// 是因为三档共用一个事务 —— 任何一档抛了都不能结算，否则"回滚了但会话以为写过了"。</summary>
    void PostFlush();
}

/// <summary>一颗临时主键（§8.2 的 <c>Tp</c>）此刻换不换得出真实键。四档而不是布尔：
/// 「这不是临时键」与「是，但换不出去」的处置完全相反 —— 前者一颗值都不许动（那是调用方自己填的），
/// 后者必须拒（写出去就是一个库里不存在的键）。把两格并成一格，要么静默改错值、要么静默写错值。</summary>
enum TempProbe
{
    /// <summary>不是本档发出去的临时键：这个值是调用方自己填的，改写路径不碰它。</summary>
    NotTemp,

    /// <summary>是临时键，而主端那一行已经拿到库里给的真实键（值在 <c>ProbeTemp</c> 的 <c>out</c> 里）。</summary>
    Resolved,

    /// <summary>是临时键，但主端那一行的键还没换出来：它本轮没欠语句，或者它与依赖方在同一张表的同一批里
    /// （自引用 —— 类型序排不出同一类型内部的先后，§14 的那一格）。</summary>
    Pending,

    /// <summary>是临时键，但它对应的那只实例已经离开会话：标删一只还没写出去的新增行等于忘掉它，
    /// 而依赖方手里那个引用还留着。</summary>
    Orphaned,
}
