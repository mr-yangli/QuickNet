using System.Data.Common;
using FastSql.Aop;
using FastSql.Execution;
using FastSql.Metadata;
using FastSql.Schema;
using FastSql.Sql;
using FastSql.Sql.Ast;
using FastSql.Write;

namespace FastSql.Tracking;

/// <summary>一个实体类型的跟踪器（§8.2 那张状态机的落地）。
///
/// <b>为什么按类型切一份</b>：脏判定要按列下标读写实体，写入要 <c>InsertEngine&lt;T&gt;</c> 与
/// <c>IEntityTypeSchema&lt;T&gt;</c>，这些都是泛型的。把泛型参数留在<b>类型</b>上而不是<b>方法</b>上，
/// 调度层（<see cref="ChangeTracker"/>）就只需要一张 <c>Type → ITypeTracker</c> 的表，
/// 全程不需要 <c>MakeGenericMethod</c>（§1.2 原则 3：加载可以反射，调用不行）。
/// 那 <c>T</c> 是怎么进来的？只有泛型调用点 —— <c>db.Set&lt;T&gt;()</c>、<c>db.Add&lt;T&gt;()</c>、
/// <c>db.Entry&lt;T&gt;()</c>，编译器已经知道它，不需要运行期问。
///
/// <b>这一份里没有事务也没有连接</b>：三档写入由 <see cref="DbContext"/> 在一条事务里依次驱动，
/// <see cref="WritePhaseAsync"/> 与 <see cref="PostFlush"/> 分成两步就是为此 —— 任何一档抛了都不能结算，
/// 否则会得到「回滚了，但会话以为写过了」。</summary>
sealed class EntityTracker<T> : ITypeTracker where T : class
{
    readonly FastSqlOrm _orm;
    readonly EntityContract _contract;
    readonly IEntityTypeSchema<T> _schema;
    readonly ColumnContract[] _columns;

    /// <summary>主键列的下标，顺序即 <see cref="EntityContract.PrimaryKey"/> 的列序（复合主键按契约声明序）。</summary>
    readonly int[] _keyOrdinals;

    /// <summary>这一列能不能进 <c>SET</c>。排除主键（改它的语义是「删一行 + 加一行」，见
    /// <see cref="EntityEntry.State"/> 那一段）、自增列（库里那一列不接受写，那是 <c>IDENTITY_INSERT</c> 那条路）
    /// 与并发列（§8.2 的只比较不书写，值由库给）。
    /// <b>三者都是「响了」而不是「剔掉」</b>：判脏了却不能进语句是一件事，静默剔掉的后果是
    /// 「改了主键保存三次没生效」，那查不出来。</summary>
    readonly bool[] _writable;

    /// <summary>并发列的下标（列序）。空数组就是「这个实体没有并发列」，于是三档写入走的还是 v0.17 那条路：
    /// 普通语句、<c>NonQueryAsync</c>、0 行只进报告。所有分岔都从这一个数组的长度起，
    /// 所以没有并发列的实体一条语句都不多、一颗参数都不多。</summary>
    readonly int[] _tokens;

    /// <summary>应用自维护的并发列（如 <c>int Version</c>），与库维护的（如 SQL Server <c>rowversion</c>）相对。
    /// 前者要在 SET 里写 <c>Version = Version + 1</c>，写完后直接在实体上 +1；后者靠 OUTPUT/RETURNING 回读。
    /// 空数组就是「没有应用自维护的并发列」，于是那一档 SET 与实体回写都不跑。</summary>
    readonly int[] _appMaintainedTokens;

    /// <summary>库维护的并发列（SQL Server <c>rowversion</c> 等），需要 OUTPUT/RETURNING 回读新值。
    /// 空数组就是「没有库维护的并发列」，于是 OUTPUT/RETURNING 子句与回读逻辑都不跑。</summary>
    readonly int[] _storeFilledTokens;

    /// <summary>库维护并发列的契约本体，顺序与 <see cref="_storeFilledTokens"/> 一致 —— 它是 OUTPUT/RETURNING 的列序。</summary>
    readonly ColumnContract[] _storeFilledTokenColumns;

    readonly TableRef _table;

    /// <summary>合并语句里那一份字面行集的引用（§12.5）。它<em>不是</em>一张表：<c>_table</c> 是库里那一行，
    /// 这一只是"这一批要写进去的值"，两者靠 <c>ON</c> 上的主键对上。别名固定 <c>t1</c>，
    /// 因为一条语句里只有这两个表源参与（<c>FastSqlOrm.AliasAt</c> 那一式）。
    /// 单行桶不引用它，所以那些语句里连 <c>t1</c> 都不出现。</summary>
    readonly TableRef _rowSet;

    readonly int _identifierChars;

    /// <summary>会话那一侧的调度器。回来路只为一件事：临时主键的账记在<em>主端</em>那一档，
    /// 而拿着临时值的是<em>依赖方</em>那一档（§8.2 的 <c>Tp</c>），两边要能对上话。
    /// 走 <see cref="ChangeTracker"/> 而不是让各档互相持有引用，是因为依赖是按 <see cref="Type"/> 说的。</summary>
    readonly ChangeTracker _session;

    /// <summary>本型每根关系边的外键列下标（<see cref="EntityContract.Relationships"/> 换算到列序）。
    /// <b>没有关系边时是 <c>null</c></b>，于是那条改写路径在没有关系的类型上一次都不跑。
    /// 边的目标是 <see cref="Type"/> 而不是档：拿着目标类型去问会话，才不需要在这里出现 <c>T</c>。</summary>
    readonly (Type Target, int[] Ordinals)[]? _fkEdges;

    readonly Dictionary<IdentityKey, Node> _identity = new();
    readonly Dictionary<object, Node> _byRef = new(ReferenceEqualityComparer.Instance);
    readonly List<Node> _order = [];

    /// <summary>临时主键 → 那一只节点（§8.2 的 <c>Tp</c>）。
    /// <b>键是规范化成 <c>long</c> 的临时值而不是原始装箱值</b>：依赖方的外键列与主端的主键列
    /// 可以一个是 <c>int</c> 一个是 <c>long</c>（那是合法 DDL），而装箱相等判的是"同型同值" ——
    /// 差一型就查不到，那颗负数就会被静默写进库。懒建：没发过临时键的类型连一个字典都不占。</summary>
    Dictionary<long, Node>? _temps;

    /// <summary>临时键的发号器，只朝负数走。不随保存归零：换过一轮号就重放一遍，而"这个负数其实是某只
    /// 实例自己填的键"这种碰撞正好是重放能造出来的。</summary>
    long _tempCursor;

    readonly bool _hasIdentity;

    bool _mayHaveWrites;

    public EntityTracker(FastSqlOrm orm, ChangeTracker session)
    {
        _orm = orm;
        _session = session;
        _schema = orm.Schemas.Schema<T>();
        _contract = _schema.Contract;
        _columns = _contract.Columns;

        var keys = new List<int>();
        var tokens = new List<int>();
        var writable = new bool[_columns.Length];

        for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
        {
            var column = _columns[ordinal];

            if (column.IsPrimary)
                keys.Add(ordinal);

            if (column.IsConcurrencyToken)
                tokens.Add(ordinal);

            _hasIdentity |= column.IsIdentity;

            writable[ordinal] = !column.IsPrimary && !column.IsIdentity && !column.IsConcurrencyToken;
        }

        _keyOrdinals = [.. keys];
        _writable = writable;
        _tokens = [.. tokens];

        // 并发列分两档：库维护的（StoreFillsValue，如 SQL Server rowversion）靠 OUTPUT/RETURNING 回读；
        // 应用自维护的（如 int Version）要在 SET 里写 Version = Version + 1，写完后实体直接 +1。
        // 两档不重叠、合起来等于 _tokens 全集。
        // 应用自维护只认整型（int/long/short/byte）：非整型的并发列（如 datetime2 时间戳）即使 StoreFillsValue = false
        // 也走 OUTPUT/RETURNING 回读，因为对它们做 +1 没有语义。这是向后兼容：v0.18 之前所有并发列都走回读。
        var storeFilled = new List<int>();
        var appMaintained = new List<int>();

        foreach (var ordinal in _tokens)
        {
            var column = _columns[ordinal];

            if (column.StoreFillsValue || !IsIntegerClrType(column.ClrType))
                storeFilled.Add(ordinal);
            else
                appMaintained.Add(ordinal);
        }

        _storeFilledTokens = [.. storeFilled];
        _appMaintainedTokens = [.. appMaintained];
        _storeFilledTokenColumns = [.. _storeFilledTokens.Select(ordinal => _columns[ordinal])];
        // 目标怎么引用自己由方言定（§4 的 WriteTargetAlias）：T-SQL 与 PG 系给一个独立别名 t0，
        // Oracle 血统不接受目标别名、只能用表名本身。这一颗要问方言而不是现场写死，因为同一份 _table
        // 既进 UPDATE 的 SET/WHERE，也进冲突重读那一条 SELECT（下面那一格）—— 两处必须同名。
        _table = new TableRef(0, _orm.Provider.Dialect.WriteTargetAlias(_contract.Table), _contract.Table, _contract);
        _rowSet = new TableRef(1, FastSqlOrm.AliasAt(1), _contract.Table);

        // 边的外键成员名换算成列下标，只在启动期这一次：改写路径每行每列都要用，而契约只按名字说话。
        // ByMember 认不出这些名字是内核不变量破了（§10.3 的关系解析判过：约定命中的必须是已映射列，
        // 显式点名的没映射则在解析期就拒），所以这里不判、让它按自己的形状响。
        if (_contract.Relationships.Count > 0)
        {
            var edges = new (Type, int[])[_contract.Relationships.Count];

            for (var i = 0; i < edges.Length; i++)
            {
                var relationship = _contract.Relationships[i];
                var ordinals = new int[relationship.MemberNames.Count];

                for (var column = 0; column < ordinals.Length; column++)
                    ordinals[column] = Array.IndexOf(_columns, _contract.ByMember[relationship.MemberNames[column]]);

                edges[i] = (relationship.TargetType, ordinals);
            }

            _fkEdges = edges;
        }

        // 预容量（§12.7 E）：一条逐行 UPDATE 的标识符就是表名 + 别名 + 主键列名那几处。
        // 并发列在 WHERE 与 OUTPUT 里各出现一次，所以按两列的量一起算进去。
        _identifierChars = _contract.Table.Name.Length + _table.Alias.Length * 3 + (_columns.Length + _tokens.Length) * 16 + 128;
    }

    public Type EntityType => typeof(T);

    public EntityContract Contract => _contract;

    public int Count => _order.Count;

    public IReadOnlyList<Node> Nodes => [.. _order];

    /// <remarks>只在「变脏 / 新增 / 删除」时置真，之后不再置假（<see cref="Clear"/> 与 <see cref="PostFlush"/>
    /// 例外）。于是它可以偶然多 true 一次：<c>SaveChanges</c> 那一档扫到零个节点，返回空报告。
    /// 反方向的错（该写而没写）才是不能接受的，所以这个位只朝安全的方向偏。</remarks>
    public bool MayHaveWrites => _mayHaveWrites;

    public bool HasTempKeys => _temps is { Count: > 0 };

    /// <summary>依赖方手里那颗值问这一档：它是不是本档发的临时键、换得出的真实键是哪一颗。
    /// <b>判"换不出"靠的是节点手里的键此刻还是不是那个临时值</b>，而不是查某张"已回填"的表：
    /// 回填由 <c>InsertEngine</c> 直接写进实体（§12.4 第 1 项那条位置映射），实体本身就是那一张表。
    /// 节点已经不在引用表里就是孤的 —— 标删一只还没写出去的新增行等于忘掉它，而引用还留在依赖方手里。</summary>
    public TempProbe ProbeTemp(object? value, out object? real)
    {
        real = null;

        // 临时键恒为负整数，所以这一判就够：非整型（Guid 键、字符串键）与没填（null）都不是本档发出去的东西。
        var candidate = Signed(value);

        if (_temps is null || candidate is null)
            return TempProbe.NotTemp;

        if (!_temps.TryGetValue(candidate.Value, out var node))
            return TempProbe.NotTemp;

        if (!_byRef.TryGetValue(node.Entity, out var held) || !ReferenceEquals(held, node))
            return TempProbe.Orphaned;

        // 与查号同一个尺子：主端那一列是 bigint、依赖方那一列是 int 时，装出来的箱是两个型，
        // 而"还是不是当初那颗"判的是数值。按装箱相等判会把"还没换"读成"换出来了"，然后原样写回去。
        var current = KeyValues((T) node.Entity)[0];

        if (current is null || Unset(current) || Signed(current) == candidate)
            return TempProbe.Pending;

        real = current;

        return TempProbe.Resolved;
    }

    /// <summary>带符号整型的规范化读数（<see cref="_temps"/> 那把 <c>long</c> 尺子的出处），装不下就返回 <c>null</c>。
    /// <b><c>short</c>／<c>byte</c> 那一档不在这里</b>：临时键只会由 <c>int</c>/<c>long</c> 主键发出去，
    /// 而一根 <c>short</c> 外键要捏住它得先自己写一次强制转换 —— 那种列在探测这里就被当成"不是我发的号"放过去，
    /// 不是被换掉也不是被拒（这一格记在 §14）。</summary>
    static long? Signed(object? value) => value switch
    {
        int small => small,
        long wide => wide,
        _ => null,
    };

    // ── 入口：物化 / Add / Attach / Update / Remove ────────────────────

    /// <summary>物化路径（§8.2 的 IdentityMap）。返回会话里<em>该拿的那一只</em>实例：
    /// 同引用已跟踪 → 原样；同键已跟踪 → 那一只，新副本丢弃；都不认识 → 登记为 <see cref="EntityState.Unmodified"/>。
    /// <b>不覆盖已有实例的值</b>：那只可能已经被改过而还没保存，用库里的值盖回去等于擦掉用户的改动。
    /// 「重新读一遍库并让库赢」是 <c>Refresh</c> 的活，还没接线（§14 的 P1 余项）。</summary>
    public Node Track(object entity)
    {
        RequireKey();

        if (_byRef.TryGetValue(entity, out var tracked))
            return tracked;

        var typed = (T) entity;
        var values = KeyValues(typed);

        if (IsDefinite(values))
        {
            var key = new IdentityKey(values);

            if (_identity.TryGetValue(key, out var canonical))
                return canonical;

            return Register(entity, EntityState.Unmodified, key, typed);
        }

        // 主键还没值：这一行不进身份映射，否则两只都没填键的新实例会被认成同一只。
        // 「同一个键被新增两次」那一档由 Tp 接住了（Add 会发临时键，见 <see cref="AssignTempKey"/>），
        // 而物化路径不动：库里递出来的行本来就该有键，没键是库表设计的问题，不替它编一个。
        return Register(entity, EntityState.Unmodified, null, typed);
    }

    /// <summary>标为新增。<b>与 <see cref="Track"/> 的分别</b>：这里「同键已有另一只实例」要抛而不是丢弃 ——
    /// 物化时库把同一行递两次，去重是对的；用户把同一个键递两次，那是他手里有两只对象，
    /// 静默选一只就是 §12.2 拒绝的那种无痕。</summary>
    public Node Add(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        RequireKey();

        var typed = (T) entity;

        if (_byRef.TryGetValue(entity, out var known))
        {
            if (known.State == EntityState.Deleted)
                throw new FastSqlException($"{_contract.Table.Name} 的这个实例已经被标成删除，不能再标成新增：同一个实例上的两种意图没有一种顺序能同时执行。要从头插就新建一个实例。");

            return known;
        }

        var values = KeyValues(typed);
        IdentityKey? key = null;

        if (IsDefinite(values))
        {
            var candidate = new IdentityKey(values);

            if (_identity.TryGetValue(candidate, out var clash))
                throw new FastSqlException(Collision(clash, values));

            key = candidate;
        }

        var node = Register(entity, EntityState.Added, key, typed);

        if (key is null)
            AssignTempKey(node, typed, values);

        return node;
    }

    /// <summary>给一只「键还没值」的新增实例发一个临时主键（§8.2 的 <c>Tp</c>）—— 于是"子实体先 <c>Add</c>、
    /// 父实体的键等库给"那一种引用终于有个东西可以捏着，而不是写出去一个空外键。
    ///
    /// <para><b>只服务一种形状：单列、带符号整型、自增的主键。</b>自增意味着那一列的值本来就要等
    /// <c>INSERT</c> 给回来（<see cref="InsertEngine{T}"/> 的回填档），而负数意味着手里这颗不可能是库里的键。
    /// 客户端生成的键（<c>Guid</c> 那一派）两种都不满足：那种键该由调用方填，空着是填错了而不是"还没生成"，
    /// 库也不会在回填时把它给回来 —— 那一档记在 §14，不在这里蒙。</para>
    ///
    /// <para><b>临时值进实体、不进身份映射</b>：节点的 <c>Registered</c> 仍是 <c>null</c>，所以
    /// <c>Find(那个负数)</c> 查不出东西；快照也保持"没填"那一格（发号在 <see cref="Register"/> 之后），
    /// 于是 <c>OriginalValue</c> 说的仍是"这一行库里没有"。写出去的语句永远不带它：自增列不进列清单，
    /// 而依赖方手里那一颗在语句构建之前被换掉 —— <b>换不掉就拒，任何情况下都不让临时值进库</b>
    /// （<see cref="ResolveTempKeys"/>）。</para></summary>
    void AssignTempKey(Node node, T entity, object?[] values)
    {
        if (!_hasIdentity || _keyOrdinals.Length != 1)
            return;

        var type = Nullable.GetUnderlyingType(_columns[_keyOrdinals[0]].ClrType) ?? _columns[_keyOrdinals[0]].ClrType;

        if (type != typeof(int) && type != typeof(long))
            return;

        // 再认一次手里那颗值：0 / Guid.Empty 才是"没填"，别的都是调用方自己选的键，不抢他的号。
        if (values[0] is not int and not long || !Unset(values[0]!))
            return;

        var temps = _temps ??= [];

        long candidate;

        do
            candidate = --_tempCursor;
        while (temps.ContainsKey(candidate) || _identity.ContainsKey(new IdentityKey([Box(candidate, type)])));

        temps[candidate] = node;
        _schema.SetValue(entity, _keyOrdinals[0], Box(candidate, type));
    }

    static object Box(long value, Type type) => type == typeof(long) ? value : (int) value;

    /// <summary>纳入会话并承认为「库里已有、当前值即库值」。主键必须有值：没有值就没有 <c>WHERE</c>，
    /// 之后任何一次改动都会变成一句定位不到的 UPDATE。</summary>
    public Node Attach(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        RequireKey();

        var typed = (T) entity;

        if (_byRef.TryGetValue(entity, out var known))
            return known;

        var values = RequireDefinite(typed, "Attach");
        var key = new IdentityKey(values);

        if (_identity.TryGetValue(key, out var clash))
            throw new FastSqlException(Collision(clash, values) + " Attach 不会替换会话里的那一只：要丢弃它先 Entry(那只).State = Detached。");

        return Register(entity, EntityState.Unmodified, key, typed);
    }

    /// <summary>承认为「库里已有」并且<em>每一列都算脏</em> —— §8.3 那句
    /// <c>db.Attach(order); db.Entry(order).State = Modified</c> 的合成形态，也就是「我知道它变了，别比了，全写」。</summary>
    public Node Update(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var node = Attach(entity);

        if (node.State == EntityState.Added)
            throw new FastSqlException($"{_contract.Table.Name} 的这个实例已经在会话里被标为新增：Update 会把意图改成按主键 UPDATE，而那一行可能根本还没进库。" +
                "要么让它保持新增，要么先 Entry(...).State = Detached 再重来。");

        ApplyState(node, EntityState.Modified);

        return node;
    }

    /// <summary>标为删除。没跟踪过的实例也纳入再标（与同类库同形），所以它同样要求主键有值。
    /// <b>唯一的例外是「本来就还是新增」</b>：库里没有这一行，删它的正确执行方式是忘掉它，不是发一句 <c>DELETE</c>。</summary>
    public Node Remove(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        RequireKey();

        var typed = (T) entity;

        if (_byRef.TryGetValue(entity, out var node))
        {
            if (node.State == EntityState.Added)
            {
                Detach(node);

                return node;
            }

            node.State = EntityState.Deleted;
            _mayHaveWrites = true;

            return node;
        }

        var values = RequireDefinite(typed, "Remove");
        var key = new IdentityKey(values);

        if (_identity.TryGetValue(key, out var clash))
            throw new FastSqlException(Collision(clash, values) + " 要删的是会话里那一只：对它调用 Remove。");

        return Register(entity, EntityState.Deleted, key, typed);
    }

    /// <summary>这只引用是不是在本档里。<see cref="ChangeTracker.OwnerOf"/> 用它把 <c>Entry</c> 的拒绝说准。</summary>
    public bool Owns(object entity) => _byRef.ContainsKey(entity);

    /// <summary>取入口。<b>不代替 Attach</b>：规格 §8.3 的写法本来就是「先 Attach 再改 State」，
    /// 而「查一个入口顺手把实体纳入会话」是一种没人说过的写入意图。
    /// 返回类型是<em>泛型的那一档</em>：<c>Entry(o).Property(o =&gt; o.Note)</c> 里那个收 lambda 的重载长在
    /// <see cref="EntityEntry{T}.Property{TProp}"/> 上，退回 <see cref="EntityEntry"/> 就只剩收字符串的那一条，
    /// §8.2 的"精确控制"会变成一句写不出来的话（名字笔误也要等到运行期才现形）。</summary>
    public EntityEntry<T> Entry(object entity)
    {
        if (entity is not T typed)
            throw new FastSqlException($"{_contract.Table.Name} 的跟踪器收到的不是 {typeof(T).Name} 而是 {entity.GetType().Name}：派生实例要按<em>基类</em>取档（Set<{typeof(T).Name}>().Entry(...)），" +
                "各占一份契约是继承映射的事，P2/P3 才接线（§8.2 的按类型切开）。");

        if (!_byRef.TryGetValue(typed, out var node))
            throw new FastSqlException($"{typeof(T).Name} #{Text(KeyValues(typed))} 不在会话里：先 Set<{typeof(T).Name}>().Attach(...) 或 Add(...)，再取 Entry（§8.3）。");

        return new TrackedEntry<T>(this, node);
    }

    // ── 状态与侦测 ────────────────────────────────────────────────────

    /// <summary><see cref="EntityEntry.State"/> 的落地。四个赋值各有各的连带效果，集中写在这里而不是散到各个入口，
    /// 是因为 <see cref="DetectChanges"/> 与显式赋值必须走同一套规则 —— 否则「显式 Modified 之后再侦测」会掉回
    /// <see cref="EntityState.Unmodified"/>，那就是把用户明说的「全写」撤销了。</summary>
    public void ApplyState(Node node, EntityState next)
    {
        if (next == node.State)
            return;

        switch (next)
        {
            case EntityState.Detached:
                Detach(node);

                return;

            case EntityState.Unmodified:
                // 「承认当前值就是库里的值」：重拍快照 + 清覆盖 + 清脏。
                // 不重拍的话，下一次侦测会拿旧快照把这一行立刻判回脏 —— 那这句赋值就只是「再说一遍」。
                node.State = next;
                Reset(node, (T) node.Entity);

                return;

            case EntityState.Modified:
                RequireKey();
                RequireDefinite((T) node.Entity, "标为 Modified");
                node.State = next;
                MarkAllWritable(node);
                _mayHaveWrites = true;

                return;

            case EntityState.Deleted:
                RequireKey();

                if (node.State == EntityState.Added)
                    Detach(node);
                else
                    node.State = next;

                _mayHaveWrites = true;

                return;

            case EntityState.Added:
                if (node.Registered is not null)
                    throw new FastSqlException($"{_contract.Table.Name} 的主键 {Text(KeyValues((T) node.Entity))} 在会话里已经被当成库里已有的一行：标成新增就是要再插一行同键的，库里必然拒绝。" +
                        "要插新行就给它一个新键（自增列的话新建一只实例，把键留在 0）。");

                node.State = next;
                _mayHaveWrites = true;

                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(next), next, "未知 EntityState。");
        }
    }

    /// <summary>逐列比较 + 覆盖合成（§8.2「脏列合成」里「发现」那一半）。
    /// 返回本次改了状态的实例数，0 就是「这一轮什么都没变」——测试与日志要的就是这个数。</summary>
    public int DetectChanges()
    {
        var changed = 0;

        foreach (var node in _order)
        {
            // 新增与删除不由比较决定：新增的行库里还没有值可比，删除的行整行都不进 SET。
            if (node.State is EntityState.Added or EntityState.Deleted)
                continue;

            var entity = (T) node.Entity;
            var any = false;

            for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
            {
                var differs = !Equals(_schema.GetValue(entity, ordinal), node.Snapshot[ordinal]);
                var dirty = node.Overrides[ordinal] ?? differs;

                node.Dirty[ordinal] = dirty;
                any |= dirty;
            }

            // 「改了又改回来」在这里回到 Unmodified，所以「曾经碰过」不会变成一次多余的 UPDATE。
            var next = any ? EntityState.Modified : EntityState.Unmodified;

            if (next == node.State)
                continue;

            node.State = next;
            changed++;

            if (any)
                _mayHaveWrites = true;
        }

        return changed;
    }

    /// <summary>清数据。<b>顺带把每个节点的状态归到 <see cref="EntityState.Detached"/></b>，
    /// 因为 <see cref="DbChangeTracker.Clear"/> 那一格对调用方的承诺就是"所有实例转 Detached"，
    /// 而手里那只 <see cref="EntityEntry"/> 是视图不是拷贝 —— 不改它的话这句承诺只写在文档里。</summary>
    public void Clear()
    {
        foreach (var node in _order)
            node.State = EntityState.Detached;

        _identity.Clear();
        _byRef.Clear();
        _order.Clear();
        _temps?.Clear();
        _mayHaveWrites = false;
    }

    /// <summary>内存视图的快照（<c>Local.Entries</c>）。含已标删的实例 —— 它们确实还在会话里，
    /// 直到那一句 <c>DELETE</c> 执行完（<see cref="LocalView{T}"/> 上方那段说的就是这件事）。</summary>
    public IReadOnlyList<T> SnapshotEntities() => [.. _order.Select(node => (T) node.Entity)];

    /// <summary>按主键在内存里找（<c>Local.Find</c>）。<b>只看得到进过身份映射的那些</b>：
    /// 键还没值的实例按规则不在映射里，所以这里查不到，而它确实在会话里。</summary>
    public T? Find(object?[] values)
    {
        if (values.Length != _keyOrdinals.Length)
            throw new FastSqlException($"{_contract.Table.Name} 的主键有 {_keyOrdinals.Length} 列，这里给了 {values.Length} 个值。");

        return _identity.TryGetValue(new IdentityKey(values), out var node) ? (T) node.Entity : null;
    }

    internal PropertyEntry Property(Node node, string memberName)
    {
        if (!_contract.ByMember.TryGetValue(memberName, out var column))
            throw new FastSqlException($"{_contract.Table.Name} 没有名为 {memberName} 的列：导航属性与未映射的计算成员没有脏标记可言（§10.2 的映射层不认它）。");

        return new TrackedProperty<T>(this, node, Array.IndexOf(_columns, column), column);
    }

    /// <summary>给 <see cref="TrackedProperty{T}"/> 用的三扇小窗：读写实体上的一格，以及"有人强制标脏了"这个通知。
    /// 走方法而不是把 <c>_schema</c> 暴露出去，是为了让快照那三张数组只有一个写入点（本类）。</summary>
    internal object? Value(T entity, int ordinal) => _schema.GetValue(entity, ordinal);

    internal void Write(T entity, int ordinal, object? value) => _schema.SetValue(entity, ordinal, value);

    internal void MarkWrites() => _mayHaveWrites = true;

    /// <summary>用库里的最新值覆盖实体并重置快照（<c>Refresh</c> 的落地）。
    /// 不在会话里的实例先登记为 <see cref="EntityState.Unmodified"/>；已在的按状态分别处理 ——
    /// <see cref="EntityState.Added"/> 与 <see cref="EntityState.Modified"/> 不动（用户有未保存的改动，库值不该覆盖），
    /// 其余状态重拍快照、清脏、清覆盖。</summary>
    internal void ApplyFreshValues(object entity, object?[] freshValues)
    {
        RequireKey();

        var typed = (T) entity;

        for (var i = 0; i < _columns.Length; i++)
            _schema.SetValue(typed, i, freshValues[i]);

        if (_byRef.TryGetValue(entity, out var node))
        {
            if (node.State is not (EntityState.Added or EntityState.Modified))
                ApplyState(node, EntityState.Unmodified);
        }
        else
        {
            var values = KeyValues(typed);
            var key = IsDefinite(values) ? new IdentityKey(values) : (IdentityKey?) null;

            Register(entity, EntityState.Unmodified, key, typed);
        }
    }

    /// <summary>这一行此刻会进 <c>SET</c> 的列（脏 且 可写），按列序、给成员名。
    /// <b>与 <see cref="Node.Dirty"/> 的分别是后者还包含主键那几列</b> —— 而主键进不了语句，
    /// 所以「这一列被判定改过了」与「这一列会被写出去」是两句话，前者由侦测回答，后者由这里回答。</summary>
    internal IReadOnlyList<string> ModifiedMembers(Node node)
    {
        List<string>? names = null;

        for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
        {
            if (!node.Dirty[ordinal] || !_writable[ordinal])
                continue;

            (names ??= []).Add(_columns[ordinal].MemberName);
        }

        return names is null ? [] : names;
    }

    /// <summary>这一行此刻会不会被写出去（<see cref="EntityEntry.IsModified"/> 与 <see cref="DbChangeTracker.HasChanges"/>
    /// 共用的判据）。<b><see cref="EntityState.Unmodified"/> 也要算</b>：
    /// <c>Property(...).IsModified = true</c> 写下去的是覆盖位，而状态要等下一次 <see cref="DetectChanges"/> 才被推成
    /// <see cref="EntityState.Modified"/> —— 只看 Modified 的话，关掉自动侦测的调用方在
    /// <c>if (HasChanges) SaveChanges()</c> 这一句上会得到 <c>false</c>，于是那次强制写入被静默跳过。
    /// <see cref="EntityState.Modified"/> 而 SET 为空（有人把每一列都标成不脏）算"不会"，
    /// 与 <see cref="WriteUpdatesAsync"/> 的跳过是同一条判据。
    ///
    /// <para><b>判据是"有没有任何一列脏"，不是"有没有一列脏且进得了 <c>SET</c>"</b>。用后者会更"准"，
    /// 但它把 <see cref="Unwritable"/> 那一档拒写变成走不到的死路：<c>SaveChanges</c> 先看这一句决定要不要开事务，
    /// 只脏了一列主键（或并发列）的节点在"能不能进 SET"那把尺子下是干净的，于是整次保存提前返回空报告，
    /// 那句「改了主键保存三次没生效」就真的没生效了 —— 而这里是唯一能听见它的地方。
    /// 所以这一格答的是"这一行有没有欠着一次处理"：能发语句是一种，必须拒的是另一种，两者都不该被跳过。</para>
    ///
    /// <para>代价方向跟着同一句话走：这一句为真而 SET 为空时，<see cref="WriteUpdatesAsync"/> 要么抛、要么整节点跳过，
    /// 都不会多发一条语句。</para></summary>
    public bool WillWrite(Node node) => node.State switch
    {
        EntityState.Added or EntityState.Deleted => true,
        EntityState.Modified or EntityState.Unmodified => AnyDirty(node),
        _ => false,
    };

    static bool AnyDirty(Node node)
    {
        foreach (var dirty in node.Dirty)
            if (dirty)
                return true;

        return false;
    }

    public EntityEntry EntryOf(Node node) => new TrackedEntry<T>(this, node);

    // ── 三档写入 ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SaveChangesOp>> WritePhaseAsync(WriteOperation phase, List<HookTrace> traces, CancellationToken ct)
        => phase switch
        {
            WriteOperation.Insert => await WriteInsertsAsync(traces, ct).ConfigureAwait(false),
            WriteOperation.Update => await WriteUpdatesAsync(traces, ct).ConfigureAwait(false),
            _ => await WriteDeletesAsync(traces, ct).ConfigureAwait(false),
        };

    /// <summary>把依赖方手里那些临时外键换成主端此刻的真实键（§8.2 的 <c>Tp</c> 回填）。
    ///
    /// <para><b>必须跑在语句构建之前</b>：这一格合上的正是"计划里带着 −1"，跑在后面就是先把错值发出去了。
    /// 而它跑得动，靠的是 v0.20 那一条拓扑序 —— 主端档的 Insert 排在依赖方之前 <c>await</c> 完，
    /// 所以此刻主端的键要么已经从库里回来，要么这一轮压根没写它（后者只能拒）。</para>
    ///
    /// <para>改动落在<em>实体</em>上而不只是落在参数上：那只实例之后还要被读、被比对、被下一次保存写，
    /// 留一颗负数在它手里就是留一颗定时炸弹。快照不用跟着改 —— <see cref="PostFlush"/> 会重拍。</para>
    ///
    /// <para><b>Update 档也要过一遍</b>：形状是"某只已在库里的行，外键那一格被赋成了主端还没换出去的临时键"
    /// （用户从 <c>parent.Id</c> 抄的值），那一句 <c>SET [CustomerId] = -1</c> 与插入档一样是写一个库里没有的键。</para></summary>
    void ResolveTempKeys(EntityState state)
    {
        if (_fkEdges is null)
            return;

        // 会话里没有临时键 = 这条路径一次都不该付钱（绝大多数保存连一个 Tp 都没发出去过）。
        if (!_session.HasPendingTemps)
            return;

        foreach (var node in _order)
        {
            if (node.State != state)
                continue;

            var entity = (T) node.Entity;

            foreach (var (target, ordinals) in _fkEdges)
                foreach (var ordinal in ordinals)
                {
                    var value = _schema.GetValue(entity, ordinal);

                    // 临时键恒为负整数：Guid / 字符串 / 可空列里的 null 都直接跳过，不必为它们问一遍。
                    if (value is not (int or long))
                        continue;

                    var probe = _session.ProbeTemp(target, value, out var real);

                    if (probe == TempProbe.NotTemp)
                        continue;

                    if (probe != TempProbe.Resolved)
                        throw UnresolvedTempKey(ordinal, target, probe, value);

                    _schema.SetValue(entity, ordinal, AdaptKey(real!, _columns[ordinal]));
                }
        }
    }

    /// <summary>把主端的真实键落成依赖方那一列装得下的型。外键列与主键列<em>可以</em>不同型
    /// （<c>bigint</c> 主键配 <c>int</c> 外键是合法 DDL），而 schema 的 setter 是强转的装箱写入 ——
    /// 型不对的现场是一次 <c>InvalidCastException</c> 而不是一句话，所以换型在这里做、装不下也在这里说。
    /// <b>只处理 <c>int</c> 与 <c>long</c> 两型，因为只有这两型的列到得了这里</b>：来路是发号那一档（只认这两型）
    /// 加探测那一档（<see cref="Signed"/> 也只认这两型），所以一根 <c>short</c> 外键不会走到这句"不替它截断"，
    /// 它会被当成"不是我发的号"原样放过去（§14 的那一格）。真正触发这句的是 <c>long</c> 主键配 <c>int</c> 外键、
    /// 而库里那个键已经大过 <c>int.MaxValue</c>。</summary>
    object AdaptKey(object real, ColumnContract column)
    {
        var value = real is int narrow ? (long) narrow : (long) real;
        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (type == typeof(long))
            return value;

        if (type != typeof(int) || value is < int.MinValue or > int.MaxValue)
            throw new FastSqlException($"{_contract.Table.Name} 的 {column.MemberName}（列 {column.ColumnName}）是 {type.Name}，" +
                $"装不下主端给回来的键 {value}（{real.GetType().Name}）：外键列比主键列窄或者不是整型，这一版不替它截断 —— 把这两列做成同型。");

        return (int) value;
    }

    FastSqlException UnresolvedTempKey(int ordinal, Type target, TempProbe probe, object? value)
    {
        var column = _columns[ordinal];

        var why = probe switch
        {
            TempProbe.Orphaned => "对应的那只实例已经离开会话（标删一只还没写出去的新增行等于忘掉它，而引用还留在这一行手里）",
            _ when target == typeof(T) => "与本行在同一张表的同一批里，而类型序排不出同一类型内部的先后（自引用那一档欠在 §14）",
            _ => "本轮没有写出去，所以那个临时键换不出真实值",
        };

        return new FastSqlException($"{_contract.Table.Name} 的 {column.MemberName}（列 {column.ColumnName}）此刻是临时主键 {value}，" +
            $"而它指向的 {target.Name} 那一行{why}。临时值不进库：这一句要么写一个库里没有的键，" +
            $"要么静默丢掉外键值，两种都比报错难查。要么先保存 {target.Name} 让库把键给回来，要么把这一行的外键自己填上。");
    }

    /// <summary>新增整批交给 <see cref="InsertEngine{T}"/>：分块、参数预算、<c>AuditValue</c>、§10.6 校验、
    /// <c>OUTPUT</c> 回填键那条链路已经被真库用例逐行钉住了（§13.1 的写侧纪律），在跟踪器里再实现一遍
    /// 只会多出第二份要各自维护的缺陷。<b>回填是这里唯一额外的要求</b>：插完拿不到主键，身份映射就登记不上，
    /// 于是同一会话第二次保存会把那一行再插一遍；插完拿不到并发列，手里那一列就是空的，
    /// 第一次保存的 <c>WHERE</c> 就对不上（后一半随 §8.2 的乐观并发一起生效）。</summary>
    async Task<IReadOnlyList<SaveChangesOp>> WriteInsertsAsync(List<HookTrace> traces, CancellationToken ct)
    {
        ResolveTempKeys(EntityState.Added);

        List<Node>? pending = null;

        foreach (var node in _order)
        {
            if (node.State != EntityState.Added)
                continue;

            (pending ??= []).Add(node);
        }

        if (pending is null)
            return [];

        var engine = new InsertEngine<T>(_orm, [.. pending.Select(node => (T) node.Entity)]);

        // 两档文本要在这里选好：有自增列、或者有库维护的并发列（如 rowversion），就必须走回填那一档，
        // 否则实体手里的键是 0、手里那一列并发列是空 —— 前者让身份映射登记不上（同一会话第二次保存会把那一行再插一遍），
        // 后者让下一次保存必假冲突（§8.2）。应用自维护的并发列（如 int Version）不需要回填 —— 实体已经有初始值。
        // 没有库维护的并发列时 Generated 档与 Keys 档逐字同文，既有样本不动。
        // 先 Explain 再执行不是多余：Explain 里跑校验（§10.6），它必须发生在第一条语句上线之前，
        // 而回填那一档的执行口内部也会走到同一个已缓存的计划，不会把 AuditValue 过第二遍。
        var backfill = _hasIdentity || _storeFilledTokens.Length > 0;
        var plan = backfill ? engine.ExplainGenerated() : engine.Explain();
        var rows = plan.RowCount;

        // 插入档的回放来自引擎那份计划（取值与两条改写钩子都在里面），而它<em>不在</em>这里的 traces 上：
        // 不并进来的话，一次 SaveChanges 的报告会说"没人改写过"，而同一次保存里那句 INSERT 确实是被改过的。
        // 并进的位置就在执行之前 —— Insert 恒为第一档，所以步号与计划里那份逐格相同。
        traces.AddRange(plan.HookReplay);

        if (backfill)
            await engine.ExecuteGeneratedAsync(ct).ConfigureAwait(false);
        else
            await engine.ExecuteAffrowsAsync(ct).ConfigureAwait(false);

        return [Operation(WriteOperation.Insert, plan.Chunks.Count, rows,
            [.. plan.Chunks[0].Ast.Columns.Select(static column => column.ColumnName)], plan.Chunks[0].Sql.Text)];
    }

    /// <summary>改动：先按<em>脏列掩码</em>分桶（§12.5），再逐桶决定发几条语句 —— 同一种改动有两行以上、
    /// 且这一型可合，就合成一条
    /// <c>UPDATE [t0] SET [c] = [t1].[c] FROM [T] AS [t0] INNER JOIN (VALUES …) AS [t1] (键, 改动列) ON 键相等 WHERE 变更守卫</c>；
    /// 否则一个实体一句，文本与 v0.16~v0.18 逐字相同。
    ///
    /// <b>三档不进合并</b>，每一档的理由都不是"麻烦"：
    /// <list type="bullet">
    /// <item><em>带并发列的实体</em>：那一句的 <c>WHERE</c> 里有逐行不同的快照值，而 <c>OUTPUT</c> 要把新值
    /// 按行落回各只实体 —— 一条多行语句的回包与行的对应关系拿不出来（§12.4 第 5 项欠的后一半）。</item>
    /// <item><em>单行桶</em>：合并没有对象，而多出来的 <c>FROM (VALUES)</c> 形状要为一条语句付两倍的文本复杂度。</item>
    /// <item><em>守卫覆盖不全的桶</em>：只要有一列在该库不可比（LOB / <c>xml</c>），整个桶退回逐条 ——
    /// 把那一列从守卫里省掉的后果是「只改了那一列」的行被守卫挡掉，那是丢写，不是少优化。</item>
    /// </list>
    ///
    /// <b>合并那一档的行数口径与逐条不同</b>：守卫把"库里已经是这个值"的行挡在 <c>UPDATE</c> 之外，所以
    /// <see cref="SaveChangesOp.Rows"/> 在这一档是<em>真变化的行数</em>，而逐条那一档一直是命中数
    /// （§12.4 第 2 项的账，两档各有一条真库用例钉着）。附带一句不好听但必须说的：合并之后
    /// <c>Rows &lt; 行数</c> 不再能区分「哪几行没变」与「哪几行已经不在了」——逐条那一句答得了后者
    /// （<c>WHERE</c> 只有主键），合并这一句答不了。要逐行归因就开单行桶（或者像带并发列那样整型退回逐条）。</summary>
    async Task<IReadOnlyList<SaveChangesOp>> WriteUpdatesAsync(List<HookTrace> traces, CancellationToken ct)
    {
        // 先换临时键再分桶：桶里的 <c>Values</c> 与合并语句的那一份字面行集都是从实体读出来的现值，
        // 换晚了就是先把 −1 抄进计划里（§8.2 的 Tp 回填，理由与插入档同一条）。
        ResolveTempKeys(EntityState.Modified);

        List<Bucket>? buckets = null;
        Dictionary<string, int>? index = null;
        var tag = new ExecutionTag("update", [_contract.Table.Name]);

        // 第一遍只判脏与取值，一条语句都不发：合并的前提是"先看清同一种改动在这一批里有几行"，
        // 而逐行发射看不到后面还有没有同伴 —— v0.16 那份"只分桶、不合并"的形状正是这么来的。
        foreach (var node in _order)
        {
            if (node.State != EntityState.Modified)
                continue;

            var entity = (T) node.Entity;
            List<int>? dirty = null;

            for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
            {
                if (!node.Dirty[ordinal])
                    continue;

                if (!_writable[ordinal])
                    throw Unwritable(ordinal, node);

                (dirty ??= []).Add(ordinal);
            }

            // 显式把每一列都标成不脏 = 这一行放弃写入。它照样会在 PostFlush 里结算掉，而不是下一次再试：
            // 留着一个「永远脏、永远不发语句」的状态，HasChanges 就再也不能当「有没有待写的东西」用。
            if (dirty is null)
                continue;

            var key = RequireDefinite(entity, "写回库里");
            var values = new object?[dirty.Count];

            for (var i = 0; i < values.Length; i++)
                values[i] = _schema.GetValue(entity, dirty[i]);

            int[] ordinals = [.. dirty];
            var names = ColumnNames(ordinals);
            var mask = string.Join(',', names);

            index ??= new(StringComparer.Ordinal);
            buckets ??= [];

            if (index.TryGetValue(mask, out var at))
                buckets[at].Rows.Add(new PendingUpdate(node, entity, ordinals, key, values));
            else
            {
                index[mask] = buckets.Count;
                buckets.Add(new Bucket(ordinals, names, [new PendingUpdate(node, entity, ordinals, key, values)]));
            }
        }

        if (buckets is null)
            return [];

        var operations = new List<SaveChangesOp>(buckets.Count);

        foreach (var bucket in buckets)
            operations.Add(bucket.Rows.Count > 1 && Mergeable(bucket)
                ? await WriteMergedAsync(bucket, traces, tag, ct).ConfigureAwait(false)
                : await WriteSingularAsync(bucket, traces, tag, ct).ConfigureAwait(false));

        return operations;
    }

    /// <summary>这一桶能不能合成一条。可不可比是逐列问 provider 的（哪一型在哪家库连等号都不接受，
    /// 只有库自己知道），带不带并发列是逐类型的结构性事实 —— 后者在这里判而不是在 <see cref="WriteAsync"/>
    /// 那一格判，因为合并语句压根不回填东西。
    ///
    /// <para>最前面那一道是第三种问法：<b>这一家有没有"把另一张表带进写语句"的这种语法</b>。
    /// 它不在这里问就只能到发射层问，而发射层抛出来的是一条构建期异常 —— 对一个没有 <c>UPDATE … FROM</c>
    /// 的库（Oracle 血统）来说，那等于 <c>SaveChanges</c> 在"两行同掩码"这一种<em>最常见</em>的形状上红，
    /// 而不是退化成一句一实体。判据写在 <see cref="ISqlDialect.SupportsJoinedWrites"/> 那一格：
    /// 它跟着方言走，所以永远不需要有人 catch 一个异常来"试探语法"（§4 明令禁止的那个形状）。</para>
    ///
    /// <para>顺序也是断言的一部分：这一道在<em>逐列问守卫之前</em>，所以一个盖不住的列都不会被问到。</para></summary>
    bool Mergeable(Bucket bucket)
    {
        if (!_orm.Provider.Dialect.SupportsJoinedWrites)
            return false;

        if (_tokens.Length > 0)
            return false;

        var guard = _orm.Provider.ChangeGuard;

        foreach (var ordinal in bucket.Ordinals)
            if (!guard.Compares(_columns[ordinal]))
                return false;

        return true;
    }

    /// <summary>一个实体一句。合并没接管的那些桶（单行、带并发列、守卫盖不全）都走这里，
    /// 所以这一档的文本必须与 v0.18 逐字相同 —— 它是"合并是加法而不是改写"的那条验收线。</summary>
    async Task<SaveChangesOp> WriteSingularAsync(Bucket bucket, List<HookTrace> traces, ExecutionTag tag, CancellationToken ct)
    {
        // 步号 = 追加时刻的 traces 长度：两个档各自从当前长度起，于是跨桶连续，而不必把 ref 带进异步口
        // （C# 不允许异步方法有 ref 参数）。
        var step = traces.Count;
        var affected = 0;
        string? first = null;

        foreach (var row in bucket.Rows)
        {
            var sets = Sets(row);

            // 应用自维护的并发列（如 int Version）：在 SET 里加 Version = Version + 1。
            // 库维护的（如 rowversion）不加 —— 它们的值由库生成，靠 OUTPUT/RETURNING 回读。
            foreach (var ordinal in _appMaintainedTokens)
                sets.Add(new SetClause(_columns[ordinal], new SqlBinaryExpression(BinaryOperator.Add,
                    new ColumnExpression(_table, _columns[ordinal]), new ParameterValue(1, _columns[ordinal]))));

            // OUTPUT/RETURNING 只回读库维护的并发列；应用自维护的在写完后直接 +1，不需要回读。
            var sql = Emit(new UpdateStatement
            {
                Table = _table,
                Sets = sets,
                Where = WhereWithTokens(row.Key, row.Node.Snapshot),
                Returning = _storeFilledTokenColumns,
            }, sets.Count + row.Key.Length + _storeFilledTokens.Length, traces, ref step);

            var rows = await WriteAsync(sql, row.Entity, tag, ct).ConfigureAwait(false);

            // 0 行 + 没有并发列 = 今天的老行为：那一行不在了，行数进报告，不抛（§12.4 第 2 项的账）。
            // 0 行 + 有并发列 = 说不清是"别人改过"还是"行没了"，所以必须问一次，不能猜（§8.2）。
            if (rows == 0 && _tokens.Length > 0)
                throw await ConflictAsync(row.Entity, row.Node.Snapshot, row.Key, "update", sql, ct).ConfigureAwait(false);

            // 应用自维护的并发列：写成功后直接在实体上 +1。库维护的已经由 WriteAsync 回读了。
            foreach (var ordinal in _appMaintainedTokens)
                IncrementTokenValue(row.Entity, ordinal);

            affected += rows;
            first ??= sql.Text;
        }

        // 语句数就是行数：这一档没有"两行合一条"，所以报告里那两个数相等，这一点与合并档正好相对。
        return Operation(WriteOperation.Update, bucket.Rows.Count, affected, bucket.ColumnNames, first!);
    }

    /// <summary>合并档：一批"键 + 要写的值"作为一个字面行集参与连接（<see cref="SourceRows"/>），
    /// 于是 N 只实体一条语句。块大小按 §12.3 那一式算，分母是<em>每行参数数</em> = 键列 + 改动列
    /// （守卫与 <c>ON</c> 引的都是行集那一侧的列，不再多要参数）。</summary>
    async Task<SaveChangesOp> WriteMergedAsync(Bucket bucket, List<HookTrace> traces, ExecutionTag tag, CancellationToken ct)
    {
        var step = traces.Count;
        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var columns = SideColumns(bucket);
        var rowsPerStatement = StatementEmitter.RowsPerStatement(budget, columns.Length);
        var sets = SetsOf(bucket);
        var where = ChangeGuard(bucket);
        var on = KeyEquality();
        var rows = bucket.Rows;
        var statements = 0;
        var affected = 0;
        string? first = null;

        for (var at = 0; at < rows.Count; at += rowsPerStatement)
        {
            var count = Math.Min(rowsPerStatement, rows.Count - at);
            var values = new IReadOnlyList<SqlExpression>[count];

            for (var row = 0; row < count; row++)
                values[row] = SideRow(columns, rows[at + row]);

            var sql = Emit(new UpdateStatement
            {
                Table = _table,
                Sets = sets,
                Where = where,
                Joins = [new JoinClause(JoinKind.Inner, new SourceRows(_rowSet, columns, values), on)],
            }, columns.Length * count, traces, ref step);

            statements++;
            affected += await _orm.Ado.NonQueryAsync(sql, tag, ct).ConfigureAwait(false);
            first ??= sql.Text;
        }

        return Operation(WriteOperation.Update, statements, affected, bucket.ColumnNames, first!);
    }

    /// <summary>行集那一侧的列清单，<b>顺序即每行参数的顺序</b>：键在前、改动列在后。
    /// <c>AS [t1] ([Id], [Name])</c> 那一份清单与 <see cref="SideRow"/> 都从这里出，所以两处不可能错开；
    /// 而它们一旦错开，值就整体串列，那种 UPDATE 每家库都执行得动。</summary>
    ColumnContract[] SideColumns(Bucket bucket)
    {
        var columns = new ColumnContract[_keyOrdinals.Length + bucket.Ordinals.Length];
        var at = 0;

        foreach (var ordinal in _keyOrdinals)
            columns[at++] = _columns[ordinal];

        foreach (var ordinal in bucket.Ordinals)
            columns[at++] = _columns[ordinal];

        return columns;
    }

    static IReadOnlyList<SqlExpression> SideRow(ColumnContract[] columns, PendingUpdate row)
    {
        var cells = new SqlExpression[columns.Length];

        for (var i = 0; i < row.Key.Length; i++)
            cells[i] = new ParameterValue(row.Key[i], columns[i]);

        for (var i = 0; i < row.Values.Length; i++)
            cells[row.Key.Length + i] = new ParameterValue(row.Values[i], columns[row.Key.Length + i]);

        return cells;
    }

    /// <summary>逐行那句的 <c>SET</c>：值来自实体当前值，一颗参数一格。</summary>
    List<SetClause> Sets(PendingUpdate row)
    {
        var sets = new List<SetClause>(row.Values.Length);

        for (var i = 0; i < row.Values.Length; i++)
            sets.Add(new SetClause(_columns[row.Ordinals[i]], new ParameterValue(row.Values[i], _columns[row.Ordinals[i]])));

        return sets;
    }

    /// <summary>合并那句的 <c>SET</c>：右侧是行集那一侧的同一列，所以一整批只有一份 <c>SET</c>，
    /// 逐行的差别全在 <c>VALUES</c> 里。这是合并省下 SQL 文本编译的那一半（另一半是往返次数）。</summary>
    List<SetClause> SetsOf(Bucket bucket)
    {
        var sets = new List<SetClause>(bucket.Ordinals.Length);

        foreach (var ordinal in bucket.Ordinals)
        {
            var column = _columns[ordinal];

            sets.Add(new SetClause(column, new ColumnExpression(_rowSet, column)));
        }

        return sets;
    }

    /// <summary>变更守卫：改动列逐列问 provider「库里这一格与行集那一格是不是不同」，再 <c>OR</c> 起来。
    /// <b>只有改动列进守卫，键列不进</b>：跟踪器不改主键（那是 <see cref="Unwritable"/> 那一档），
    /// 而把键列也比一遍只会让谓词更长。右倾折是因为 <c>DialectBase</c> 对同种连接词不加括号，
    /// 于是这一串出来的是一条平铺的 <c>OR</c>。</summary>
    SqlExpression ChangeGuard(Bucket bucket)
    {
        var guard = _orm.Provider.ChangeGuard;
        var differences = new SqlExpression[bucket.Ordinals.Length];

        for (var i = 0; i < differences.Length; i++)
        {
            var column = _columns[bucket.Ordinals[i]];

            differences[i] = guard.BuildDifference(column, new ColumnExpression(_table, column), new ColumnExpression(_rowSet, column));
        }

        var where = differences[^1];

        for (var i = differences.Length - 2; i >= 0; i--)
            where = new SqlBinaryExpression(BinaryOperator.Or, differences[i], where);

        return where;
    }

    /// <summary>连接条件：主键逐列相等。与 <see cref="KeyPredicate"/> 的区别只在右侧 —— 那里是一颗参数，
    /// 这里是行集的那一列，而两边的类型由参数绑定那一侧给（所以 <c>varchar</c> 键与 <c>nvarchar</c> 行集
    /// 的比较仍要看排序规则，§10.4 那笔账在这一句上同样成立）。</summary>
    SqlExpression KeyEquality()
    {
        SqlExpression? on = null;

        foreach (var ordinal in _keyOrdinals)
        {
            var column = _columns[ordinal];
            var equal = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(_table, column), new ColumnExpression(_rowSet, column));

            on = on is null ? equal : new SqlBinaryExpression(BinaryOperator.And, on, equal);
        }

        return on!;
    }

    IReadOnlyList<string> ColumnNames(int[] ordinals)
    {
        var names = new string[ordinals.Length];

        for (var i = 0; i < names.Length; i++)
            names[i] = _columns[ordinals[i]].ColumnName;

        return names;
    }

    /// <summary>删除：一句实体一句按主键的 <c>DELETE</c>。<see cref="SaveChangesOp.Columns"/> 留空 ——
    /// 那条语句里没有 <c>SET</c>，把主键列填进去会让人以为它能像 UPDATE 那样挑列。</summary>
    async Task<IReadOnlyList<SaveChangesOp>> WriteDeletesAsync(List<HookTrace> traces, CancellationToken ct)
    {
        var statements = 0;
        var rows = 0;
        string? first = null;
        var step = traces.Count;

        foreach (var node in _order)
        {
            if (node.State != EntityState.Deleted)
                continue;

            // 删哪一行以快照为准：那是当初把这一行认成「库里那一条」的依据。
            // 主键被改过就不能按快照删（会删错一行），也不能按当前值删（那一行未必存在）—— 只能拒。
            foreach (var ordinal in _keyOrdinals)
                if (!Equals(_schema.GetValue((T) node.Entity, ordinal), node.Snapshot[ordinal]))
                    throw Unwritable(ordinal, node);

            object?[] key = [.. _keyOrdinals.Select(ordinal => node.Snapshot[ordinal])];
            var sql = Emit(new DeleteStatement { Table = _table, Where = WhereWithTokens(key, node.Snapshot) }, key.Length + _tokens.Length, traces, ref step);

            var affected = await _orm.Ado.NonQueryAsync(sql, new ExecutionTag("delete", [_contract.Table.Name]), ct).ConfigureAwait(false);

            // 删除的并发谓词同样取快照：意图是「删掉我读到的那一版」，那一版被人改过就不是这一行了。
            // 这一档不回填任何东西 —— 语句成功就意味着行不在了，<c>deleted</c> 侧没有要留下的值。
            if (affected == 0 && _tokens.Length > 0)
                throw await ConflictAsync((T) node.Entity, node.Snapshot, key, "delete", sql, ct).ConfigureAwait(false);

            rows += affected;
            statements++;
            first ??= sql.Text;
        }

        return statements == 0
            ? []
            : [Operation(WriteOperation.Delete, statements, rows, [], first!)];
    }

    /// <summary>整批写成功之后的结算（三档共用一次，所以它拿到的是「全部成功」这个前提）。
    /// 删除的摘出会话；新增的把库刚给的主键登记进身份映射；其余全部重拍快照并转
    /// <see cref="EntityState.Unmodified"/>。</summary>
    public void PostFlush()
    {
        foreach (var node in _order.ToArray())
        {
            var entity = (T) node.Entity;

            // 没欠语句的实例不重拍快照：结算的代价不该随会话里的实例总数涨（一次写两行不该读两万遍字段），
            // 而未改动的 Unmodified 节点的快照本来就是当前值。
            if (node.State == EntityState.Unmodified)
                continue;

            switch (node.State)
            {
                case EntityState.Deleted:
                    Detach(node);

                    continue;

                case EntityState.Added:
                    // 插完才可能有键：自增列此刻才被 InsertEngine 回填进实体，所以登记身份的时刻在这里。
                    if (node.Registered is null)
                    {
                        var values = KeyValues(entity);

                        if (IsDefinite(values))
                        {
                            var key = new IdentityKey(values);

                            // 撞上别人只有一种来路：库里那一行的键原本被当成「没填」（所以没登记），
                            // 而会话里另一只先一步拿到了它。静默覆盖就是两只实例共用一个身份，
                            // 之后改哪一只都写同一行。
                            if (_identity.TryGetValue(key, out var clash))
                                throw new FastSqlException(Collision(clash, values) + " 而这一行已经写进库了，所以退不回「没插」：要清干净请另开一个会话。");

                            _identity[key] = node;
                            node.Registered = key;
                        }
                    }

                    break;
            }

            node.State = EntityState.Unmodified;
            Reset(node, entity);
        }

        // 临时键的账到这一句就清完：三档全部发完，依赖方手里那些颗都已经被换掉
        // （整批抛在半路时这里根本走不到，那是 §14 里"回滚后同一会话继续写"那一格的题）。
        // 号本身不回退（<c>_tempCursor</c> 单调往负走），所以下一轮不会重放一个旧临时值。
        _temps?.Clear();

        _mayHaveWrites = false;
    }

    /// <summary>一个报告桶，也就是 §12.5 的合并单位：<em>列掩码相同</em>的那几只实体。
    /// 之所以拿得出掩码当键而不出错：同一个桶里每只实体的改动列集合逐字相同，只是行的取值不同 ——
    /// 合并语句的那一份 <c>SET</c> 与那一份列清单正是靠这一条才只有一份。
    /// <see cref="Rows"/> 是<em>待写</em>的行，不是回来的行数（那个数在报告里，见 <see cref="SaveChangesOp.Rows"/>）。</summary>
    sealed record Bucket(int[] Ordinals, IReadOnlyList<string> ColumnNames, List<PendingUpdate> Rows);

    /// <summary>一只待写的行。<see cref="Values"/> 与 <see cref="PendingUpdate.Ordinals"/> 同序，
    /// 所以两份都在合并档共用、逐条档各取所需，而<em>不需要</em>在合并之前把值包成参数再拆回来。</summary>
    sealed record PendingUpdate(Node Node, T Entity, int[] Ordinals, object?[] Key, object?[] Values);

    SaveChangesOp Operation(WriteOperation operation, int statements, int rows, IReadOnlyList<string> columns, string sql)
        => new(operation, _contract.Table.Name, rows, statements, columns, sql);

    DialectSql Emit(UpdateStatement ast, int parameters, List<HookTrace> traces, ref int step)
    {
        var dialect = _orm.Provider.Dialect;
        var writer = StatementEmitter.Writer(dialect, StatementEmitter.EstimateLength(parameters, _identifierChars));

        dialect.Emit(in ast, writer);

        return Bind(new DialectSql(writer.ToSqlString(), writer.Parameters), "update", traces, ref step);
    }

    DialectSql Emit(DeleteStatement ast, int parameters, List<HookTrace> traces, ref int step)
    {
        var dialect = _orm.Provider.Dialect;
        var writer = StatementEmitter.Writer(dialect, StatementEmitter.EstimateLength(parameters, _identifierChars));

        dialect.Emit(in ast, writer);

        return Bind(new DialectSql(writer.ToSqlString(), writer.Parameters), "delete", traces, ref step);
    }

    /// <summary>两个钩子的顺序即语义：<c>CudBuilding</c> 改整句文本（所以它必须在参数级改写之前，
    /// 否则改写者新加的参数没人过钩子），<c>ParameterBinding</c> 改值。跟踪器这一份与三条链同一份实现。</summary>
    DialectSql Bind(DialectSql sql, string operationKind, List<HookTrace> traces, ref int step)
        => StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, sql, operationKind, traces, ref step), traces, ref step);

    // ── 内部 ──────────────────────────────────────────────────────────

    /// <summary>登记。<paramref name="key"/> 为 null 表示「不进身份映射」，而它仍然进引用表与跟踪序 ——
    /// 一个键还没填的实例依旧是会话里的一个实例，只是查不到它。</summary>
    Node Register(object entity, EntityState state, IdentityKey? key, T typed)
    {
        var node = new Node(entity, _columns.Length) { State = state, Registered = key };

        Snapshot(node, typed);

        _byRef[entity] = node;
        _order.Add(node);

        if (key is not null)
            _identity[key.Value] = node;

        if (state is not EntityState.Unmodified)
            _mayHaveWrites = true;

        return node;
    }

    void Detach(Node node)
    {
        // 按节点存的那个键摘，而不是按当前值重算：改过主键的实例若按新值去摘，摘掉的是别人的那一条。
        if (node.Registered is { } key && _identity.TryGetValue(key, out var held) && ReferenceEquals(held, node))
            _identity.Remove(key);

        _byRef.Remove(node.Entity);
        _order.Remove(node);

        // 状态归到 Detached 而不是留在摘掉前那一格：调用方手里可能还有一只 <see cref="EntityEntry"/>
        // （它只是视图，不持状态），留着 Modified 就成了「一个已经不在会话里的实例还欠着一条 UPDATE」。
        node.State = EntityState.Detached;
    }

    void Snapshot(Node node, T entity)
    {
        for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
            node.Snapshot[ordinal] = _schema.GetValue(entity, ordinal);
    }

    void Reset(Node node, T entity)
    {
        Snapshot(node, entity);

        Array.Fill(node.Overrides, (bool?) null);
        Array.Clear(node.Dirty);
    }

    /// <summary>整行强制脏。<b>只覆盖可写列</b>：主键与自增列一旦进 <see cref="Node.Overrides"/>，
    /// 发射前就会撞 <see cref="Unwritable"/>，于是「把 State 设成 Modified」这一句最常见的写法变成必抛 ——
    /// 那不是「全写」，那是没法写。</summary>
    void MarkAllWritable(Node node)
    {
        for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
        {
            if (!_writable[ordinal])
                continue;

            node.Overrides[ordinal] = true;
            node.Dirty[ordinal] = true;
        }
    }

    object?[] KeyValues(T entity)
    {
        var values = new object?[_keyOrdinals.Length];

        for (var i = 0; i < values.Length; i++)
            values[i] = _schema.GetValue(entity, _keyOrdinals[i]);

        return values;
    }

    object?[] RequireDefinite(T entity, string what)
    {
        var values = KeyValues(entity);

        if (IsDefinite(values))
        {
            // 临时主键是「有值，但那不是库里的键」，所以它躲得过上面那一判而躲不过这一句：
            // 按 -1 去定位一句 UPDATE/DELETE 命中 0 行，而报告只会说"0 行" —— 那是静默丢写（§12.2）。
            // 判据用 ProbeTemp 而不是"看着像负数"：只有本档真发出去过的那一颗才算临时键，
            // 调用方自己填的负数键（库里真有这么一行）不该被这一句挡下来。
            if (values.Length == 1 && ProbeTemp(values[0], out _) != TempProbe.NotTemp)
                throw new FastSqlException($"{_contract.Table.Name} 的这一行手里还是临时主键 {Text(values)}，而 {what} 要按主键定位库里那一行：" +
                    "那个负数不是库里的键，一句按它写的 WHERE 只会命中 0 行。先 SaveChanges 让库把真实的键给回来（§8.2 的 Tp）。");

            return values;
        }

        throw new FastSqlException($"{_contract.Table.Name} 的主键现在没有值（{Text(values)}），而 {what} 要按主键定位行：" +
            "一句 WHERE [主键] = NULL 什么都匹配不到，却会安静地报告「0 行」。自增列请把它标为新增、让库先把键给回来；键由客户端生成的话先把值填上。");
    }

    void RequireKey()
    {
        if (_keyOrdinals.Length > 0)
            return;

        throw new FastSqlException($"{_contract.Table.Name} 没有主键：跟踪器认不出「这一行」，而身份映射与 UPDATE/DELETE 的 WHERE 都建立在主键上（§8.2）。" +
            $"只读请走 orm.Select<{typeof(T).Name}>()。这就是 §8.2 那句「无 PK 自动不跟踪」今天的形态 —— 它是<em>拒绝跟踪并指路</em>，不是静默降级（§12.2）。");
    }

    static bool IsDefinite(object?[] values)
    {
        foreach (var value in values)
        {
            if (value is null || Unset(value))
                return false;
        }

        return true;
    }

    /// <summary>「这个值其实是<em>没填</em>」：整数 0 与 <see cref="Guid.Empty"/>。
    /// 判得出来的依据是自增列的种子不是 0（<c>IDENTITY(1,1)</c> 与 <c>SEQUENCE START WITH</c> 的默认都不是），
    /// 而 <c>Guid.Empty</c> 不会由 <c>NEWID()</c> 生成。空串<em>不</em>算未填：字符串列没有「零值」这一说，
    /// <c>""</c> 只能是有人写的。<b>枚举主键会被判成已填</b>（装箱后匹配不到这些 case），
    /// 而 0 号枚举值确实是合法键 —— 这一侧偏保守是对的方向。</summary>
    static bool Unset(object value) => value switch
    {
        int i => i == 0,
        long l => l == 0,
        short s => s == 0,
        byte b => b == 0,
        uint u => u == 0,
        ulong ul => ul == 0,
        Guid g => g == Guid.Empty,
        _ => false,
    };

    /// <summary>CLR 型是不是有符号/无符号整型之一（含 nullable 包装）。应用自维护的并发列只认这几型，
    /// 因为只有它们支持 <c>+1</c> 语义。<c>datetime2</c>、<c>byte[]</c>（rowversion）等并发列不属于这一档。</summary>
    static bool IsIntegerClrType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying == typeof(int) || underlying == typeof(long)
            || underlying == typeof(short) || underlying == typeof(byte)
            || underlying == typeof(uint) || underlying == typeof(ulong);
    }

    string Collision(Node clash, object?[] values)
        => $"{_contract.Table.Name} 的主键 {Text(values)} 已经有另一个实例在被跟踪（{clash.State}）。同键两只实例在会话里没有定义：写谁？";

    FastSqlException Unwritable(int ordinal, Node node)
    {
        var column = _columns[ordinal];

        if (column.IsConcurrencyToken)
            return new($"{column.MemberName}（列 {column.ColumnName}）被判定为已改动，而它是并发列：那一列只比较、不书写（§8.2），进不了 SET。" +
                $"实体里现在是 {StatementEmitter.Text(_schema.GetValue((T) node.Entity, ordinal))}，会话认库里那一版是 {StatementEmitter.Text(node.Snapshot[ordinal])}。" +
                "两种来路：要么有人往这一列里写了值（库维护的并发列由库赋值，应用自维护的由 SET Version = Version + 1 自动递增，都不接受手动赋值），" +
                "要么上一批保存被回滚过（回滚不会把已经读进实体的值撤回，会话因此停在「以为写过了」的那一版）。" +
                "要么把这一列改回原值，要么显式 Property(...).IsModified = false，要么丢弃这一只实例。");

        return new($"{column.MemberName}（列 {column.ColumnName}）被判定为已改动，而它是主键或自增列，进不了 SET。" +
            "改「已经有值的主键」是换键外加把所有依赖方的外键那一格一起改过去，比 Tp 那一档远（临时主键只管「新行的键还没值」，§8.2），今天没有；" +
            "自增列不接受写，那是 IDENTITY_INSERT 那条路。要么把这一列改回原值，要么显式 Property(...).IsModified = false。");
    }

    static string Text(object?[] values)
        => string.Join("、", values.Select(static value => value?.ToString() ?? "NULL"));

    /// <summary>主键合取式。<b>逐列 <c>=</c> 再 <c>AND</c>，不用 <c>(a, b) IN ((…))</c></b>：
    /// 行值构造器的支持面在国产库里参差（PG 血统可用而达梦不稳），而一句单行的
    /// <c>WHERE a = @p AND b = @q</c> 没有任何一家需要特殊照顾。跟踪器发的本来就是逐行语句，
    /// 这里的形状只服务「哪一家都执行得动」这一条。</summary>
    SqlExpression KeyPredicate(object?[] values)
    {
        SqlExpression? where = null;

        for (var i = 0; i < values.Length; i++)
        {
            var column = _columns[_keyOrdinals[i]];
            var equal = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(_table, column), new ParameterValue(values[i], column));

            where = where is null ? equal : new SqlBinaryExpression(BinaryOperator.And, where, equal);
        }

        return where!;
    }

    /// <summary>标识部分 ∧ 并发列。<b>并发谓词是并进 AST 的 <c>WHERE</c>、在发射之前</b>，不是往文本尾巴上接一句
    /// <c>AND</c>：<c>UPDATE ... OUTPUT ... FROM ... WHERE</c> 与 <c>UPDATE ... SET ... WHERE ... RETURNING</c>
    /// 两家的收尾位置不一样（T-SQL 的 <c>OUTPUT</c> 在 <c>FROM</c> 之前，PG 系的 <c>RETURNING</c> 在整句之后），
    /// 文本级追加必在其中一家写出语法错，而且参数编号会跟着乱。旧注释里那句「落地位置在 CudBuilding 之后」
    /// 说的就是这个尾巴，它不对 —— 顺序反过来：<c>AST → 文本 → CudBuilding → ParameterBinding</c>（§10.1）。</summary>
    SqlExpression WhereWithTokens(object?[] key, object?[] snapshot)
    {
        var tokens = ConcurrencyPredicate(snapshot);

        return tokens is null ? KeyPredicate(key) : new SqlBinaryExpression(BinaryOperator.And, KeyPredicate(key), tokens);
    }

    /// <summary>并发列的合取式，值取<em>快照</em>而不是实体当前值：这一句回答的是「我读到的那一版还在不在」，
    /// 拿当前值去比就是拿「我想写成的样子」当「我以为库里的样子」，那样的 <c>WHERE</c> 永远命中 0 行。</summary>
    SqlExpression? ConcurrencyPredicate(object?[] snapshot)
    {
        SqlExpression? and = null;

        foreach (var ordinal in _tokens)
        {
            var column = _columns[ordinal];
            var value = snapshot[ordinal];
            var target = new ColumnExpression(_table, column);

            // 可空的并发列：库里那一格当时是 NULL 就只能写 IS NULL —— [列] = @p 在哪家都是 UNKNOWN，
            // 于是每一次保存都成假冲突，而异常会说"别人改过"。值的比较形状归 provider（§4），
            // 因为只有它知道 rowversion 该按什么比。
            var match = value is null
                ? new SqlUnaryExpression(UnaryOperator.IsNull, target)
                : _orm.Provider.Concurrency.BuildPredicate(target, column, value);

            and = and is null ? match : new SqlBinaryExpression(BinaryOperator.And, and, match);
        }

        return and;
    }

    /// <summary>一句 UPDATE 的执行出口。<b>有库维护并发列时它同时是回填口</b>：同一条语句既写出去又把
    /// <c>OUTPUT</c>/<c>RETURNING</c> 回读的新值落进实体。应用自维护的并发列不走这条路 —— 它们在 SET 里
    /// <c>Version = Version + 1</c>，写完后直接在实体上 +1（<see cref="IncrementTokenValue"/>），不需要回读。
    /// 返回值按<em>回包行数</em>算（<see cref="AdoExecutor.WriteWithResultAsync"/> 的口径），对一句
    /// <c>WHERE 主键 ∧ 并发列</c> 的单行 UPDATE 它就是受影响行数，0 就是 <see cref="ConflictAsync"/>。</summary>
    async Task<int> WriteAsync(DialectSql sql, T entity, ExecutionTag tag, CancellationToken ct)
    {
        if (_storeFilledTokens.Length == 0)
            return await _orm.Ado.NonQueryAsync(sql, tag, ct).ConfigureAwait(false);

        return await _orm.Ado.WriteWithResultAsync(sql, tag, (reader, index) =>
        {
            for (var column = 0; column < _storeFilledTokens.Length; column++)
                _schema.SetValue(entity, _storeFilledTokens[column], Value(reader, column));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>应用自维护的并发列在写成功后直接 +1：库已经把 <c>Version = Version + 1</c> 写进去了，
    /// 实体手里那一格还停在旧值上，下一次保存的 <c>WHERE</c> 就会拿旧值去比 —— 假冲突。
    /// 这里读当前值、加一、写回去，与库那一侧的改写保持同步。</summary>
    void IncrementTokenValue(T entity, int ordinal)
    {
        var current = _schema.GetValue(entity, ordinal);

        object? next = current switch
        {
            int i => i + 1,
            long l => l + 1,
            short s => (short) (s + 1),
            byte b => (byte) (b + 1),
            _ => throw new FastSqlException($"{_columns[ordinal].MemberName}（列 {_columns[ordinal].ColumnName}）是应用自维护的并发列，" +
                $"但它的 CLR 型是 {current?.GetType().Name ?? "null"}，不支持 +1 自增。应用自维护的并发列只支持 int / long / short / byte。"),
        };

        _schema.SetValue(entity, ordinal, next);
    }

    /// <summary>那一格的值。<c>GetValue</c> 对 NULL 给的是 <see cref="DBNull"/>，直接塞进实体属性就是一次
    /// 类型不匹配，所以这里折成 <c>null</c> —— 可空的并发列（<c>DateTimeOffset?</c> 那一派）读回空值是常态。</summary>
    static object? Value(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);

    /// <summary>0 行那一条的成因分查：<b>"别人改过"与"这一行没了"在行数上一模一样</b>，而处置相反
    /// （前者要合并，后者通常丢弃意图），所以抛出前按主键补读一次。代价只付在冲突那一条上 ——
    /// 判据写在 <c>affected == 0</c> 后面就是为了这件事不影响正常保存。</summary>
    async Task<DbUpdateConcurrencyException> ConflictAsync(T entity, object?[] snapshot, object?[] key, string operation, DialectSql sql, CancellationToken ct)
    {
        var current = new Dictionary<string, object?>(_tokens.Length);
        var database = await ReadTokensAsync(key, ct).ConfigureAwait(false);
        var lines = new string[_tokens.Length];

        for (var i = 0; i < _tokens.Length; i++)
        {
            var column = _columns[_tokens[i]];
            var expected = snapshot[_tokens[i]];
            var actual = database is null ? null : database[i];

            current[column.MemberName] = _schema.GetValue(entity, _tokens[i]);

            lines[i] = $"{column.MemberName}: 会话以为是 {StatementEmitter.Text(expected)}，库里" +
                (database is null ? "已经没有这一行" : $"是 {StatementEmitter.Text(actual)}");
        }

        Dictionary<string, object?>? databaseValues = database is null ? null : ToTokenDictionary(database);

        return new DbUpdateConcurrencyException(
            $"{_contract.Table.Name} 的一句 {operation} 命中 0 行，而它带着并发列的谓词。" +
            (database is null ? "按主键补查也没有这一行：它被删掉了。" : "并发列在库里的现值与快照不同：它被别人改过。") +
            $"逐列：{string.Join("；", lines)}。" +
            $"最终执行的语句：{sql.Text}",
            entity, operation, sql.Text, current, databaseValues);
    }

    Dictionary<string, object?> ToTokenDictionary(object?[] values)
    {
        var dictionary = new Dictionary<string, object?>(values.Length);

        for (var i = 0; i < values.Length; i++)
            dictionary[_columns[_tokens[i]].MemberName] = values[i];

        return dictionary;
    }

    /// <summary>按主键补读那几列。<b>只读并发列，不整行物化</b>：整行刷新是 <c>Refresh</c> 那一格的活
    /// （§14 的 P1 余项，没接线），这里只要回答"这一行还在不在、那几列现在是什么"。
    /// 这一条语句<em>不过</em> <c>CudBuilding</c> 也不过多出来的参数钩子：它的 <c>WHERE</c> 必须与刚才那句的
    /// 标识部分逐字同一把尺子，被改写钩子换成别的东西就答错了题，而答错的题比不答更糟。</summary>
    async Task<object?[]?> ReadTokensAsync(object?[] key, CancellationToken ct)
    {
        var projections = new Projection[_tokens.Length];

        for (var i = 0; i < _tokens.Length; i++)
            projections[i] = new Projection(new ColumnExpression(_table, _columns[_tokens[i]]), null);

        var select = new SelectStatement { Columns = projections, From = new SourceTable(_table), Where = KeyPredicate(key) };

        var dialect = _orm.Provider.Dialect;
        var writer = StatementEmitter.Writer(dialect, StatementEmitter.EstimateLength(key.Length, _identifierChars));

        dialect.Emit(in select, writer);

        var sql = new DialectSql(writer.ToSqlString(), writer.Parameters);

        object?[] Map(DbDataReader reader)
        {
            var values = new object?[_tokens.Length];

            for (var i = 0; i < values.Length; i++)
                values[i] = Value(reader, i);

            return values;
        }

        return await _orm.Ado.FirstAsync(sql, Map, new ExecutionTag("concurrency-read", [_contract.Table.Name]), ct).ConfigureAwait(false);
    }
}
