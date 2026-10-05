using FastSql.Providers;

namespace FastSql.Metadata;

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
    public ColumnContract ToProvisional(TableInfo table) => new()
    {
        MemberName = MemberName,
        ClrType = ClrType,
        ColumnName = ColumnName,
        LengthSemantics = LengthSemantics.Char,
        Unicode = true,
        IsNullable = IsNullable,
        IsPrimary = IsPrimary,
        IsIdentity = false,
        IsConcurrencyToken = IsConcurrencyToken,
        MaxChars = MaxChars,
        MaxBytes = null,
        Precision = Precision,
        Scale = Scale,
        StoreTypeName = StoreTypeName,
        Comment = Comment,
        Origin = new OriginTrace(ColumnNameLayer, $"{table.Name}.{ColumnName} ← {ColumnNameNote}; 长度 ← {LengthNote}; 可空 ← {NullableNote}"
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

/// <summary>引用导航在两侧的声明形状：G2 由 <c>ContractResolver</c> 反射填，G1 由源生成器写成代码。
/// 与 <see cref="IndexFacts"/> 同一取向——只抄声明，成员名 → 列名的换算与「外键指向没映射的属性」
/// 那条拒绝都只有 <see cref="EntityContractFactory"/> 一份。</summary>
public sealed record RelationshipFacts
{
    /// <summary>导航属性名（<c>Order.Customer</c>）。它自己不进列清单。</summary>
    public required string MemberName { get; init; }

    /// <summary>主端实体的 CLR 类型。<b>只有类型、没有被引用侧的列名</b>：那一半要另一份契约才算得出来，
    /// 而今天唯一的消费者（写侧的依赖排序）只要这一条边。补 FK 约束文本时要先补它（§14）。</summary>
    public required Type TargetType { get; init; }

    /// <summary>依赖侧的外键成员名。一颗 <c>[ForeignKey("A,B")]</c> 折成两条，复合与单列走同一条判定。</summary>
    public required IReadOnlyList<string> ForeignKeyMembers { get; init; }

    /// <summary>外键成员名来自哪一层：自研属性那一层今天没有等价注解，所以只有 DataAnnotations 与约定两档。</summary>
    public required MetadataLayer Layer { get; init; }

    public required string Note { get; init; }
}

/// <summary>事实 → <see cref="EntityContract"/> 的唯一收尾路径。</summary>
public static class EntityContractFactory
{
    /// <param name="indexes">按优先级从低到高给出（注解在前、Fluent 在后），同名时后者覆盖前者。</param>
    /// <param name="relationships">引用导航的声明。成员名 → 列名在这里换算，与索引同一个出口，
    /// 所以「外键指向了一个没映射的属性」在两条路径上是同一句话。</param>
    public static EntityContract Build(Type clrType, TableInfo table, IReadOnlyList<ColumnFacts> columns, IReadOnlyList<IndexFacts> indexes,
        IReadOnlyList<RelationshipFacts> relationships, ITypeMap types)
    {
        var resolved = new ColumnContract[columns.Count];

        for (var i = 0; i < columns.Count; i++)
            resolved[i] = Complete(columns[i], table, types);

        if (!Array.Exists(resolved, static c => c.IsPrimary))
            ApplyKeyConvention(resolved, clrType.Name);

        // 分表这一格要等列算完才判得动（"分片键指向了一颗没映射的属性"要拿列清单比），
        // 而它一改就得改 table 本身，所以放在这里、在契约成形之前。
        if (table.Split is { } split)
            table = table with { Split = CompleteSplit(split, resolved, table, clrType.Name) };

        return new EntityContract(clrType, table, resolved, MapIndexes(indexes, resolved, clrType.Name),
            MapRelationships(relationships, resolved));
    }

    /// <summary>分片声明 → 已换算的声明，顺手把四条启动期判据比一遍：分片键得是一列、
    /// 表名模板得配得上那一档、哈希档得有桶、主键不许自增。
    /// <b>自增那一条是硬的</b>（规格 §15.4 同一族）：跨片唯一由库自己给的话，两张表能各自发出
    /// 同一个号，而"谁冲突"这件事要到合并统计时才看得见。</summary>
    static SplitFacts CompleteSplit(SplitFacts split, ColumnContract[] columns, TableInfo table, string entityName)
    {
        var column = Array.Find(columns, c => string.Equals(c.MemberName, split.MemberName, StringComparison.Ordinal))
            ?? throw new FastSqlException($"分表声明的分片键 {split.MemberName} 不是实体 {entityName} 的一列（可能被 [Column(IsIgnore)] 或 [NotMapped] 排除）");

        SplitRouting.ValidateTemplate(split.Kind, table.Name);

        if (split.Kind == SplitKind.Hash && split.Buckets < 2)
            throw new FastSqlException($"分表声明了 {SplitKind.Hash} 档却只给 {split.Buckets} 个桶（实体 {entityName}），少于 2 个桶等于没分");

        if (Array.Find(columns, c => c.IsPrimary && c.IsIdentity) is { } identity)
            throw new FastSqlException($"分表实体 {entityName} 的主键 {identity.MemberName} 是自增列：跨片唯一性库给不了。要么由客户端生成主键（[Column(IsIdentity=TriState.No)]），要么这一张表不分表");

        return split with { ColumnName = column.ColumnName };
    }

    static ColumnContract Complete(ColumnFacts facts, TableInfo table, ITypeMap types)
    {
        var withStorage = types.ApplyStorageDefaults(facts.ToProvisional(table));

        // 自增要等类型定下来才能判定：主键 + 整型，且没有任何一层显式说过不是。
        var identity = facts.DeclaredIdentity ?? (facts.IsPrimary && IsIntegral(withStorage.ClrType));

        return withStorage with
        {
            IsIdentity = identity,
            Origin = facts.DeclaredIdentity is null && identity
                ? withStorage.Origin with { Note = withStorage.Origin.Note + "; 自增 ← 约定：整型主键" }
                : withStorage.Origin,
        };
    }

    static void ApplyKeyConvention(ColumnContract[] columns, string entityName)
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
            IsIdentity = IsIntegral(found.ClrType),
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

    /// <summary>外键成员名换算成列名。<b>不查主端</b>：被引用侧的列要另一份契约才算得出来，
    /// 而这一格的消费者（§8.2 的写序）只需要「谁依赖谁」那一条边。</summary>
    static IReadOnlyList<RelationshipContract> MapRelationships(IReadOnlyList<RelationshipFacts> facts, ColumnContract[] columns)
    {
        var result = new RelationshipContract[facts.Count];

        for (var i = 0; i < facts.Count; i++)
        {
            var fact = facts[i];

            result[i] = new RelationshipContract(fact.MemberName, fact.TargetType, [.. fact.ForeignKeyMembers],
                MapToColumns(fact.ForeignKeyMembers, columns, $"引用导航 {fact.MemberName}"))
            {
                Origin = new OriginTrace(fact.Layer, fact.Note),
            };
        }

        return [.. result];
    }

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
                        ?? throw new FastSqlException($"{declaration} 引用了未映射的属性 {memberNames[i]}（可能被 [NotMapped] 或 [Column(IsIgnore)] 排除）");
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
        => failures.Add(new ValidationFailure(column.MemberName, "required", $"{column.MemberName} 不可为空（列 {column.ColumnName}，来源 {column.Origin}）"));

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
