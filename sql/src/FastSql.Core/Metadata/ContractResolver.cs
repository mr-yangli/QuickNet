using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using FastSql.Metadata.Fluent;
using FastSql.Providers;
using Da = System.ComponentModel.DataAnnotations;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;

namespace FastSql.Metadata;

/// <summary>§10.2 的解析链实现：Fluent &gt; 自研属性 &gt; DataAnnotations &gt; 约定 &gt; provider 兜底。
/// 这是 G2 兜底路径（启动期反射一次、结果冻结）；G1 源生成器产出同样的 <see cref="EntityContract"/>，
/// 两条路径由 §13.11 的用例断言逐字段相等。</summary>
public sealed class ContractResolver(IDbProvider provider, ModelConfiguration? configuration = null)
{
    readonly ConditionalWeakTable<Type, EntityContract> _cache = new();

    public IDbProvider Provider { get; } = provider;

    public ModelConfiguration Configuration { get; } = configuration ?? new ModelConfiguration();

    [RequiresUnreferencedCode("G2 兜底路径按 Type 扫描属性与注解；AOT 场景要用 G1 生成的契约（§12.7）。")]
    public EntityContract Resolve(Type clrType) => _cache.GetValue(clrType, Build);

    [RequiresUnreferencedCode("G2 兜底路径按 Type 扫描属性与注解；AOT 场景要用 G1 生成的契约（§12.7）。")]
    public EntityContract Resolve<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
        where T : class => Resolve(typeof(T));

    [RequiresUnreferencedCode("G2 兜底路径按 Type 扫描属性与注解；主路径是 G1 源生成。")]
    EntityContract Build(Type clrType)
    {
        var fluent = Configuration.Find(clrType);
        var table = ResolveTable(clrType, fluent);
        var members = ReadMembers(clrType);
        var columns = new List<ColumnFacts>();

        foreach (var member in members)
        {
            if (member.Ignored is null)
                columns.Add(ResolveColumn(member, fluent));
        }

        // 收尾（provider 的 storage defaults、自增约定、索引与外键的列名换算）在 EntityContractFactory 里，
        // 与 G1 生成的事实共用同一段代码：这条链的正确性由 §13.11 的两侧逐字段相等断言兜住。
        return EntityContractFactory.Build(clrType, table, [.. columns], ResolveIndexFacts(clrType, fluent),
            ResolveRelationships(members, columns), Provider.Types);
    }

    TableInfo ResolveTable(Type clrType, FluentEntity? fluent)
    {
        var fastsql = clrType.GetCustomAttribute<TableAttribute>();
        var data = clrType.GetCustomAttribute<DaSchema.TableAttribute>();

        if (fastsql is not null && data is not null && fastsql.Name != data.Name)
            throw new MetadataConflictException(clrType.Name, [$"[Table(\"{fastsql.Name}\")]", $"[Table(\"{data.Name}\")]"]);

        var name =
            fluent?.TableName is { } fn ? fn
            : fastsql?.Name is { } cn ? cn
            : data?.Name is { } dn ? dn
            : clrType.Name;

        // 分表只认自研属性那一层（DataAnnotations 里没有等价注解，Fluent 侧今天不落这一格，
        // 理由写在 SplitTableAttribute 的注释上）。名字是哪一层给的不影响这一格：模板名与分片声明
        // 是两件事，把它们绑在同一层会让 [Table(DataAnnotations)] + [SplitTable] 这种组合没法写。
        var split = clrType.GetCustomAttribute<SplitTableAttribute>();

        return new TableInfo(fluent?.Schema ?? fastsql?.Schema ?? data?.Schema, name,
            split is null ? null : new SplitFacts(split.Kind, split.Field, split.Buckets));
    }

    [RequiresUnreferencedCode("按 Type 扫描公共属性。")]
    List<MemberFacts> ReadMembers(Type clrType)
    {
        var result = new List<MemberFacts>();
        var nullability = new NullabilityInfoContext();

        foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.MetadataToken))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var fastsql = property.GetCustomAttribute<ColumnAttribute>();
            var facts = new MemberFacts(property)
            {
                FastSqlColumn = fastsql,
                DataColumn = property.GetCustomAttribute<DaSchema.ColumnAttribute>(),
            };

            if (fastsql?.IsIgnore == true)
                facts.Ignored = "[Column(IsIgnore=true)]";
            else if (property.GetCustomAttribute<DaSchema.NotMappedAttribute>() is not null)
                facts.Ignored = "[NotMapped]";
            else if (property.PropertyType == typeof(byte[]) || IsMappable(property.PropertyType))
                facts.IsNullable = nullability.Create(property).WriteState == NullabilityState.Nullable;
            else if (IsReferenceNavigation(property.PropertyType))
            {
                // 引用导航不是列，但它是关系：外键列在依赖侧这一张表上，早先那一版把它连同别的
                // 不可映射类型一起丢掉，于是写侧连"谁依赖谁"都无从知道（§8.2 的拓扑那一格）。
                facts.Ignored = "引用导航（不是列）";
                facts.Reference = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                facts.ForeignKeyMembers = ForeignKeyOf(property);
            }
            else
                facts.Ignored = "非可映射类型（集合导航由 §5.6 的缝合路径处理，今天未接线）";

            result.Add(facts);
        }

        return result;
    }

    static bool IsMappable(Type type)
    {
        var core = Nullable.GetUnderlyingType(type) ?? type;
        return core.IsPrimitive
               || core == typeof(string)
               || core == typeof(decimal)
               || core == typeof(Guid)
               || core == typeof(DateTime)
               || core == typeof(DateTimeOffset)
               || core == typeof(DateOnly)
               || core == typeof(TimeOnly)
               || core == typeof(TimeSpan)
               || core.IsEnum;
    }

    /// <summary>引用导航 = 类类型、不是 <c>string</c>、不是集合形态。数组与 <c>List&lt;T&gt;</c> 走不到这里：
    /// 那是集合导航，§5.6 的缝合路径还没接线，今天仍然只是「不可映射而跳过」。
    /// <b>判定不要求目标类型是实体</b>——一个 <c>Uri</c> 属性配上 <c>UriId</c> 列也会算出一条边，
    /// 而这条边今天唯一的用法（写序）对没有跟踪档的类型天然无效。G1 侧同一条判定。</summary>
    static bool IsReferenceNavigation(Type type)
    {
        var core = Nullable.GetUnderlyingType(type) ?? type;

        return core.IsClass && core != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(core);
    }

    /// <summary><c>[ForeignKey]</c> 只读<em>导航属性</em>上那一颗（反向标注在属性上、指向导航的那一种
    /// 形态没有实现，§10.2 的层清单里它本来就只有一格）。逗号分隔 = 复合外键，两侧同一句拆分。</summary>
    static string[]? ForeignKeyOf(PropertyInfo property)
        => property.GetCustomAttribute<DaSchema.ForeignKeyAttribute>() is { } declared ? Split(declared.Name) : null;

    /// <summary>空串按"没声明"处理（G1 侧同一个答案，于是两侧都退回约定那一档）。</summary>
    static string[]? Split(string? names)
    {
        if (string.IsNullOrEmpty(names))
            return null;

        var members = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return members.Length > 0 ? members : null;
    }

    /// <summary>声明折成 <see cref="RelationshipFacts"/>，与索引同一取向：列名换算、
    /// 「外键指向未映射属性」那条拒绝都在 <see cref="EntityContractFactory"/>，只有一份。
    /// <b>约定那一档要先自己确认属性在场</b>：命不中就根本没有关系（不是错误），
    /// 而 <c>[ForeignKey]</c> 写了却没那一列 = 配置错误，交给收尾那条消息抛出去。</summary>
    static IReadOnlyList<RelationshipFacts> ResolveRelationships(List<MemberFacts> members, List<ColumnFacts> columns)
    {
        var result = new List<RelationshipFacts>();

        foreach (var member in members)
        {
            if (member.Reference is not { } target)
                continue;

            if (member.ForeignKeyMembers is { } declared)
            {
                result.Add(new RelationshipFacts
                {
                    MemberName = member.Property.Name,
                    TargetType = target,
                    ForeignKeyMembers = declared,
                    Layer = MetadataLayer.DataAccessAnnotation,
                    Note = $"[ForeignKey(\"{string.Join(",", declared)}\")]",
                });

                continue;
            }

            // 约定：导航属性名 + "Id"。G1 侧同一句（EntityFactsBuilder.RelationshipsOf）。
            var convention = member.Property.Name + "Id";

            if (!columns.Exists(c => string.Equals(c.MemberName, convention, StringComparison.Ordinal)))
                continue;

            result.Add(new RelationshipFacts
            {
                MemberName = member.Property.Name,
                TargetType = target,
                ForeignKeyMembers = [convention],
                Layer = MetadataLayer.Convention,
                Note = $"约定：{member.Property.Name} ⇒ {convention}",
            });
        }

        return [.. result];
    }

    ColumnFacts ResolveColumn(MemberFacts member, FluentEntity? fluent)
    {
        var property = member.Property;
        var name = property.Name;
        var fluentMember = fluent?.Members.GetValueOrDefault(name);
        var fastsql = member.FastSqlColumn;
        var dataColumn = member.DataColumn;

        var required = property.GetCustomAttribute<Da.RequiredAttribute>();
        var stringLength = property.GetCustomAttribute<Da.StringLengthAttribute>();
        var maxLength = property.GetCustomAttribute<Da.MaxLengthAttribute>();

        // 同层冲突：StringLength 与 MaxLength 都在 DataAnnotations 层（§10.2）。取严格者会把
        // 「建表按 20、校验按 50」这种三个月后才现形的 bug 藏起来，所以直接抛。
        if (stringLength is not null && maxLength is not null)
            throw new MetadataConflictException(name, [$"[StringLength({stringLength.MaximumLength})]", $"[MaxLength({maxLength.Length})]"]);

        (string Value, MetadataLayer Layer, string Note) columnName =
            fluentMember?.ColumnName is { } fc ? (fc, MetadataLayer.Fluent, "Fluent HasColumnName")
            : fastsql?.Name is { } nc ? (nc, MetadataLayer.FastSqlAttribute, $"[Column(\"{nc}\")]")
            : dataColumn?.Name is { } dc ? (dc, MetadataLayer.DataAccessAnnotation, $"[Column(\"{dc}\")]")
            : (name, MetadataLayer.Convention, "约定：属性名");

        (int Value, MetadataLayer Layer, string Note) length =
            fluentMember?.MaxLength is { } fl ? (fl, MetadataLayer.Fluent, $"Fluent HasMaxLength({fl})")
            : fastsql is { Length: > 0 } scl ? (scl.Length, MetadataLayer.FastSqlAttribute, $"[Column(Length={scl.Length})]")
            : stringLength is not null ? (stringLength.MaximumLength, MetadataLayer.DataAccessAnnotation, $"[StringLength({stringLength.MaximumLength})]")
            : maxLength is not null ? (maxLength.Length, MetadataLayer.DataAccessAnnotation, $"[MaxLength({maxLength.Length})]")
            : (0, MetadataLayer.ProviderFallback, "未声明，交给 provider 默认");

        var nullable = ResolveNullable(member, required, fastsql, fluentMember);

        var isKey = fluentMember?.IsKey == true
                    || fastsql?.IsPrimary == TriState.Yes
                    || property.GetCustomAttribute<Da.KeyAttribute>() is not null;

        var identity = fluentMember?.IsIdentity
                       ?? fastsql?.IsIdentity switch
                       {
                           TriState.Yes => true,
                           TriState.No => false,
                           _ => null,
                       };

        int? precision = fluentMember?.Precision ?? (fastsql is { Precision: > 0 } p ? p.Precision : null);
        int? scale = fluentMember?.Scale ?? (fastsql is { Scale: > 0 } s ? s.Scale : null);

        var storeType = fluentMember?.StoreTypeName ?? fastsql?.DbTypes ?? dataColumn?.TypeName;
        var display = property.GetCustomAttribute<Da.DisplayAttribute>();
        var comment = fluentMember?.Comment ?? fastsql?.Comment ?? display?.Description ?? display?.Name;

        // 并发列只有两个来源：自研属性与 Fluent（DataAnnotations 那一层没有等价注解 —— 带这个语义的
        // TimestampAttribute/ConcurrencyCheckAttribute 住在 EF Core 的程序集里，引它等于引 EF Core）。
        var token = fluentMember?.IsConcurrencyToken
                    ?? (property.GetCustomAttribute<ConcurrencyTokenAttribute>() is null ? (bool?) null : true);

        return new ColumnFacts
        {
            MemberName = name,
            ClrType = property.PropertyType,
            ColumnName = columnName.Value,
            ColumnNameLayer = columnName.Layer,
            ColumnNameNote = columnName.Note,
            MaxChars = length.Value == 0 ? null : length.Value,
            LengthNote = length.Note,
            IsNullable = nullable.Value,
            NullableNote = nullable.Note,
            IsPrimary = isKey,
            DeclaredIdentity = identity,
            Precision = precision,
            Scale = scale,
            StoreTypeName = storeType,
            Comment = comment,
            IsConcurrencyToken = token == true,
            ConcurrencyNote = token == true
                ? fluentMember?.IsConcurrencyToken == true ? "Fluent IsConcurrencyToken" : "[ConcurrencyToken]"
                : null,
        };
    }

    static (bool Value, MetadataLayer Layer, string Note) ResolveNullable(
        MemberFacts member, Da.RequiredAttribute? required, ColumnAttribute? fastsql, FluentMember? fluentMember)
    {
        if (fluentMember?.Required is { } fr)
            return (!fr, MetadataLayer.Fluent, $"Fluent IsRequired({fr})");

        if (fastsql?.IsPrimary == TriState.Yes)
            return (false, MetadataLayer.FastSqlAttribute, "[Column(IsPrimary=Yes)] ⇒ 非空");

        if (fastsql?.IsNullable is TriState.Yes or TriState.No)
        {
            var isNullable = fastsql.IsNullable == TriState.Yes;

            return (isNullable, MetadataLayer.FastSqlAttribute, isNullable
                ? required is not null
                    ? "WARN: [Required] 与 [Column(IsNullable=Yes)] 矛盾，按优先级取自研属性"
                    : "[Column(IsNullable=Yes)]"
                : "[Column(IsNullable=No)]");
        }

        if (required is not null)
            return (false, MetadataLayer.DataAccessAnnotation, "[Required]");

        var type = member.Property.PropertyType;

        if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            return (false, MetadataLayer.Convention, "值类型非 Nullable ⇒ 非空列");

        return (member.IsNullable, MetadataLayer.Convention, member.IsNullable ? "C# 可空 ⇒ 可空列" : "C# 非空 ⇒ 非空列");
    }

    /// <summary>只把声明抄成 <see cref="IndexFacts"/>：名字/量纲的换算、同名冲突判定都在
    /// <see cref="EntityContractFactory"/>，与 G1 生成的那条共用一份。<b>顺序即优先级</b>（§10.2），
    /// 所以 Fluent 必须排在注解后面。</summary>
    [RequiresUnreferencedCode("读取 [Index] 注解。")]
    IReadOnlyList<IndexFacts> ResolveIndexFacts(Type clrType, FluentEntity? fluent)
    {
        var facts = new List<IndexFacts>();

        foreach (var index in clrType.GetCustomAttributes<IndexAttribute>())
            facts.Add(new IndexFacts(index.Name, [.. index.MemberNames], index.Unique, index.Clustered, index.Descending));

        foreach (var index in fluent?.Indexes ?? [])
            facts.Add(new IndexFacts(index.Name, index.MemberNames, index.Unique, Descending: index.Descending));

        return [.. facts];
    }

    sealed class MemberFacts(PropertyInfo property)
    {
        public PropertyInfo Property { get; } = property;

        public ColumnAttribute? FastSqlColumn { get; init; }

        public DaSchema.ColumnAttribute? DataColumn { get; init; }

        public string? Ignored { get; set; }

        public bool IsNullable { get; set; }

        /// <summary>非 null = 这一格是引用导航，值为主端实体的 CLR 类型。它与 <see cref="Ignored"/> 同时存在
        /// 是故意的：「不是列」与「是一条关系」是两个问题，各自有各自的答案。</summary>
        public Type? Reference { get; set; }

        /// <summary>只有 <c>[ForeignKey]</c> 给过名字才非 null；约定那一档在 <c>ResolveRelationships</c> 里判。</summary>
        public string[]? ForeignKeyMembers { get; set; }
    }
}
