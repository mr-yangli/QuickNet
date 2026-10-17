using QuickSql.Providers;

namespace QuickSql.Metadata;

/// <summary>解析链在「provider 之前」产出的列事实——G1 与 G2 的公共接口。
/// G2 由 <c>ContractResolver</c> 反射填，G1 由源生成器把同一形状写成代码；
/// 之后 <see cref="EntityContractFactory"/> 只做一次收尾，所以 §10.2 的 origin 文本模板、
/// §9.3 的 storage defaults 与「整型主键 ⇒ 自增」约定全仓库只有一份实现（§10.3）。</summary>
public sealed record ColumnFacts
{
    public required string MemberName { get; init; }

    public required Type ClrType { get; init; }

    public required string ColumnName { get; init; }

    /// <summary>列名来自哪一层。整条 <see cref="ColumnContract.Origin"/> 也取这一层（§10.2「以最高层为准」）。</summary>
    public required MetadataLayer ColumnNameLayer { get; init; }

    public required string ColumnNameNote { get; init; }

    /// <summary>字符上限；null = 未声明，由 provider 兜底。</summary>
    public required int? MaxChars { get; init; }

    public required string LengthNote { get; init; }

    public required bool IsNullable { get; init; }

    public required string NullableNote { get; init; }

    /// <summary>这一列的"非空"究竟是谁说的。<b>默认 true</b>（Fluent∶自研 <c>[Column]</c>∶<c>[Required]</c>∶
    /// 可空上下文里的 <c>string</c> 都是表过态）。唯一落 <c>false</c> 的是<b>可空上下文之外</b>那一格：
    /// <c>#nullable disable</c> 或没带可空元数据的引用程序集（整份 ABP/DDD 框架代码天天在这一格上），
    /// 那里读出来的是"我没说过"，不是"我说了非空"。
    /// <b>它只摘掉必填尺子，不改列的形状</b>：建表照旧发非空列，动那一格等于替现库批量
    /// <c>DROP NOT NULL</c>，而那不是这句话的本意。</summary>
    public bool NullabilityDeclared { get; init; } = true;

    public required bool IsPrimary { get; init; }

    /// <summary>null = 没有任何一层显式说过自增与否，交给「整型主键 ⇒ 自增」约定（要等 provider 定完类型才能判）。</summary>
    public bool? DeclaredIdentity { get; init; }

    public int? Precision { get; init; }

    public int? Scale { get; init; }

    public string? StoreTypeName { get; init; }

    public string? Comment { get; init; }

    /// <summary>并发列标记。缺省 <c>false</c> 而不是 <c>required</c>：这一格没有「没表过态」这一态
    /// （不像自增 —— 那一个要等 provider 定完类型才能判），而两侧漏产出的后果由 §13.11 的逐字段对拍兜住。</summary>
    public bool IsConcurrencyToken { get; init; }

    /// <summary>并发标记来自哪一层，只用于拼 <see cref="ColumnContract.Origin"/>（打了标记才拼进去，
    /// 于是没打标记的实体那串文本一字不动 —— 黄金样本与 <c>Explain()</c> 的既有断言不该为这一格重抄）。</summary>
    public string? ConcurrencyNote { get; init; }

    /// <summary>补上「provider 才知道的事实」之前的那一半：量纲与 Unicode 用中性默认值，
    /// 由 <see cref="ITypeMap.ApplyStorageDefaults"/> 覆写（§10.4「量纲由库决定，不由注解决定」）。</summary>
    /// <remarks><b>非正数的宽度在这里折成"未声明"</b>：<c>[StringLength(-1)]</c> 这一类声明想说的是
    /// "不给上限"，而负数一路走到发射侧就是 <c>VARCHAR(-1)</c> 那句过不了语法关的 DDL。
    /// BCL 那一侧不替我们收：<c>StringLengthAttribute</c> 的构造函数照收 -1（.NET 10.0.12 量过 ——
    /// <c>new StringLengthAttribute(-1)</c> 不抛且 <c>MaximumLength == -1</c>，反射取该属性上的实例
    /// 也原样交回），它只在有人真调用 <c>IsValid</c> 时才响一句 <c>InvalidOperationException</c>
    /// "The maximum length must be a nonnegative integer"。也就是说建契约这一路一路绿灯，直到发射 DDL
    /// 或首次校验才现形 —— 两条路（源码生成与反射）在这里同形，所以收口只能收在这一个点上：
    /// 六家发射侧与校验侧因此都拿不到负数。</remarks>
    public ColumnContract ToProvisional(TableInfo table) => new()
    {
        MemberName = MemberName,
        ClrType = ClrType,
        ColumnName = ColumnName,
        LengthSemantics = LengthSemantics.Char,
        Unicode = true,
        IsNullable = IsNullable,
        NullabilityDeclared = NullabilityDeclared,
        IsPrimary = IsPrimary,
        IsIdentity = false,
        IsConcurrencyToken = IsConcurrencyToken,
        MaxChars = MaxChars > 0 ? MaxChars : null,
        MaxBytes = null,
        Precision = Precision,
        Scale = Scale,
        StoreTypeName = StoreTypeName,
        Comment = Comment,
        Origin = new OriginTrace(ColumnNameLayer, $"{table.Name}.{ColumnName} ← {ColumnNameNote}; 长度 ← {LengthNote}"
            + (MaxChars is { } declared && declared <= 0 ? "（非正数 ⇒ 按未声明宽度处理）" : string.Empty)
            + $"; 可空 ← {NullableNote}"
            + (IsConcurrencyToken ? $"; 并发 ← {ConcurrencyNote}" : string.Empty)),
    };
}

/// <summary>索引在两侧的声明形状：<c>[Index]</c> 注解与 Fluent 的 <c>HasIndex</c> 都折成它，
/// 于是「同名覆盖、同名不同形抛」这条规则也只有一份。优先级由给出的<b>顺序</b>表达（低在前），
/// 而不是再加一个用不上的层字段。</summary>
public sealed record IndexFacts(
    string? Name,
    IReadOnlyList<string> MemberNames,
    bool Unique,
    bool Clustered = false,
    bool Descending = false);

/// <summary>导航在两侧的声明形状：G2 由 <c>ContractResolver</c> 反射填，G1 由源生成器写成代码。
/// 与 <see cref="IndexFacts"/> 同一取向——只抄声明，成员名 → 列名的换算与「外键指向没映射的属性」
/// 那条拒绝都只有 <see cref="EntityContractFactory"/> 一份。</summary>
public sealed record RelationshipFacts
{
    /// <summary>导航属性名（<c>Order.Customer</c>）。它自己不进列清单。</summary>
    public required string MemberName { get; init; }

    /// <summary>主端实体的 CLR 类型。<b>只有类型、没有被引用侧的列名</b>：那一半要另一份契约才算得出来，
    /// 而今天唯一的消费者（写侧的依赖排序）只要这一条边。补 FK 约束文本时要先补它（§14）。</summary>
    public required Type TargetType { get; init; }

    /// <summary>依赖侧的外键成员名。一颗 <c>[ForeignKey("A,B")]</c> 折成两条，复合与单列走同一条判定。
    /// <b><see cref="IsCollection"/> 那一档这里装的是<em>子端</em>的成员名</b>（<c>[ForeignKey]</c> 打在集合
    /// 导航上，EF 同一口径），所以那一档不能拿本实体的列清单去换算，空数组说"没有声明、交给运行期解算"。</summary>
    public required IReadOnlyList<string> ForeignKeyMembers { get; init; }

    /// <summary>外键成员名来自哪一层：自研属性那一层今天没有等价注解，所以只有 DataAnnotations 与约定两档。</summary>
    public required MetadataLayer Layer { get; init; }

    public required string Note { get; init; }

    /// <summary>true = 这是一根<em>集合</em>导航（父端 → 子端，如 <c>Order.Items</c>）。它不进
    /// <see cref="EntityContract.Relationships"/> 而是进 <see cref="EntityContract.Collections"/>：
    /// 两根边的外键住在不同那张表上，混在一格里会让每一个只认 to-one 的消费者（写序、Include、
    /// 外键列改写）拿子端的名字去查本实体的列清单。</summary>
    public bool IsCollection { get; init; }

    /// <summary>级联开关，缺省开。<c>[NoCascade]</c> 打在实体或这一根导航上时折成 <c>false</c>。</summary>
    public bool Cascade { get; init; } = true;

    /// <summary>集合导航的读口（父实体 → 集合位当时的值），只有 <see cref="IsCollection"/> 那一档带。
    /// <b>级联走这把委托而不是现场反射</b>：G1 由生成器写成 lambda，G2 由反射侧在启动期编译一次
    /// （§1.2 原则 3「加载可以反射，调用不行」，与 schema 上那把 <c>Reader</c> 同一取向）。</summary>
    public Func<object, object?>? NavigationReader { get; init; }

    /// <summary>级联的入口那一格，同样只有 <see cref="IsCollection"/> 那一档带：它拿会话交出的
    /// <see cref="QuickSql.Tracking.ICascadeSink"/> 把这一根导航里的子实体送去建档，而
    /// <c>TChild</c> 在<em>写这一格的代码里</em>是编译期已知的（生成的 lambda / 反射侧编译的委托）。
    /// 为什么不能由会话自己 <c>MakeGenericType</c>，理由在 <c>ICascadeSink</c> 的注释上。</summary>
    public Action<QuickSql.Tracking.ICascadeSink, object>? CascadeEnter { get; init; }
}

/// <summary>事实 → <see cref="EntityContract"/> 的唯一收尾路径。</summary>
public static class EntityContractFactory
{
    /// <param name="indexes">按优先级从低到高给出（注解在前、Fluent 在后），同名时后者覆盖前者。</param>
    /// <param name="relationships">引用导航的声明。成员名 → 列名在这里换算，与索引同一个出口，
    /// 所以「外键指向了一个没映射的属性」在两条路径上是同一句话。</param>
    /// <param name="integralPrimaryKeyAutoIncrement">约定那一档给不给「整型主键 ⇒ 自增」。默认 true 与这一格
    /// 落地之前逐字同形；<b>false</b> 是给"主键由客户端生成"那一族库（雪花编号）留的装配声明，
    /// 它只挪约定 —— 注解与 Fluent 表过态的列照旧赢过它，与 §10.2 那条优先级链同形。</param>
    public static EntityContract Build(Type clrType, TableInfo table, IReadOnlyList<ColumnFacts> columns, IReadOnlyList<IndexFacts> indexes,
        IReadOnlyList<RelationshipFacts> relationships, ITypeMap types, bool integralPrimaryKeyAutoIncrement = true)
    {
        var resolved = new ColumnContract[columns.Count];

        for (var i = 0; i < columns.Count; i++)
            resolved[i] = Complete(columns[i], table, types, integralPrimaryKeyAutoIncrement);

        if (!Array.Exists(resolved, static c => c.IsPrimary))
            ApplyKeyConvention(resolved, clrType.Name, integralPrimaryKeyAutoIncrement);

        // 分表这一格要等列算完才判得动（"分片键指向了一颗没映射的属性"要拿列清单比），
        // 而它一改就得改 table 本身，所以放在这里、在契约成形之前。
        if (table.Split is { } split)
            table = table with { Split = CompleteSplit(split, resolved, table, clrType.Name) };

        // 集群归属只有名字要判（连接串的事判不到这一层，那是组装根的启动判据），
        // 而名字判完就得落回 table：两侧（G1 生成的事实、G2 反射读到的注解）从这里同一处收口。
        if (table.Source is { } source)
            table = table with { Source = source with { Name = DataSourceRouting.ValidateName(source.Name, clrType.Name) } };

        var (edges, collections) = MapRelationships(relationships, resolved);

        return new EntityContract(clrType, table, resolved, MapIndexes(indexes, resolved,
            // 约定索引名的底子：名字没有模板的表用表名（与 [Index] 显式点名时代的习惯同源 ——
            // 仓内实体钉着的 IX_{表}_{列} 就是这一形）；分表的表名是 {year} 样模板，按它算名
            // 物理表还没成形，那一样式取类名（镜像 ShardLog 的名字）。
            table.Split is null ? table.Name : clrType.Name),
            edges, collections);
    }

    /// <summary>分片声明 → 已换算的声明，顺手把四条启动期判据比一遍：分片键得是一列、
    /// 表名模板得配得上那一档、哈希档得有桶、主键不许自增。
    /// <b>自增那一条是硬的</b>（规格 §16.4 同一族）：跨片唯一由库自己给的话，两张表能各自发出
    /// 同一个号，而"谁冲突"这件事要到合并统计时才看得见。</summary>
    static SplitFacts CompleteSplit(SplitFacts split, ColumnContract[] columns, TableInfo table, string entityName)
    {
        var column = Array.Find(columns, c => string.Equals(c.MemberName, split.MemberName, StringComparison.Ordinal))
            ?? throw new QuickSqlException($"分表声明的分片键 {split.MemberName} 不是实体 {entityName} 的一列（可能被 [NotMapped] 排除）");

        SplitRouting.ValidateTemplate(split.Kind, table.Name);

        if (split.Kind == SplitKind.Hash && split.Buckets < 2)
            throw new QuickSqlException($"分表声明了 {SplitKind.Hash} 档却只给 {split.Buckets} 个桶（实体 {entityName}），少于 2 个桶等于没分");

        if (Array.Find(columns, c => c.IsPrimary && c.IsIdentity) is { } identity)
            throw new QuickSqlException($"分表实体 {entityName} 的主键 {identity.MemberName} 是自增列：跨片唯一性库给不了。要么由客户端生成主键（[QuickColumn(IsIdentity=TriState.No)]），要么这一张表不分表");

        return split with { ColumnName = column.ColumnName };
    }

    static ColumnContract Complete(ColumnFacts facts, TableInfo table, ITypeMap types, bool integralPrimaryKeyAutoIncrement)
    {
        var withStorage = types.ApplyStorageDefaults(facts.ToProvisional(table));

        // 自增要等类型定下来才能判定：主键 + 整型，且没有任何一层显式说过不是。
        // 装配口说"这一族库的整型主键归客户端给号"时，约定那一半让位给声明，声明那一半原样保留。
        var identity = facts.DeclaredIdentity
                       ?? (integralPrimaryKeyAutoIncrement && facts.IsPrimary && IsIntegral(withStorage.ClrType));

        return withStorage with
        {
            IsIdentity = identity,
            Origin = facts.DeclaredIdentity is null && identity
                ? withStorage.Origin with { Note = withStorage.Origin.Note + "; 自增 ← 约定：整型主键" }
                : withStorage.Origin,
        };
    }

    static void ApplyKeyConvention(ColumnContract[] columns, string entityName, bool integralPrimaryKeyAutoIncrement)
    {
        var index = Array.FindIndex(columns, c =>
            string.Equals(c.MemberName, "Id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.MemberName, entityName + "Id", StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return;

        var found = columns[index];

        columns[index] = found with
        {
            IsPrimary = true,
            // 命中 Id/{Entity}Id 形态只补「谁是键」这一半；自增那一半不许把高优先层的声明盖掉 ——
            // 上面那一档（Complete）已经按声明算过一次，这里只允许约定往"归库给"的方向添，
            // 装配口说这一族库归客户端给号时，只有真表过态自增的列才留得下来。
            IsIdentity = found.IsIdentity || (integralPrimaryKeyAutoIncrement && IsIntegral(found.ClrType)),
            Origin = new OriginTrace(MetadataLayer.Convention, $"{found.MemberName} 命中 Id/{{Entity}}Id 形态"),
        };
    }

    static bool IsIntegral(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(int) || type == typeof(long) || type == typeof(short);
    }

    static IReadOnlyList<IndexContract> MapIndexes(IReadOnlyList<IndexFacts> facts, ColumnContract[] columns, string entityName)
    {
        var result = new Dictionary<string, IndexContract>(StringComparer.Ordinal);

        foreach (var fact in facts)
        {
            var name = fact.Name ?? DefaultIndexName(entityName, fact.MemberNames);
            var kind = fact.Clustered ? IndexKind.Clustered : fact.Unique ? IndexKind.Unique : IndexKind.NonClustered;

            Add(result, new IndexContract(name, kind, [.. fact.MemberNames], fact.Descending)
            {
                ColumnNames = MapToColumns(fact.MemberNames, columns, "索引 " + name),
            });
        }

        return [.. result.Values];
    }

    /// <summary>导航声明按朝向分成两格。<b>to-one 那一格才换算列名</b>：外键列就在这张表上，
    /// 而集合那一格的名字属于子端那张表（换算要另一份契约，于是挪到运行期解算那一处，见
    /// <c>ChangeTracker</c> 的级联）。<b>不查主端</b>：被引用侧的列要另一份契约才算得出来，
    /// 而这一格的消费者（§8.2 的写序）只需要「谁依赖谁」那一条边。</summary>
    static (IReadOnlyList<RelationshipContract> Edges, IReadOnlyList<CollectionEdgeContract> Collections)
        MapRelationships(IReadOnlyList<RelationshipFacts> facts, ColumnContract[] columns)
    {
        var edges = new List<RelationshipContract>(facts.Count);
        var collections = new List<CollectionEdgeContract>(0);

        foreach (var fact in facts)
        {
            if (!fact.IsCollection)
            {
                edges.Add(new RelationshipContract(fact.MemberName, fact.TargetType, [.. fact.ForeignKeyMembers],
                    MapToColumns(fact.ForeignKeyMembers, columns, $"引用导航 {fact.MemberName}"))
                {
                    Origin = new OriginTrace(fact.Layer, fact.Note),
                });

                continue;
            }

            // 集合那一格没有可读的列名，但两把委托必须在：读口拿父实体手里那一串子实体，
            // 入口把取到的子实体送进子端的跟踪器 —— 都是静态型的东西，只能由两侧各自编好带过来。
            collections.Add(new CollectionEdgeContract(fact.MemberName, fact.TargetType,
                [.. fact.ForeignKeyMembers],
                fact.NavigationReader ?? throw new QuickSqlException(MissingAccessor(fact, "读口")),
                fact.CascadeEnter ?? throw new QuickSqlException(MissingAccessor(fact, "级联入口")))
            {
                Origin = new OriginTrace(fact.Layer, fact.Note),
                Cascade = fact.Cascade,
            });
        }

        return (edges, collections);
    }

    /// <summary>集合边的委托缺口的消息：把「哪一层声明的这条边」一起写进去，因为两侧生成器与反射解析器
    /// 只有一侧漏带时，报错的那次运行可能走的是另一侧，光看成员名分不出是谁的锅。</summary>
    static string MissingAccessor(RelationshipFacts fact, string what)
        => $"集合导航 {fact.MemberName} 没有{what}（声明来自 {fact.Layer}：{fact.Note}）：级联要靠它取子实体并送进会话，两侧生成器与反射解析器都必须带这把委托。";

    /// <summary>同名且同形 = 两层声明了同一件事，留高优先级的最后那条；同名不同形 = 配置错误，抛。</summary>
    static void Add(IDictionary<string, IndexContract> result, IndexContract index)
    {
        if (result.TryGetValue(index.Name, out var existing)
            && !existing.MemberNames.SequenceEqual(index.MemberNames, StringComparer.Ordinal))
        {
            throw new MetadataConflictException(index.Name,
            [
                $"索引 {index.Name} 被声明为 ({string.Join(", ", index.MemberNames)})",
                $"又被声明为 ({string.Join(", ", existing.MemberNames)})",
            ]);
        }

        result[index.Name] = index;
    }

    static string DefaultIndexName(string entityName, IReadOnlyList<string> memberNames)
        => "IX_" + entityName + "_" + string.Join("_", memberNames);

    /// <param name="declaration">这句话是谁说的（<c>索引 IX_x</c> / <c>引用导航 Customer</c>）——
    /// 成员名换不成列名时，消息要指回声明本身，而不是只报一个属性名。</param>
    static string[] MapToColumns(IReadOnlyList<string> memberNames, ColumnContract[] columns, string declaration)
    {
        var result = new string[memberNames.Count];

        for (var i = 0; i < memberNames.Count; i++)
        {
            result[i] = Array.Find(columns, c => string.Equals(c.MemberName, memberNames[i], StringComparison.Ordinal))
                        ?.ColumnName
                        ?? throw new QuickSqlException($"{declaration} 引用了未映射的属性 {memberNames[i]}（可能被 [NotMapped] 排除）");
        }

        return result;
    }
}

/// <summary><see cref="ValidationMode.ContractOnly"/> 档的两条尺子（§10.6）。
/// G1 生成的校验器与 G2 的反射校验器都调这里，所以失败消息的文本也只有一份——
/// 否则「同一行数据两条路径报出两句话」这种偏差只能靠人眼发现。</summary>
public static class ContractRules
{
    public static void Required(ColumnContract column, List<ValidationFailure> failures)
    {
        // "没表过态"不是一句"可以说非空"的许可（§10.6 的尺子量的是声明）：可空上下文之外读回来的
        // 非空形状不挡写入，否则 ABP/DDD 那一族实体的合法 null 会被一次没说过的话拒掉。
        // 判据只在这一处，G1 生成的校验器与 G2 的反射校验器因此不会各说一句。
        if (!column.NullabilityDeclared)
            return;

        failures.Add(new ValidationFailure(column.MemberName, "required", $"{column.MemberName} 不可为空（列 {column.ColumnName}，来源 {column.Origin}）"));
    }

    /// <summary>量纲用列自己声明的那把尺子，不是 <c>string.Length</c>（§10.4）。</summary>
    public static void Length(ColumnContract column, string value, List<ValidationFailure> failures)
    {
        var (fits, ruler) = column.LengthSemantics == LengthSemantics.Byte
            ? (column.FitsBytes(value), $"{column.MaxBytes} 字节")
            : (column.FitsChars(value), $"{column.MaxChars} 字符");

        if (!fits)
            failures.Add(new ValidationFailure(column.MemberName, "length", $"{column.MemberName} 超出列 {column.ColumnName} 的 {ruler}"));
    }
}
