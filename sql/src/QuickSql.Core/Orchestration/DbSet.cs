using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text;
using QuickSql.Execution;
using QuickSql.Materialization;
using QuickSql.Metadata;
using QuickSql.Query;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.Tracking;

namespace QuickSql;

/// <summary>某一类实体在<em>一个会话里</em>的入口（§8.1：<c>DbSet&lt;T&gt; : ISet&lt;T&gt;</c>（本仓库里那个契约叫
/// <c>ITrackedSet&lt;T&gt;</c>，改名的理由写在它自己的注释上），而 <c>ITrackedSet&lt;T&gt; : IQuery&lt;T&gt;</c>）。
///
/// <b>链式全部可用，<c>from … in</c> 不可用</b>（§16.16：跟踪集合也不是 <c>IQueryable</c>）—— 本类不新增任何
/// 查询算子，它只是把 <see cref="QuickSqlQuery{T}"/> 那一条链和会话的跟踪器接在一起。
/// 于是 §8.1 那三行示例的形状是直接的推论而不是巧合：<c>db.Orders.Where(...)</c> 得到 <c>IQuery&lt;Order&gt;</c>
/// （<b>仍然跟踪</b>，因为派生出来的那一份引擎还带着会话），而 <c>.AsNoTracking()</c> 之后得到的是同一个模型
/// 的无跟踪视图 —— §8.3 那句「只差一个标记位」从 v0.16 起是真的：那个标记位在引擎上，不在模型里。
///
/// <b>它不是缓存</b>：每次 <c>Set&lt;T&gt;()</c> 得到同一个对象（同一份身份映射，见 <see cref="DbContext.Set{T}"/>），
/// 但链式算子每次产出新值（§5.11），两者不矛盾。</summary>
public sealed class DbSet<T> : QuickSqlQuery<T>, ITrackedSet<T> where T : class
{
    /// <summary>本类型的档。<b>构造时就先建好</b>，因为物化路径按 <see cref="Type"/> 查档，
    /// 而查不到档意味着这一行没人跟 —— 那是"看起来跟了其实没跟"，本仓库不接受这种降级（§12.2）。</summary>
    readonly EntityTracker<T> _tracker;

    /// <summary>会话那一份调度器。<b>本型的档不够用</b>：补载（<c>Load</c>）要把<em>子型</em>的实体缝进父的导航，
    /// 而"这一行在会话里是哪一只"要按子型那一档问。留会话、不留一堆档，是因为档按 <see cref="Type"/> 查就好。</summary>
    readonly ChangeTracker _session;

    LocalView<T>? _local;

    /// <remarks>这两个标注是从 <see cref="QuickSqlOrm.RootEngine"/> 传下来的入口税（§5.10），
    /// 不是本类自己反射：<b>下面那几个写侧成员一个都没有</b>，那正是 §14「G1 路径下写侧 0 反射」的形状。</remarks>
    [RequiresUnreferencedCode("根模型要按 Type 向注册表取实体契约（G2）。")]
    [RequiresDynamicCode("G2 注册表编译读写委托。")]
    internal DbSet(DbContext context)
        : base(context.Orm.RootEngine(typeof(T), context.Tracker))
    {
        _tracker = context.Tracker.For<T>();
        _session = context.Tracker;
    }

    /// <summary>会话里已跟踪实例的内存视图（§8.1）。<b>只反映本会话见过的实体</b>，不等于库里有什么；
    /// 每次枚举取当时那一份，所以枚举中途 <c>Remove</c> 不炸，但也看不到自己刚删的那一步
    /// （被标删的实例留在视图里直到 <c>SaveChanges</c> 真把那句 <c>DELETE</c> 发出去）。</summary>
    public LocalView<T> Local => _local ??= new LocalView<T>(_tracker.SnapshotEntities, _tracker.Find);

    /// <summary>给已跟踪实例补载导航属性（§8.1）。对当前会话里每一颗 <typeparamref name="T"/> 实体，
    /// 发一条 SQL 把 <paramref name="navigation"/> 指向的那一批子实体拿回来，再按 FK↔PK 缝到父实体的导航属性上。
    /// 引用导航（<c>o =&gt; o.Customer</c>）与集合导航（<c>c =&gt; c.Orders</c>）都走这一颗口，
    /// 分岔判据是导航属性的 CLR 型是不是 <see cref="IEnumerable{T}"/>。
    ///
    /// <para><b>为什么不静默返回 <c>this</c></b>：从前那一版抛"未实现"就是因为"调了 Load 就以为导航有值"
    /// 是最难查的一种假象。现在它真发 SQL 了，返回 <c>this</c> 让链式继续是安全的。</para></summary>
    [RequiresUnreferencedCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    [RequiresDynamicCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    public ITrackedSet<T> Load<TProp>(Expression<Func<T, TProp>> navigation)
        => LoadAsync<TProp>(navigation, default).GetAwaiter().GetResult();

    [RequiresUnreferencedCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    [RequiresDynamicCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    async Task<ITrackedSet<T>> LoadAsync<TProp>(Expression<Func<T, TProp>> navigation, CancellationToken ct)
    {
        var memberName = ExtractMemberName(navigation.Body);
        var parents = _tracker.SnapshotEntities();

        if (parents.Count == 0)
            return this;

        var parentContract = Engine.Model.Root.Contract
            ?? throw new QuickSqlException("Load 需要实体契约：根表位没有绑定实体。");

        var navProperty = parentContract.ClrType.GetProperty(memberName)
            ?? throw new QuickSqlException(
                $"实体 {parentContract.ClrType.Name} 上没有名为 {memberName} 的属性。");

        var childType = GetCollectionElementType(navProperty.PropertyType);

        if (childType is not null)
            await LoadCollectionNavigationAsync(parents, parentContract, memberName, childType, ct).ConfigureAwait(false);
        else
            await LoadReferenceNavigationAsync(parents, parentContract, memberName, navProperty.PropertyType, ct).ConfigureAwait(false);

        return this;
    }

    /// <summary>引用导航补载（如 <c>Load(o =&gt; o.Customer)</c>）：父端有 FK 列指向目标 PK。
    /// 收集所有父实体的 FK 值 → 查目标表 WHERE PK IN (...) → 按 PK 索引 → 缝回父实体。</summary>
    [RequiresUnreferencedCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    [RequiresDynamicCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    async Task LoadReferenceNavigationAsync(
        IReadOnlyList<T> parents, EntityContract parentContract,
        string memberName, Type targetType, CancellationToken ct)
    {
        var relationship = FindRelationship(parentContract, memberName);
        var targetContract = Engine.Orm.Schemas.Contract(targetType);
        var targetRead = Engine.Orm.Schemas.ReadDelegate(targetType);
        var targetPkColumns = targetContract.PrimaryKey;

        var parentFkGetters = BuildKeyGettersByColumnName(parentContract.ClrType, parentContract, relationship.ColumnNames);

        var fkValues = new HashSet<object>(
            parentFkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);
        var parentByFk = new Dictionary<object, List<object>>(
            parentFkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);

        foreach (var parent in parents)
        {
            object fkKey = parentFkGetters.Length == 1
                ? parentFkGetters[0](parent)
                : BuildCompositeKey(parentFkGetters, parent);

            fkValues.Add(fkKey);

            if (!parentByFk.TryGetValue(fkKey, out var list))
            {
                list = [];
                parentByFk[fkKey] = list;
            }

            list.Add(parent);
        }

        var targetTable = Engine.Orm.NewTableRef(targetType, 0);
        var projections = new List<Projection>(targetContract.Columns.Length);

        for (var i = 0; i < targetContract.Columns.Length; i++)
            projections.Add(new Projection(new ColumnExpression(targetTable, targetContract.Columns[i]), null));

        var inValues = new List<SqlExpression>(fkValues.Count);

        foreach (var fkValue in fkValues)
            inValues.Add(new ParameterValue(fkValue, null));

        var conditions = new List<SqlExpression>(targetPkColumns.Length);

        for (var i = 0; i < targetPkColumns.Length; i++)
            conditions.Add(new InExpression(new ColumnExpression(targetTable, targetPkColumns[i]), inValues));

        SqlExpression? where = null;

        foreach (var condition in conditions)
            where = where is null ? condition : new SqlBinaryExpression(BinaryOperator.And, where, condition);

        var ast = new SelectStatement
        {
            Columns = projections,
            From = new SourceTable(targetTable),
            Where = where,
        };

        var dialect = Engine.Orm.Provider.Dialect;
        var writer = new SqlWriter(
            new StringBuilder(QueryCompiler.EstimateSqlLength(ast)),
            new List<AdoParameter>(),
            dialect.ParameterPrefix);
        dialect.Emit(in ast, writer);
        var sql = new DialectSql(writer.ToSqlString(), writer.Parameters);

        var children = await Engine.Orm.Ado.QueryAsync(
            sql,
            Materializer.EntityRows<object>(targetRead, targetContract),
            new ExecutionTag("load-ref", [targetContract.Table.Name], DataSourceRouting.ClusterOf(targetContract.Table)),
            null,
            ct).ConfigureAwait(false);

        var pkGetters = BuildKeyGetters(targetType, targetPkColumns);

        // 缝回父实体那一格：原先是每配到一颗子行就按运行期类型找一次属性、再反射写一次值
        // （DbSet 的 T 就是父型，那颗 Type 从头到尾没变过），现在建一次委托、每行只调用。
        var setNavigation = MemberAccessors.Setter(
            parentContract.ClrType, memberName,
            $"实体 {parentContract.ClrType.Name} 上没有名为 {memberName} 的引用导航属性，无法把补载的实体缝回父实体。",
            $"实体 {parentContract.ClrType.Name}.{memberName} 没有公共写口：Load 要求导航属性可写。");

        foreach (var child in children)
        {
            object pkKey = pkGetters.Length == 1
                ? pkGetters[0](child)
                : BuildCompositeKey(pkGetters, child);

            if (parentByFk.TryGetValue(pkKey, out var matchedParents))
            {
                // 缝会话里的那一只（§8.2）：这一行可能早就在跟踪档里（先查过它、或它刚被别处补载过），
                // 而按刚物化那只缝会让同一行在会话里有两只实例。
                var stitched = _session.CanonicalOf(child, targetType) ?? child;

                foreach (var parent in matchedParents)
                    setNavigation(parent, stitched);
            }
        }
    }

    /// <summary>集合导航补载（如 <c>Load(c =&gt; c.Orders)</c>）：子端有 FK 列指向父端 PK。
    /// 收集所有父实体的 PK 值 → 查子表 WHERE FK IN (...) → 按 FK 分组 → 缝回父实体的集合属性。</summary>
    [RequiresUnreferencedCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    [RequiresDynamicCode("Load 按运行期 Type 反射读取 FK/PK 值与设置导航属性。")]
    async Task LoadCollectionNavigationAsync(
        IReadOnlyList<T> parents, EntityContract parentContract,
        string memberName, Type childType, CancellationToken ct)
    {
        var childContract = Engine.Orm.Schemas.Contract(childType);
        var childRead = Engine.Orm.Schemas.ReadDelegate(childType);
        var fkColumnNames = DiscoverFkColumns(parentContract, childContract, memberName);

        var parentPkGetters = BuildKeyGetters(parentContract.ClrType, parentContract.PrimaryKey);

        var pkValues = new HashSet<object>(
            parentPkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);
        var parentByPk = new Dictionary<object, object>(
            parentPkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);

        foreach (var parent in parents)
        {
            object pkKey = parentPkGetters.Length == 1
                ? parentPkGetters[0](parent)
                : BuildCompositeKey(parentPkGetters, parent);

            pkValues.Add(pkKey);
            parentByPk[pkKey] = parent;
        }

        var childTable = Engine.Orm.NewTableRef(childType, 0);
        var projections = new List<Projection>(childContract.Columns.Length);

        for (var i = 0; i < childContract.Columns.Length; i++)
            projections.Add(new Projection(new ColumnExpression(childTable, childContract.Columns[i]), null));

        var inValues = new List<SqlExpression>(pkValues.Count);

        foreach (var pkValue in pkValues)
            inValues.Add(new ParameterValue(pkValue, null));

        var conditions = new List<SqlExpression>(fkColumnNames.Count);

        for (var i = 0; i < fkColumnNames.Count; i++)
            conditions.Add(new InExpression(new ColumnExpression(childTable, childContract.ByColumn[fkColumnNames[i]]), inValues));

        SqlExpression? where = null;

        foreach (var condition in conditions)
            where = where is null ? condition : new SqlBinaryExpression(BinaryOperator.And, where, condition);

        var ast = new SelectStatement
        {
            Columns = projections,
            From = new SourceTable(childTable),
            Where = where,
        };

        var dialect = Engine.Orm.Provider.Dialect;
        var writer = new SqlWriter(
            new StringBuilder(QueryCompiler.EstimateSqlLength(ast)),
            new List<AdoParameter>(),
            dialect.ParameterPrefix);
        dialect.Emit(in ast, writer);
        var sql = new DialectSql(writer.ToSqlString(), writer.Parameters);

        var children = await Engine.Orm.Ado.QueryAsync(
            sql,
            Materializer.EntityRows<object>(childRead, childContract),
            new ExecutionTag("load-coll", [childContract.Table.Name], DataSourceRouting.ClusterOf(childContract.Table)),
            null,
            ct).ConfigureAwait(false);

        var fkGetters = BuildKeyGettersByColumnName(childType, childContract, fkColumnNames);

        var grouped = new Dictionary<object, List<object>>(
            fkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);

        foreach (var child in children)
        {
            object fkKey = fkGetters.Length == 1
                ? fkGetters[0](child)
                : BuildCompositeKey(fkGetters, child);

            if (!grouped.TryGetValue(fkKey, out var list))
            {
                list = [];
                grouped[fkKey] = list;
            }

            list.Add(child);
        }

        // 缝回集合导航：建一次四把委托（读、写、追加、造表），每行只调用。
        // 原先这三处反射分散在每只父实体上 —— 找属性、按子行实际类型造 List<>、再反射写值。
        var nav = MemberAccessors.BindCollection(
            parentContract.ClrType, memberName, childType,
            $"实体 {parentContract.ClrType.Name} 上没有名为 {memberName} 的集合导航属性，无法把补载的子实体缝回父实体。");

        // 枚举的是<em>父</em>而不是分组结果：一只没有子行的父同样要拿到一句"我捞过，这里没有人"。
        // 只缝有人的那几只会让空串留下一颗 null，而 null 在级联删除那一档是"还没捞"（裁决①），
        // 于是按消息给的出路 Load 一次之后仍然删不下去 —— 那句出路就成了死路。
        foreach (var (pkKey, parent) in parentByPk)
        {
            var collection = nav.NewCollection();

            if (grouped.TryGetValue(pkKey, out var childList))
                foreach (var child in childList)
                    // 同引用导航那一档：串里挂的是会话里的那一只，不是刚物化那一只（§8.2 同一行同一实例）。
                    // 挂了双胞胎的后果要到级联删除才看得见：那一颗撞的是"同键两只实例在会话里没有定义"。
                    nav.Add(collection, _session.CanonicalOf(child, childType) ?? child);

            nav.Setter(parent, collection);

            // 缝完就记基线：从这一刻起"这一串里有谁"是一句<em>捞过</em>的声明，
            // 之后从里面摘掉一只，会话才判得出要删的是那一行（裁决③，见 Node.Baselines）。
            _tracker.RememberCollections((T) parent);
        }
    }

    static string ExtractMemberName(Expression body)
    {
        if (body is MemberExpression member)
            return member.Member.Name;

        throw new QuickSqlException(
            $"Load 的 lambda 必须是简单的属性访问（如 o => o.Customer），当前表达式形态是 {body.NodeType}。");
    }

    static RelationshipContract FindRelationship(EntityContract contract, string memberName)
    {
        foreach (var rel in contract.Relationships)
        {
            if (rel.MemberName == memberName)
                return rel;
        }

        throw new QuickSqlException(
            $"实体 {contract.ClrType.Name} 上没有名为 {memberName} 的导航属性。" +
            $"已注册的关系：{string.Join(", ", contract.Relationships.Select(r => r.MemberName).DefaultIfEmpty("(无)"))}。");
    }

    [RequiresUnreferencedCode("按运行期 Type 反射判集合元素型。")]
    [RequiresDynamicCode("按运行期 Type 反射判集合元素型。")]
    static Type? GetCollectionElementType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>补载/缝合那一条路的外键发现。<b>三档与级联落键那三档同名同序</b>
    /// （跟踪器侧的 <c>ResolveCascadeForeignKey</c>）：同一根边在两条路径上不许有两个答案，
    /// 否则「级联插得进去、Load 捞不上来」就成了裁决①那条出路自己走不通的死路。</summary>
    static IReadOnlyList<string> DiscoverFkColumns(
        EntityContract parentContract, EntityContract childContract, string memberName)
    {
        var parentKeys = parentContract.PrimaryKey;

        if (parentKeys.Length == 0)
            throw new QuickSqlException(
                $"父实体 {parentContract.ClrType.Name} 没有主键，无法发现子实体 {childContract.ClrType.Name} 的外键。");

        foreach (var rel in childContract.Relationships)
        {
            if (rel.TargetType == parentContract.ClrType)
            {
                if (rel.ColumnNames.Count != parentKeys.Length)
                    throw new QuickSqlException(
                        $"子实体 {childContract.ClrType.Name} 到 {parentContract.ClrType.Name} 的关系有 {rel.ColumnNames.Count} 个外键列，" +
                        $"但父实体有 {parentKeys.Length} 个主键列，数量不匹配。");

                return rel.ColumnNames;
            }
        }

        if (ContractResolver.ResolveDeclaredFkColumns(parentContract, childContract, memberName) is { } declaredColumns)
            return declaredColumns;

        var parentTypeName = parentContract.ClrType.Name;

        if (parentKeys.Length == 1)
        {
            var conventionNames = new[] { $"{parentTypeName}Id", $"{parentTypeName}ID" };

            foreach (var conventionName in conventionNames)
            {
                if (childContract.ByColumn.ContainsKey(conventionName))
                    return [conventionName];
            }

            var byColumnName = $"{parentTypeName}{parentKeys[0].ColumnName}";

            if (childContract.ByColumn.ContainsKey(byColumnName))
                return [byColumnName];
        }

        var compositeNames = new List<string>(parentKeys.Length);

        foreach (var pk in parentKeys)
        {
            var name = $"{parentTypeName}{pk.ColumnName}";

            if (!childContract.ByColumn.ContainsKey(name))
                throw new QuickSqlException(
                    $"子实体 {childContract.ClrType.Name} 上没有指向父实体 {parentContract.ClrType.Name} 的外键列（尝试过约定名 {name}）。");

            compositeNames.Add(name);
        }

        return compositeNames;
    }

    [RequiresUnreferencedCode("按运行期 Type 反射取主键属性 getter。")]
    [RequiresDynamicCode("把主键属性读口编译成委托。")]
    static Func<object, object>[] BuildKeyGetters(Type entityType, ColumnContract[] keyColumns)
    {
        var getters = new Func<object, object>[keyColumns.Length];

        for (var i = 0; i < keyColumns.Length; i++)
            getters[i] = MemberAccessors.Getter(
                entityType, keyColumns[i].MemberName,
                $"实体 {entityType.Name} 上没有主键属性 {keyColumns[i].MemberName}",
                $"实体 {entityType.Name} 的主键属性 {keyColumns[i].MemberName} 没有公共 getter");

        return getters;
    }

    [RequiresUnreferencedCode("按运行期 Type 反射取 FK 属性 getter。")]
    [RequiresDynamicCode("把外键列对应属性的读口编译成委托。")]
    static Func<object, object>[] BuildKeyGettersByColumnName(Type entityType, EntityContract contract, IReadOnlyList<string> columnNames)
    {
        var getters = new Func<object, object>[columnNames.Count];

        for (var i = 0; i < columnNames.Count; i++)
        {
            var memberName = contract.ByColumn[columnNames[i]].MemberName;

            getters[i] = MemberAccessors.Getter(
                entityType, memberName,
                $"实体 {entityType.Name} 上没有属性 {memberName}",
                $"实体 {entityType.Name} 的属性 {memberName} 没有公共 getter");
        }

        return getters;
    }

    static object BuildCompositeKey(Func<object, object>[] getters, object entity)
    {
        var vals = new object[getters.Length];

        for (var j = 0; j < getters.Length; j++)
            vals[j] = getters[j](entity);

        return vals;
    }

    sealed class StructuralComparer : IEqualityComparer<object>
    {
        public static readonly StructuralComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x is object[] ax && y is object[] ay)
            {
                if (ax.Length != ay.Length) return false;

                for (var i = 0; i < ax.Length; i++)
                    if (!Equals(ax[i], ay[i])) return false;

                return true;
            }

            return object.Equals(x, y);
        }

        public int GetHashCode(object obj)
        {
            if (obj is object[] arr)
            {
                var hash = new HashCode();

                foreach (var item in arr)
                    hash.Add(item);

                return hash.ToHashCode();
            }

            return obj.GetHashCode();
        }
    }

    // ── 写侧（§8.2 状态机的入口） ──────────────────────────────────

    /// <summary>标为新增。与 <see cref="DbContext.Add{T}"/> 是同一条路，只是 <c>T</c> 已经在这只对象上。</summary>
    public T Add(T entity)
    {
        _tracker.Add(entity);

        return entity;
    }

    public void AddRange(IEnumerable<T> entities)
    {
        foreach (var entity in entities)
            _tracker.Add(entity);
    }

    /// <summary>承认为「库里已有、当前值就是库值」：不写任何列，只登记快照。用于「我手里这只是从别处来的」。</summary>
    public T Attach(T entity)
    {
        _tracker.Attach(entity);

        return entity;
    }

    /// <summary>纳入会话且每列都算脏（§8.3 那两行 <c>Attach</c> + <c>Entry(...).State = Modified</c> 的合成形态）。</summary>
    public T Update(T entity)
    {
        _tracker.Update(entity);

        return entity;
    }

    /// <summary>标为删除。<b>此刻还没有发 <c>DELETE</c></b>，所以它仍在 <see cref="Local"/> 里；
    /// 而 <c>SaveChanges</c> 一抛就整个回滚（§6 (1)），那一行会原样回到库里。</summary>
    public T Remove(T entity)
    {
        _tracker.Remove(entity);

        return entity;
    }

    public void RemoveRange(IEnumerable<T> entities)
    {
        foreach (var entity in entities)
            _tracker.Remove(entity);
    }

    /// <summary>本会话中这一行的状态与逐列入口（§8.2）。<b>不在会话里的实例在这里抛</b>：
    /// 取一个入口顺手把实体纳入会话，是一种没人说过的写入意图。泛型那一档才有收 lambda 的 <c>Property</c>。</summary>
    public EntityEntry<T> Entry(T entity) => _tracker.Entry(entity);

    // ── Find / Refresh（§8.3） ──────────────────────────────────────

    public T? Find(params object?[] primaryKeyValues)
        => FindAsync(default, primaryKeyValues).GetAwaiter().GetResult();

    public Task<T?> FindAsync(CancellationToken ct = default, params object?[] primaryKeyValues)
    {
        var cached = _tracker.Find(primaryKeyValues);

        if (cached is not null)
            return Task.FromResult<T?>(cached);

        return FindFromDbAsync(primaryKeyValues, ct);
    }

    async Task<T?> FindFromDbAsync(object?[] primaryKeyValues, CancellationToken ct)
    {
        var result = await ByPkQuery(primaryKeyValues).CappedAsync<T>(1, ct).ConfigureAwait(false);

        return result.Count > 0 ? result[0] : null;
    }

    public void Refresh(T entity)
        => RefreshAsync(entity, default).GetAwaiter().GetResult();

    public async Task RefreshAsync(T entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var schema = Engine.Orm.Schemas.Schema<T>();
        var keys = Engine.Model.Root.Contract!.PrimaryKey;
        var pkValues = new object?[keys.Length];

        for (var i = 0; i < keys.Length; i++)
            pkValues[i] = schema.GetValue(entity, Array.IndexOf(Engine.Model.Root.Contract.Columns, keys[i]));

        var fresh = await ByPkQuery(pkValues).NoTracking().FirstAsync<T>(ct).ConfigureAwait(false);

        if (fresh is null)
            throw new QuickSqlException("Refresh 找不到这一行：库里没有这个主键的记录。");

        var columns = Engine.Model.Root.Contract.Columns;
        var values = new object?[columns.Length];

        for (var i = 0; i < values.Length; i++)
            values[i] = schema.GetValue(fresh, i);

        _tracker.ApplyFreshValues(entity, values);
    }

    QueryEngine ByPkQuery(object?[] primaryKeyValues)
    {
        var root = Engine.Model.Root;
        var contract = root.Contract
            ?? throw new QuickSqlException("Find/Refresh 需要实体契约：根表位没有绑定实体。");
        var keys = contract.PrimaryKey;

        if (keys.Length == 0)
            throw new QuickSqlException($"{contract.Table.Name} 没有主键：Find/Refresh 无法构建 WHERE 条件。");

        if (primaryKeyValues.Length != keys.Length)
            throw new QuickSqlException($"{contract.Table.Name} 的主键有 {keys.Length} 列，这里给了 {primaryKeyValues.Length} 个值。");

        SqlExpression? predicate = null;

        for (var i = 0; i < keys.Length; i++)
        {
            var column = keys[i];
            var equal = new SqlBinaryExpression(BinaryOperator.Equal,
                new ColumnExpression(root, column),
                new ParameterValue(primaryKeyValues[i], column));

            predicate = predicate is null ? equal : new SqlBinaryExpression(BinaryOperator.And, predicate, equal);
        }

        return Engine.Derive(Engine.Model.AddPredicate(predicate!));
    }
}
