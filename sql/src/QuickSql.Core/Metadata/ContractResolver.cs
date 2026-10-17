using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using QuickSql.Materialization;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using Da = System.ComponentModel.DataAnnotations;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;
using Sc = System.ComponentModel;

namespace QuickSql.Metadata;

/// <summary>§10.2 的解析链实现：Fluent &gt; 自研属性 &gt; DataAnnotations &gt; 约定 &gt; provider 兜底。
/// 这是 G2 兜底路径（启动期反射一次、结果冻结）；G1 源生成器产出同样的 <see cref="EntityContract"/>，
/// 两条路径由 §13.11 的用例断言逐字段相等。</summary>
public sealed class ContractResolver(IDbProvider provider, ModelConfiguration? configuration = null,
    bool integralPrimaryKeyAutoIncrement = true)
{
    readonly ConditionalWeakTable<Type, EntityContract> _cache = new();

    public IDbProvider Provider { get; } = provider;

    public ModelConfiguration Configuration { get; } = configuration ?? new ModelConfiguration();

    /// <summary>约定那一档给不给"整型主键 ⇒ 自增"（<see cref="QuickSqlOrmOptions.IntegralPrimaryKeyAutoIncrement"/>）。
    /// 它只挪约定：注解与 Fluent 表过态的列照旧赢过它。</summary>
    public bool IntegralPrimaryKeyAutoIncrement { get; } = integralPrimaryKeyAutoIncrement;

    [RequiresUnreferencedCode("G2 兜底路径按 Type 扫描属性与注解；AOT 场景要用 G1 生成的契约（§12.7）。")]
    [RequiresDynamicCode("G2 兜底路径为集合导航编译读口委托；AOT 场景要用 G1 生成的契约（§12.7）。")]
    public EntityContract Resolve(Type clrType) => _cache.GetValue(clrType, Build);

    [RequiresUnreferencedCode("G2 兜底路径按 Type 扫描属性与注解；AOT 场景要用 G1 生成的契约（§12.7）。")]
    [RequiresDynamicCode("G2 兜底路径为集合导航编译读口委托；AOT 场景要用 G1 生成的契约（§12.7）。")]
    public EntityContract Resolve<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
        where T : class => Resolve(typeof(T));

    /// <summary>那一根集合边上点名的外键成员（<c>[ForeignKey]</c> 的第二档）。
    /// 找不到那一根边（导航没被契约收录）就给空串，调用方照旧走第一、第三档 —— 这一格不替配置错误编一个"没有声明"。</summary>
    static IReadOnlyList<string> DeclaredFkMembers(EntityContract parent, string memberName)
    {
        foreach (var edge in parent.Collections)
            if (edge.MemberName == memberName)
                return edge.DeclaredForeignKeyMembers;

        return [];
    }

    /// <summary>第二档：把集合导航上点名的外键成员换算成<b>子端的列名</b>；这根导航没有点名就返回 <c>null</c>。
    /// 三处反推外键的路（<c>DbSet.Load</c>、<c>IncludeMany</c>、子查询里的集合导航）与级联落键那一条
    /// （<c>ChangeTracker.ResolveCascadeForeignKey</c>）共用这一颗：同一根边不许在两条路径上得到两个答案，
    /// 否则"级联插得进去、Load 捞不上来"会把级联删除那条出路（先捞进会话再删）走成死路。</summary>
    internal static IReadOnlyList<string>? ResolveDeclaredFkColumns(EntityContract parent, EntityContract child, string memberName)
    {
        var declared = DeclaredFkMembers(parent, memberName);

        if (declared.Count == 0)
            return null;

        var keys = parent.PrimaryKey;

        if (declared.Count != keys.Length)
            throw new QuickSqlException(
                $"{child.ClrType.Name} 到 {parent.ClrType.Name} 的导航 {memberName} 点了 {declared.Count} 个外键成员，" +
                $"而父实体有 {keys.Length} 个主键列，数量不匹配 —— 复合主键要逐列点名。");

        var columns = new List<string>(declared.Count);

        foreach (var member in declared)
        {
            if (!child.ByMember.TryGetValue(member, out var column))
                throw new QuickSqlException(
                    $"{child.ClrType.Name} 上没有名为 {member} 的列（{parent.ClrType.Name}.{memberName} 点名的外键成员）：" +
                    "外键成员点了名而子端没有那一列，是配置错误。");

            columns.Add(column.ColumnName);
        }

        return columns;
    }

    [RequiresUnreferencedCode("G2 兜底路径按 Type 扫描属性与注解；主路径是 G1 源生成。")]
    [RequiresDynamicCode("G2 兜底路径为集合导航编译读口委托；主路径是 G1 源生成。")]
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
            ResolveRelationships(members, columns), Provider.Types, IntegralPrimaryKeyAutoIncrement);
    }

    TableInfo ResolveTable(Type clrType, FluentEntity? fluent)
    {
        var data = clrType.GetCustomAttribute<DaSchema.TableAttribute>();

        var name =
            fluent?.TableName is { } fn ? fn
            : data?.Name is { } dn ? dn
            : clrType.Name;

        // 分表只认自研属性那一层（DataAnnotations 里没有等价注解，Fluent 侧今天不落这一格，
        // 理由写在 SplitTableAttribute 的注释上）。名字是哪一层给的不影响这一格：模板名与分片声明
        // 是两件事，把它们绑在同一层会让 [Table(DataAnnotations)] + [SplitTable] 这种组合没法写。
        var split = clrType.GetCustomAttribute<SplitTableAttribute>();

        // 集群归属与分表同一取向：只认自研属性那一层、不落 Fluent。落 Fluent 的那一条路会让
        // "同一张表在两条路径上属于两座库"有能发生的形状，而那一格的代价是语句发到另一座库里去。
        var source = clrType.GetCustomAttribute<DataSourceAttribute>();

        // 表备注与表名同一条优先级链（Fluent > 注解 > 文档兜底），只是最后一层只长在 G1（xmlDoc 开关）。
        // 系统的 <c>System.ComponentModel.DataAnnotations.Schema.TableAttribute</c> 只有 Name/Schema 两格，
        // 备注那一格由组件模型的 <c>[Description]</c>（System.ComponentModel）继承：它就是从前的
        // 自研 <c>[Table(Comment=…)]</c> 那一格退场后的接棒者，座次也照旧 —— 排在 Fluent 之后。
        var comment = fluent?.Comment ?? clrType.GetCustomAttribute<Sc.DescriptionAttribute>()?.Description;

        return new TableInfo(fluent?.Schema ?? data?.Schema, name,
            split is null ? null : new SplitFacts(split.Kind, split.Field, split.Buckets),
            source is null ? null : new DataSourceFacts(source.Cluster),
            comment);
    }

    [RequiresUnreferencedCode("按 Type 扫描公共属性。")]
    [RequiresDynamicCode("集合导航的读口要编译委托。")]
    List<MemberFacts> ReadMembers(Type clrType)
    {
        var result = new List<MemberFacts>();
        var nullability = new NullabilityInfoContext();

        foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.MetadataToken))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var facts = new MemberFacts(property)
            {
                QuickColumn = property.GetCustomAttribute<QuickColumnAttribute>(),
                DataColumn = property.GetCustomAttribute<DaSchema.ColumnAttribute>(),
            };

            if (property.GetCustomAttribute<DaSchema.NotMappedAttribute>() is not null)
                facts.Ignored = "[NotMapped]";
            else if (!property.CanWrite)
                // 没有写口的成员既落不回值（读取器给它赋值会在编译当场 ArgumentException:
                // Expression must be writeable），也不是数据：ABP 那一族实体上的
                // <c>public bool RequiresManualGrading =&gt; Type == …</c> 就是这一形 —— 它是算法。
                // 按非公开写口问一次（<c>CanWrite</c>）而不是按公共写口：{ get; protected set; }
                // 与 { get; init; } 都是能落的形状（编译后的表达式树不受可访问性限制），
                // 把它们一起摘掉会让整族 ABP 实体的键与明细列凭空消失。
                facts.Ignored = "没有写口（计算属性，不是列）";
            else if (property.PropertyType == typeof(byte[]) || IsMappable(property.PropertyType))
                facts.Nullability = nullability.Create(property).WriteState;
            else if (IsReferenceNavigation(property.PropertyType))
            {
                // 引用导航不是列，但它是关系：外键列在依赖侧这一张表上，早先那一版把它连同别的
                // 不可映射类型一起丢掉，于是写侧连"谁依赖谁"都无从知道（§8.2 的拓扑那一格）。
                facts.Ignored = "引用导航（不是列）";
                facts.Reference = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                facts.ForeignKeyMembers = ForeignKeyOf(property);
            }
            else if (property.CanRead && CollectionElementOf(property.PropertyType) is { } element)
            {
                // 集合导航也不是列，但它是<em>朝反了的那一根</em>关系（父端 → 子端，外键在子端那张表上）。
                // 丢掉它 = 级联没有边可走，于是 Add 不带子实体、Remove 留一串孤行。
                facts.Ignored = "集合导航（不是列）";
                facts.Collection = element;
                facts.ForeignKeyMembers = ForeignKeyOf(property);
            }
            else
                facts.Ignored = "非可映射类型";

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

    /// <summary>引用导航 = 类类型、不是 <c>string</c>、不是集合形态（数组与 <c>List&lt;T&gt;</c> 走
    /// <see cref="CollectionElementOf"/> 那一格，成为朝反了的边）。
    /// <b>判定不要求目标类型是实体</b>——一个 <c>Uri</c> 属性配上 <c>UriId</c> 列也会算出一条边，
    /// 而这条边今天唯一的用法（写序）对没有跟踪档的类型天然无效。G1 侧 <c>IsReferenceNavigation</c> 同一条。</summary>
    static bool IsReferenceNavigation(Type type)
    {
        var core = Nullable.GetUnderlyingType(type) ?? type;

        return core.IsClass && core != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(core);
    }

    /// <summary>集合导航的元素型：收下了 <see cref="IEnumerable"/>（非泛型那一颗）又不是 <c>string</c>
    /// 也不是 <c>byte[]</c>（那两格在上面那条链里已经是列）的形状，取它 <c>IEnumerable&lt;T&gt;</c> 的那个 <c>T</c>。
    /// <b>只有类形态的元素才算集合导航</b>：<c>List&lt;string&gt;</c>、<c>List&lt;int&gt;</c>、
    /// <c>Dictionary&lt;K,V&gt;</c>（元素是 <c>KeyValuePair</c> 结构体）这些都是标签或配置，不是一串子行，
    /// 给它们出一条边会让 <c>Add</c> 在根本不该级联的地方找外键。两侧同一判据（G1 侧
    /// <c>CollectionElementOf</c>）。</summary>
    [RequiresUnreferencedCode("按运行期 Type 反射判集合元素型。")]
    [RequiresDynamicCode("按运行期 Type 反射判集合元素型。")]
    static Type? CollectionElementOf(Type type)
    {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
            return null;

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var element = iface.GetGenericArguments()[0];

                return element.IsClass && element != typeof(string) ? element : null;
            }
        }

        return null;
    }

    /// <summary><c>[NoCascade]</c> 的两档：打在实体上是"这一张表的导航都不带人"，打在具体那根集合导航上
    /// 是"只有这一根不带"。级联缺省为开，所以这里只判退出（G1 侧同一条，注解读的是源码符号）。</summary>
    static bool IsNoCascade(MemberFacts member)
        => member.Property.GetCustomAttribute<NoCascadeAttribute>() is not null
           || member.Property.DeclaringType!.GetCustomAttribute<NoCascadeAttribute>() is not null;

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
    /// 而 <c>[ForeignKey]</c> 写了却没那一列 = 配置错误，交给收尾那条消息抛出去。
    ///
    /// <para>集合那一格另算：它的外键成员在<em>子端</em>那张表上，这里既换算不了、也不该拿本实体的列清单
    /// 去比，所以声明只带着成员名原样走（没声明就是空数组，运行期级联那一处再解），而级联要的那把读口
    /// 在这里编译一次 —— 启动期反射、级联每行只调委托（§12.7 C 那条界线）。</para></summary>
    [RequiresUnreferencedCode("集合导航的读口按运行期类型与成员名定位属性。")]
    [RequiresDynamicCode("集合导航的读口是编译出来的表达式树。")]
    static IReadOnlyList<RelationshipFacts> ResolveRelationships(List<MemberFacts> members, List<ColumnFacts> columns)
    {
        var result = new List<RelationshipFacts>();

        foreach (var member in members)
        {
            if (member.Collection is { } child)
            {
                result.Add(new RelationshipFacts
                {
                    MemberName = member.Property.Name,
                    TargetType = child,
                    ForeignKeyMembers = member.ForeignKeyMembers ?? [],
                    Layer = member.ForeignKeyMembers is null ? MetadataLayer.Convention : MetadataLayer.DataAccessAnnotation,
                    Note = member.ForeignKeyMembers is { } named
                        ? $"[ForeignKey(\"{string.Join(",", named)}\")]"
                        : $"集合导航：{member.Property.Name} ⇒ {child.Name}",
                    IsCollection = true,
                    Cascade = !IsNoCascade(member),
                    NavigationReader = MemberAccessors.Getter(member.Property.DeclaringType!, member.Property.Name,
                        $"实体 {member.Property.DeclaringType!.Name} 上没有属性 {member.Property.Name}"),
                    CascadeEnter = MemberAccessors.CascadeEnter(member.Property.DeclaringType!, child, member.Property.Name,
                        $"实体 {member.Property.DeclaringType!.Name} 上没有可读的集合属性 {member.Property.Name}"),
                });

                continue;
            }

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
        var quickColumn = member.QuickColumn;
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
            : dataColumn?.Name is { } dc ? (dc, MetadataLayer.DataAccessAnnotation, $"[Column(\"{dc}\")]")
            : (name, MetadataLayer.Convention, "约定：属性名");

        (int Value, MetadataLayer Layer, string Note) length =
            fluentMember?.MaxLength is { } fl ? (fl, MetadataLayer.Fluent, $"Fluent HasMaxLength({fl})")
            : quickColumn is { Length: > 0 } scl ? (scl.Length, MetadataLayer.QuickSqlAttribute, $"[QuickColumn(Length={scl.Length})]")
            : stringLength is not null ? (stringLength.MaximumLength, MetadataLayer.DataAccessAnnotation, $"[StringLength({stringLength.MaximumLength})]")
            : maxLength is not null ? (maxLength.Length, MetadataLayer.DataAccessAnnotation, $"[MaxLength({maxLength.Length})]")
            : (0, MetadataLayer.ProviderFallback, "未声明，交给 provider 默认");

        var nullable = ResolveNullable(member, required, quickColumn, fluentMember);

        var isKey = fluentMember?.IsKey == true
                    || quickColumn?.IsPrimary == TriState.Yes
                    || property.GetCustomAttribute<Da.KeyAttribute>() is not null;

        var identity = fluentMember?.IsIdentity
                       ?? quickColumn?.IsIdentity switch
                       {
                           TriState.Yes => true,
                           TriState.No => false,
                           _ => null,
                       };

        int? precision = fluentMember?.Precision ?? (quickColumn is { Precision: > 0 } p ? p.Precision : null);
        int? scale = fluentMember?.Scale ?? (quickColumn is { Scale: > 0 } s ? s.Scale : null);

        var storeType = fluentMember?.StoreTypeName ?? dataColumn?.TypeName;

        // 备注的两档注解：组件模型的 [Description]（System.ComponentModel）是自研 [Column(Comment=…)]
        // 退场后的接棒者，座次照旧 —— 排在 Fluent 之后；DataAnnotations 的 [Display]（描述/显示名）
        // 接在它后面作兜底（这条链从前只到 Display，[Description] 与它们同层竞争）。
        var description = property.GetCustomAttribute<Sc.DescriptionAttribute>()?.Description;
        var display = property.GetCustomAttribute<Da.DisplayAttribute>();
        var comment = fluentMember?.Comment ?? description ?? display?.Description ?? display?.Name;

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
            // 只有"约定"这一档且读回来是没表过态（Unknown）时，这句话不算声明 —— 见 ColumnFacts 那一格。
            NullabilityDeclared = nullable.Declared,
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

    static (bool Value, MetadataLayer Layer, string Note, bool Declared) ResolveNullable(
        MemberFacts member, Da.RequiredAttribute? required, QuickColumnAttribute? quickColumn, FluentMember? fluentMember)
    {
        if (fluentMember?.Required is { } fr)
            return (!fr, MetadataLayer.Fluent, $"Fluent IsRequired({fr})", true);

        if (quickColumn?.IsPrimary == TriState.Yes)
            return (false, MetadataLayer.QuickSqlAttribute, "[QuickColumn(IsPrimary=Yes)] ⇒ 非空", true);

        if (quickColumn?.IsNullable is TriState.Yes or TriState.No)
        {
            var isNullable = quickColumn.IsNullable == TriState.Yes;

            return (isNullable, MetadataLayer.QuickSqlAttribute, isNullable
                ? required is not null
                    ? "WARN: [Required] 与 [QuickColumn(IsNullable=Yes)] 矛盾，按优先级取自研属性"
                    : "[QuickColumn(IsNullable=Yes)]"
                : "[QuickColumn(IsNullable=No)]", true);
        }

        if (required is not null)
            return (false, MetadataLayer.DataAccessAnnotation, "[Required]", true);

        var type = member.Property.PropertyType;

        if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            return (false, MetadataLayer.Convention, "值类型非 Nullable ⇒ 非空列", true);

        // Unknown = 这段代码根本不在可空上下文里（`#nullable disable`、没带可空元数据的引用程序集 ——
        // 整份 ABP/DDD 框架代码天天在这一格）。它说的是"我没表过态"，不是"我说了非空"，所以列的形状
        // 照旧按非空建（改那一格等于替现库批量 DROP NOT NULL），而 §10.6 的必填尺子不量它。
        // 编译期那侧的同一格是 NullableAnnotation.None，两侧同形由 §13.11 的对拍钉住。
        if (member.Nullability == NullabilityState.Unknown)
            return (false, MetadataLayer.Convention, "C# 没表过态（可空上下文之外）⇒ 建表按非空列，不落必填尺子", false);

        return (member.Nullability == NullabilityState.Nullable, MetadataLayer.Convention,
            member.Nullability == NullabilityState.Nullable ? "C# 可空 ⇒ 可空列" : "C# 非空 ⇒ 非空列", true);
    }

    /// <summary>只把声明抄成 <see cref="IndexFacts"/>：名字/量纲的换算、同名冲突判定都在
    /// <see cref="EntityContractFactory"/>，与 G1 生成的那条共用一份。<b>顺序即优先级</b>（§10.2），
    /// 所以 Fluent 必须排在注解后面。</summary>
    [RequiresUnreferencedCode("读取 [Index] 注解。")]
    IReadOnlyList<IndexFacts> ResolveIndexFacts(Type clrType, FluentEntity? fluent)
    {
        var facts = new List<IndexFacts>();

        foreach (var index in clrType.GetCustomAttributes<IndexAttribute>())
            facts.Add(new IndexFacts(null, [.. index.MemberNames], index.IsUnique, index.Clustered, index.Descending));

        foreach (var index in fluent?.Indexes ?? [])
            facts.Add(new IndexFacts(index.Name, index.MemberNames, index.Unique, Descending: index.Descending));

        return [.. facts];
    }

    sealed class MemberFacts(PropertyInfo property)
    {
        public PropertyInfo Property { get; } = property;

        public QuickColumnAttribute? QuickColumn { get; init; }

        public DaSchema.ColumnAttribute? DataColumn { get; init; }

        public string? Ignored { get; set; }

        /// <summary>反射侧读的是 <see cref="NullabilityInfoContext"/> 的 WriteState，三态原样留着：
        /// <c>Unknown</c>（可空上下文之外，没表过态）与 <c>NotNull</c>（表过态说非空）在必填尺子上是两个答案。
        /// 缺省取 <see cref="NullabilityState.NotNull"/> —— 走不到这一格的成员本来就不是列。</summary>
        public NullabilityState Nullability { get; set; } = NullabilityState.NotNull;

        /// <summary>非 null = 这一格是引用导航，值为主端实体的 CLR 类型。它与 <see cref="Ignored"/> 同时存在
        /// 是故意的：「不是列」与「是一条关系」是两个问题，各自有各自的答案。</summary>
        public Type? Reference { get; set; }

        /// <summary>非 null = 这一格是<em>集合</em>导航，值为元素型（子端 CLR 型）。与 <see cref="Reference"/>
        /// 互斥：一根导航的型要么是引用要么是集合，判定链上先问引用。</summary>
        public Type? Collection { get; set; }

        /// <summary>只有 <c>[ForeignKey]</c> 给过名字才非 null；约定那一档在 <c>ResolveRelationships</c> 里判。</summary>
        public string[]? ForeignKeyMembers { get; set; }
    }
}
