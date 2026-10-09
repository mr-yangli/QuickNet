using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace QuickSql.Generators;

/// <summary>§10.2 解析链的编译期实现。<b>逐条对着 <c>ContractResolver</c> 写</b>：层优先级、每层的 note
/// 文本、成员被跳过的条件都要一致——两侧一致性由 §13.11 的逐字段相等断言兜住，所以这里改一句话术，
/// 那一层就会红，正是想要的接线。
/// <b>唯一的刻意例外</b>是 <see cref="Build"/> 的 <c>xmlDocComments</c> 那一档：源码注释只有编译器看得见，
/// 反射侧没有这份来源，所以它一旦开着，两侧对同一张表就能给出不同备注。默认关正是为了让"开它"是一次签字
/// 而不是一次顺带。</summary>
sealed class BuildOutcome
{
    public EntityModel? Model;

    public List<Diagnostic> Diagnostics = [];
}

static class EntityFactsBuilder
{
    const string QuickSqlTable = "QuickSql.Metadata.TableAttribute";
    const string QuickSqlColumn = "QuickSql.Metadata.ColumnAttribute";
    const string QuickSqlIndex = "QuickSql.Metadata.IndexAttribute";
    const string QuickSqlSplit = "QuickSql.Metadata.SplitTableAttribute";
    const string QuickSqlDataSource = "QuickSql.Metadata.DataSourceAttribute";
    const string QuickSqlConcurrency = "QuickSql.Metadata.ConcurrencyTokenAttribute";
    const string DataSchemaTable = "System.ComponentModel.DataAnnotations.Schema.TableAttribute";
    const string DataSchemaColumn = "System.ComponentModel.DataAnnotations.Schema.ColumnAttribute";
    const string DataNotMapped = "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute";
    const string DataForeignKey = "System.ComponentModel.DataAnnotations.Schema.ForeignKeyAttribute";
    const string QuickSqlNoCascade = "QuickSql.Metadata.NoCascadeAttribute";
    const string IEnumerable = "System.Collections.IEnumerable";
    const string DataRequired = "System.ComponentModel.DataAnnotations.RequiredAttribute";
    const string DataStringLength = "System.ComponentModel.DataAnnotations.StringLengthAttribute";
    const string DataMaxLength = "System.ComponentModel.DataAnnotations.MaxLengthAttribute";
    const string DataDisplay = "System.ComponentModel.DataAnnotations.DisplayAttribute";
    const string DataKey = "System.ComponentModel.DataAnnotations.KeyAttribute";

    static readonly SymbolDisplayFormat Qualified = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    static readonly HashSet<string> MappedByName =
    [
        "System.Decimal", "System.Guid", "System.DateTime", "System.DateTimeOffset",
        "System.DateOnly", "System.TimeOnly", "System.TimeSpan",
    ];

    /// <summary><paramref name="xmlDocComments"/> 是<b>整条链上唯一一颗 G1 独有的来源</b>：源码里的
    /// <c>/// &lt;summary&gt;</c> 只有编译器看得见，反射侧没有这一份。开着它，同一张表在两条启动路径上就能给出
    /// 两个备注（§13.11 的 <c>comment=</c> 那一格会红），所以它默认关、且只在消费方点名后才生效——
    /// "开它"要是一次签字，不是一次顺带。</summary>
    public static BuildOutcome Build(GeneratorAttributeSyntaxContext context, bool viaQuickSqlTable, bool xmlDocComments)
    {
        var outcome = new BuildOutcome();
        var entity = (INamedTypeSymbol)context.TargetSymbol;
        var at = context.TargetNode.GetLocation();
        var attributes = entity.GetAttributes();

        // 两个 [Table] 同时存在时只由自研属性那一层产出（§10.2 的优先级），否则同一个实体会生成两份。
        if (!viaQuickSqlTable && Find(attributes, QuickSqlTable) is not null)
            return outcome;

        if (entity.IsGenericType || entity.IsUnboundGenericType)
            return Blocked(outcome, at, "泛型实体");

        if (entity.BaseType is { SpecialType: not SpecialType.System_Object })
            return Blocked(outcome, at, "有基类：继承来的属性列序与反射侧的 MetadataToken 序对不齐");

        if (!entity.InstanceConstructors.Any(static c => c.Parameters.IsEmpty && c.DeclaredAccessibility == Accessibility.Public))
            return Blocked(outcome, at, "没有公共无参构造器，无法物化");

        var table = Find(attributes, QuickSqlTable);
        var dataTable = Find(attributes, DataSchemaTable);

        // 同层冲突在 G2 是启动期抛，在 G1 就是编译错（§10.2「不自动取更严的那条」）。
        if (table is not null && dataTable is not null && Text(table, 0, "Name") != Text(dataTable, 0, "Name"))
        {
            outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.MetadataConflict, at,
                $"类型 '{entity.Name}' 的同一层元数据来源互斥：[Table(\"{Text(table, 0, "Name")}\")] vs [Table(\"{Text(dataTable, 0, "Name")}\")]。QuickSql 不自动取更严的那条。"));

            return outcome;
        }

        var model = new EntityModel
        {
            Namespace = entity.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
            Entity = TypeExpression(entity),
            Name = GeneratedName(entity),
            TableName = Text(table, 0, "Name") ?? Text(dataTable, 0, "Name") ?? entity.Name,
            TableSchema = Text(table, -1, "Schema") ?? Text(dataTable, -1, "Schema"),
            // 表备注只有自研那一层有（DataAnnotations 的 [Table] 没这一格），所以这里不比对 dataTable。
            // 文档注释排在最末：注解写过就轮不到它（与下面列备注同一条取向）。
            TableComment = Text(table, -1, "Comment") ?? (xmlDocComments ? SummaryOf(entity) : null),
            Split = SplitOf(attributes),
            Source = SourceOf(attributes),
        };

        var conflicts = false;
        var navigations = new List<IPropertySymbol>();

        foreach (var property in entity.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.DeclaredAccessibility != Accessibility.Public || property.IsStatic || property.IsIndexer)
                continue;

            // 显式排除先判：一颗 [NotMapped] 打在导航属性上就只是"不映射",不是一条关系
            // （反射侧 ReadMembers 的 if-else 链同一次序，两侧必须同一条链）。
            if (IsExplicitlyIgnored(property))
                continue;

            if (!IsColumnShape(property.Type))
            {
                // 两格导航都收，收在<em>同一份列表</em>里按声明序：反射侧那一格是按 MetadataToken 序
                // 一次走完的，而 §13.11 的对拍比的是逐行相等（顺序也算内容），分成两份列表去产出会把
                // "先声明集合再声明引用"那一张实体判成分叉。
                if (IsReferenceNavigation(property.Type))
                    navigations.Add(property);
                else if (property.GetMethod is not null && CollectionElementOf(property.Type) is not null)
                    navigations.Add(property);

                continue;
            }

            var facts = ColumnOf(property, model, outcome, at, ref conflicts, xmlDocComments);

            if (facts is not null)
                model.Columns.Add(facts);
        }

        // 关系要等列收齐（约定那一档问的是"这张表上有没有 XxxId 那一列",而导航属性常写在前面）。
        RelationshipsOf(navigations, entity, model, outcome, at, conflicts);

        IndexesOf(entity, model, outcome, at, conflicts);

        if (conflicts)
            // 报错时不产出模型，但诊断必须留着——丢掉它等于「编译不过却没人说为什么」。
            return outcome;

        if (model.Columns.Count == 0)
            return Blocked(outcome, at, "没有可映射的公共属性");

        outcome.Model = model;

        return outcome;
    }

    /// <summary>调用点已经判过"显式排除"与"这一格是不是列的形状"，所以这里只做 §10.2 的逐层取值。</summary>
    static ColumnModel? ColumnOf(
        IPropertySymbol property, EntityModel model, BuildOutcome outcome, Location entityLocation, ref bool conflicts, bool xmlDocComments)
    {
        var declared = property.GetAttributes();
        var column = Find(declared, QuickSqlColumn);
        var dataColumn = Find(declared, DataSchemaColumn);
        var type = property.Type;

        var required = Find(declared, DataRequired);
        var stringLength = Find(declared, DataStringLength);
        var maxLength = Find(declared, DataMaxLength);

        // 同层冲突：StringLength 与 MaxLength 都在 DataAnnotations 层。取严格者会把
        // 「建表按 20、校验按 50」藏三个月，所以 G1 侧同样拒绝——只是拒绝发生在编译期。
        if (stringLength is not null && maxLength is not null)
        {
            outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.MetadataConflict, At(property, entityLocation),
                $"属性 '{property.Name}' 的同一层元数据来源互斥：[StringLength({Number(stringLength, 0, "MaximumLength") ?? 0})] vs [MaxLength({Number(maxLength, 0, "Length") ?? 0})]。QuickSql 不自动取更严的那条。"));

            conflicts = true;

            return null;
        }

        var writable = property.SetMethod is { DeclaredAccessibility: Accessibility.Public } && !IsInitOnly(property);

        if (property.GetMethod is not { DeclaredAccessibility: Accessibility.Public } || !writable)
        {
            outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.NotGenerated, entityLocation,
                $"实体 {model.Name} 的属性 {property.Name} 不是公共可读写的（init 或受限 set），G1 不生成该实体，退回 G2（§12.7）。"));

            conflicts = true;

            return null;
        }

        var name = property.Name;
        var declaredLength = Number(column, -1, "Length") ?? 0;

        var (columnName, columnNameLayer, columnNameNote) =
            Text(column, 0, "Name") is { } quicksqlName ? (quicksqlName, "QuickSqlAttribute", $"[Column(\"{quicksqlName}\")]")
            : Text(dataColumn, 0, "Name") is { } dataName ? (dataName, "DataAccessAnnotation", $"[Column(\"{dataName}\")]")
            : (name, "Convention", "约定：属性名");

        var (lengthValue, lengthNote) =
            declaredLength > 0 ? (declaredLength, $"[Column(Length={declaredLength})]")
            : stringLength is not null ? (Number(stringLength, 0, "MaximumLength") ?? 0, $"[StringLength({Number(stringLength, 0, "MaximumLength") ?? 0})]")
            : maxLength is not null ? (Number(maxLength, 0, "Length") ?? 0, $"[MaxLength({Number(maxLength, 0, "Length") ?? 0})]")
            : (0, "未声明，交给 provider 默认");

        var (isNullable, nullableNote) = NullableOf(type, property, column, required);

        // 没打标记就不写来源：G2 侧那一格同样是 null，而 §13.11 的对拍比的是整个 ColumnFacts——
        // 这里多写一句常量就是「两侧各自都合理、但文本不一样」。
        var concurrency = Find(declared, QuickSqlConcurrency) is not null;

        return new ColumnModel
        {
            Member = name,
            ClrType = TypeExpression(type),
            ColumnName = columnName,
            ColumnNameLayer = columnNameLayer,
            ColumnNameNote = columnNameNote,
            MaxChars = lengthValue == 0 ? null : lengthValue,
            LengthNote = lengthNote,
            IsNullable = isNullable,
            NullableNote = nullableNote,
            IsPrimary = Tri(column, "IsPrimary") == 1 || Find(declared, DataKey) is not null,
            DeclaredIdentity = Tri(column, "IsIdentity") switch { 1 => true, 2 => false, _ => null },
            IsConcurrencyToken = concurrency,
            ConcurrencyNote = concurrency ? "[ConcurrencyToken]" : null,
            Precision = Positive(Number(column, -1, "Precision")),
            Scale = Positive(Number(column, -1, "Scale")),
            StoreType = Text(column, -1, "DbTypes") ?? Text(dataColumn, -1, "TypeName"),
            // 备注的链与反射侧同一条（自研 [Column] > [Display(Description)] > [Display(Name)]，Fluent 那档
            // 生成期整体上看不见），文档注释接在链尾兜底：写过显式备注就不轮到它。
            Comment = Text(column, -1, "Comment") ?? Text(Find(declared, DataDisplay), -1, "Description") ?? Text(Find(declared, DataDisplay), -1, "Name")
                      ?? (xmlDocComments ? SummaryOf(property) : null),
            IsString = Core(type).SpecialType == SpecialType.System_String,
            DirectArm = DirectArmOf(type),
            CanBeNull = !(type.IsValueType && !IsNullableValue(type)),
        };
    }

    /// <summary>这一列有没有直读臂（§16.21）。<b>这里只出方法名，不出编号</b>：
    /// 编号是运行期按驱动交回的<em>实际</em>类型落的（<c>ReadOps.Probe</c>），名单与臂本体在
    /// <c>QuickSql.Core/Materialization/ReadOps.cs</c> 那一个文件里，两侧唯一的连接就是这个名字 ——
    /// 名字写错是编译错误，挑错臂是编号对不上、静默退回装箱口，所以能错的方向只有"没变快"。
    /// 枚举不在名单里（成员型是 <c>State</c> 而驱动回的是底层整数，探测永远不落编号）；
    /// <c>byte[]</c>、<c>DateOnly</c>、<c>TimeOnly</c>、<c>DateTimeOffset</c>、<c>TimeSpan</c>、<c>sbyte</c>、无符号整族也不在：
    /// <c>DbDataReader</c> 对这几首没有"一次调用一个值"的原始访问器（<c>GetTimeSpan</c> 根本不存在，那是构建期撞出来的）。</summary>
    static string? DirectArmOf(ITypeSymbol type)
    {
        var arm = FullName(Core(type)) switch
        {
            "System.Boolean" => "Boolean",
            "System.Byte" => "Byte",
            "System.Int16" => "Int16",
            "System.Int32" => "Int32",
            "System.Int64" => "Int64",
            "System.Single" => "Single",
            "System.Double" => "Double",
            "System.Decimal" => "Decimal",
            "System.Char" => "Char",
            "System.DateTime" => "DateTime",
            "System.String" => "String",
            "System.Guid" => "Guid",
            _ => null,
        };

        if (arm is null)
            return null;

        // 可空<em>值</em>类型换一首臂：非空臂的返回类型是底层型，NULL 那一格由门给 default(T)，
        // 交给 DateTime? 成员就是静默摊平（0001-01-01 冒充"没有值"）。可空那一族的非直读分支是
        // Read<T?>，所以 NULL 仍是 null。字符串不换 —— string? 与 string 是同一个运行期类型，
        // 非空臂交回的 null 本来就是门给的答案。
        return IsNullableValue(type) ? "ReadNullable" + arm : "Read" + arm;
    }

    /// <summary>可空性的五档判定，顺序与 <c>ContractResolver.ResolveNullable</c> 一字不差
    /// （第一档 Fluent 在编译期不存在，所以从自研属性起）。</summary>
    static (bool, string) NullableOf(ITypeSymbol type, IPropertySymbol property, AttributeData? column, AttributeData? required)
    {
        if (Tri(column, "IsPrimary") == 1)
            return (false, "[Column(IsPrimary=Yes)] ⇒ 非空");

        if (Tri(column, "IsNullable") is 1 or 2)
        {
            var nullable = Tri(column, "IsNullable") == 1;

            return (nullable, nullable
                ? required is not null
                    ? "WARN: [Required] 与 [Column(IsNullable=Yes)] 矛盾，按优先级取自研属性"
                    : "[Column(IsNullable=Yes)]"
                : "[Column(IsNullable=No)]");
        }

        if (required is not null)
            return (false, "[Required]");

        if (type.IsValueType && !IsNullableValue(type))
            return (false, "值类型非 Nullable ⇒ 非空列");

        // 反射侧读的是 NullabilityInfoContext 的 WriteState；可空值类型那一档它给的就是 Nullable。
        return (IsNullableValue(type) || property.NullableAnnotation == NullableAnnotation.Annotated,
            IsNullableValue(type) || property.NullableAnnotation == NullableAnnotation.Annotated ? "C# 可空 ⇒ 可空列" : "C# 非空 ⇒ 非空列");
    }

    /// <summary>「被显式排除」= 自研 <c>[Column(IsIgnore=true)]</c> 或 <c>[NotMapped]</c>。
    /// 反射侧同一句判定，且它在链条上的位置也一样：排在类型判定<em>之前</em>，所以打在导航属性上的
    /// 那颗 <c>[NotMapped]</c> 只是"不映射",不会被下面那条当成关系。</summary>
    static bool IsExplicitlyIgnored(IPropertySymbol property)
    {
        var declared = property.GetAttributes();

        return Bool(Find(declared, QuickSqlColumn), "IsIgnore") || Find(declared, DataNotMapped) is not null;
    }

    /// <summary>能进列清单的形状：<c>byte[]</c> 单独放行，其余要落在可映射闭包里。反射侧同一句。</summary>
    static bool IsColumnShape(ITypeSymbol type)
        => type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } || IsMappable(type);

    /// <summary>引用导航 = 类类型、不是 <c>string</c>、不是集合形态（<c>byte[]</c> 与 <c>List&lt;T&gt;</c> 都在这条上
    /// 落到"不可映射而跳过"）。判定不要求目标是实体：<c>Uri</c> 配 <c>UriId</c> 也会成一条边，
    /// 而这条边唯一的用法（写序）对没有跟踪档的类型天然无效。反射侧 <c>IsReferenceNavigation</c> 同一条。</summary>
    static bool IsReferenceNavigation(ITypeSymbol type)
    {
        var core = Core(type);

        return core.TypeKind == TypeKind.Class
               && core.SpecialType != SpecialType.System_String
               && !ImplementsEnumerable(core);
    }

    /// <summary><c>IEnumerable</c>（非泛型那一颗）是所有集合形态的公共祖先，与反射侧
    /// <c>typeof(IEnumerable).IsAssignableFrom</c> 同一问句。自算闭包而不调 <c>Compilation.GetSpecialType</c>：
    /// 生成器手里没有 <see cref="Compilation"/>，而多引一份上下文只为这一问不值。</summary>
    static bool ImplementsEnumerable(ITypeSymbol type)
    {
        if (FullName(type) == IEnumerable)
            return true;

        // AllInterfaces 已经含继承来的那些，所以不必自己走基类链。
        foreach (var iface in type.AllInterfaces)
        {
            if (FullName(iface) == IEnumerable)
                return true;
        }

        return false;
    }

    static void RelationshipsOf(List<IPropertySymbol> navigations, INamedTypeSymbol entity, EntityModel model, BuildOutcome outcome, Location at, bool alreadyFailed)
    {
        if (alreadyFailed)
            return;

        foreach (var property in navigations)
        {
            var declared = ForeignKeyOf(property);

            // 集合那一格：外键成员在<em>子端</em>那张表上，这里既拿本实体的列清单换算不了，也不该换算
            // （换算要另一份契约，而注册表是按需解析的，子端契约此刻未必存在）。声明只带着成员名走，
            // 真到了级联要用那一刻才解，解不出就响。约定那一档同理不在编译期判。
            if (CollectionElementOf(property.Type) is { } child)
            {
                model.Relationships.Add(new RelationshipModel
                {
                    Member = property.Name,
                    TargetType = TypeExpression(child),
                    Members = [.. declared ?? []],
                    Layer = declared is null ? "Convention" : "DataAccessAnnotation",
                    Note = declared is null ? $"集合导航：{property.Name} ⇒ {child.Name}" : $"[ForeignKey(\"{string.Join(",", declared)}\")]",
                    IsCollection = true,
                    Cascade = !IsNoCascade(property, entity),
                });

                continue;
            }

            // 显式声明：写了却没那一列 = 配置错，编译期就得说（反射侧是启动期抛同一句话）。
            if (declared is not null)
            {
                foreach (var member in declared)
                {
                    if (!model.Columns.Any(c => c.Member == member))
                    {
                        outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.ForeignKeyMemberUnmapped, At(property, at),
                            $"引用导航 {property.Name} 引用了未映射的属性 {member}（可能被 [NotMapped] 或 [Column(IsIgnore)] 排除）"));

                        return;
                    }
                }

                model.Relationships.Add(new RelationshipModel
                {
                    Member = property.Name,
                    TargetType = TypeExpression(Core(property.Type)),
                    Members = [.. declared],
                    Layer = "DataAccessAnnotation",
                    Note = $"[ForeignKey(\"{string.Join(",", declared)}\")]",
                });

                continue;
            }

            // 约定：导航属性名 + "Id"。命不中就没有这条关系（不是错误 —— 那正是 v0.19 之前两侧共同的行为）。
            var convention = property.Name + "Id";

            if (!model.Columns.Any(c => c.Member == convention))
                continue;

            model.Relationships.Add(new RelationshipModel
            {
                Member = property.Name,
                TargetType = TypeExpression(Core(property.Type)),
                Members = [convention],
                Layer = "Convention",
                Note = $"约定：{property.Name} ⇒ {convention}",
            });
        }
    }

    /// <summary>集合导航的元素型：收下 <see cref="IEnumerable"/>（非泛型那一颗）又不是 <c>string</c>
    /// 的形状，取它 <c>IEnumerable&lt;T&gt;</c> 的那个 <c>T</c>，<b>只有类形态的才算子行</b>
    /// （<c>List&lt;string&gt;</c> 是标签、<c>Dictionary&lt;K,V&gt;</c> 的元素是 <c>KeyValuePair</c> 结构体，
    /// 给它们出一条边等于让 <c>Add</c> 在根本没有子行的地方找外键）。反射侧 <c>CollectionElementOf</c> 同一条。</summary>
    static ITypeSymbol? CollectionElementOf(ITypeSymbol type)
    {
        var core = Core(type);

        if (core.SpecialType == SpecialType.System_String || !ImplementsEnumerable(core))
            return null;

        foreach (var iface in core.AllInterfaces)
        {
            if (iface is { IsGenericType: true, OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T })
            {
                var element = iface.TypeArguments[0];

                return element.TypeKind == TypeKind.Class && element.SpecialType != SpecialType.System_String ? element : null;
            }
        }

        return null;
    }

    /// <summary><c>[NoCascade]</c> 的两档：打在实体上这一张表的导航都不带人，打在那根集合导航上只有它不带。
    /// 级联缺省为开，所以这里只判退出（反射侧 <c>IsNoCascade</c> 同一条）。</summary>
    static bool IsNoCascade(IPropertySymbol property, INamedTypeSymbol entity)
        => Find(property.GetAttributes(), QuickSqlNoCascade) is not null
           || Find(entity.GetAttributes(), QuickSqlNoCascade) is not null;

    static string[]? ForeignKeyOf(IPropertySymbol property)
        => Find(property.GetAttributes(), DataForeignKey) is { } declared ? Split(Text(declared, 0, "Name")) : null;

    /// <summary>逗号分隔 = 复合外键。空串按"没声明"处理（反射侧同一个答案，于是两侧都会退回约定那一档）。
    /// netstandard2.0 没有 <c>StringSplitOptions.TrimEntries</c>，所以逐条 <c>Trim()</c> —— 结果与反射侧那份一致。</summary>
    static string[]? Split(string? names)
    {
        if (names is null || names.Length == 0)
            return null;

        var members = names.Split(',').Select(static name => name.Trim()).Where(static name => name.Length > 0).ToArray();

        return members.Length > 0 ? members : null;
    }

    static void IndexesOf(INamedTypeSymbol entity, EntityModel model, BuildOutcome outcome, Location at, bool alreadyFailed)
    {
        if (alreadyFailed)
            return;

        foreach (var index in entity.GetAttributes().Where(a => Name(a) == QuickSqlIndex))
        {
            var members = Texts(index, 1);
            var indexName = Text(index, 0, "Name");

            foreach (var member in members)
            {
                if (!model.Columns.Any(c => c.Member == member))
                {
                    // 反射侧是启动期抛 QuickSqlException；编译期同样只能是错，不能默默少一个索引。
                    outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.IndexMemberUnmapped, at,
                        $"索引 {indexName ?? "(按约定命名)"} 引用了未映射的属性 {member}（可能被 [NotMapped] 或 [Column(IsIgnore)] 排除）"));

                    return;
                }
            }

            model.Indexes.Add(new IndexModel
            {
                Name = indexName,
                Members = [.. members],
                Unique = Bool(index, "Unique"),
                Clustered = Bool(index, "Clustered"),
                Descending = Bool(index, "Descending"),
            });
        }
    }

    /// <summary>Roslyn 4.14 的符号层面查不到 <c>init</c>，只能回到语法树。之所以要单独问一句：
    /// init-only 属性写不了，G1 的 <c>SetValue</c> 就无从生成，与其生成一个半残的 schema，不如整条实体退回 G2。</summary>
    static bool IsInitOnly(IPropertySymbol property)
        => property.DeclaringSyntaxReferences.Any(static reference
            => reference.GetSyntax(default) is PropertyDeclarationSyntax { AccessorList: { } accessors }
               && accessors.Accessors.Any(static accessor => accessor.IsKind(SyntaxKind.InitAccessorDeclaration)));

    /// <summary>属性自己的位置更好定位；合成成员没有位置时退回实体。</summary>
    static Location At(ISymbol symbol, Location fallback) => symbol.Locations.FirstOrDefault() ?? fallback;

    /// <summary>取那一格 <c>&lt;summary&gt;</c> 作备注（只在开关开着时被调用）。
    /// <b>自己扫前导 trivia，不走 <see cref="ISymbol.GetDocumentationCommentXml"/></b>：那颗 API 的源码侧文档
    /// 要由 <c>DocumentationProvider</c> 供给，而 <c>csc</c> 批编译压根没装它——实测同一段
    /// <c>/// &lt;summary&gt;</c> 在内存编译里有值、在真实 MSBuild 编译里交回<em>空串</em>（2026-10-08 端到端）。
    /// 空串不是"没写注释"而是"读不到"，而"注释在、备注为空、构建还全绿"正是这一档最不能有的失效形状。
    /// <b>筛子是 kind 与前缀两把并起来，单用一把都缺一档</b>：<c>DocumentationMode</c> 由编译器那一侧决定
    /// （<c>csc</c> 不带 <c>/doc</c> 时实测为 <c>None</c>，IDE 与内存量具为 <c>Parse</c>），同一行 <c>///</c> 在
    /// 前者是普通 <c>SingleLineCommentTrivia</c>、原文带着 <c>///</c>，在后者是
    /// <c>SingleLineDocumentationCommentTrivia</c>、而 <c>ToString()</c> 交回的是<em>结构</em>文本（<c>///</c>
    /// 已经不在里面了）。实测（2026-10-08）：只按 kind 筛 → <c>None</c> 档读不到（真实构建就是这一档，
    /// 量具全绿而字段为空）；只按前缀筛 → <c>Parse</c> 档读不到。两把并起来，G1 在 IDE 与构建里才算得出
    /// 同一份元数据，否则编辑期看到的备注和落库的那句不是一句话。同样不要求
    /// <c>GenerateDocumentationFile</c>（那颗只管把注释落成 <c>.xml</c> 边车，是给运行期反射读的，
    /// 生成期用不着它）。
    /// <b>只截 <c>&lt;summary&gt;…&lt;/summary&gt;</c> 那一段来解析</b>，不把整块注释拼成一棵树：一行
    /// <c>//// 分节线</c> 里出现裸 <c>&lt;</c> 时，整棵树的解析会连旁边写得好好的 summary 一起判死；截段解析只让
    /// 那一段自己承担畸形——而<b>畸形交回 null 而不是抛</b>：作者写了裸 <c>&amp;</c> 时编译器已经有 CS1570 那句，
    /// 生成器再说一遍只是把一条警告升成一次构建失败。</summary>
    static string? SummaryOf(ISymbol symbol)
    {
        var node = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(default);

        if (node is null)
            return null;

        var source = new StringBuilder();

        foreach (var trivia in node.GetLeadingTrivia())
        {
            var text = trivia.ToString();

            // 相邻两行 /// 是两条 trivia，中间那个换行不属于任何一条；这里补一个分隔，否则
            // 「上一句」与「下一句」会粘成「上一句下一句」。补空格与补换行等价——下面的 Fold 都把
            // 一段空白折成一个空格，而落库的备注本来就要折成一行。
            if (text.StartsWith("///", StringComparison.Ordinal) || text.StartsWith("/**", StringComparison.Ordinal)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                source.Append(text).Append(' ');
        }

        // 注释外框（/// 与 /* */ 那一圈、以及多行注释里的 HTML 包壳）不是 XML，摘掉才谈得上解析。
        var body = source.ToString().Replace("///", "").Replace("<!--", "").Replace("-->", "")
                         .Replace("/**", "").Replace("*/", "");

        var from = body.IndexOf("<summary>", StringComparison.Ordinal);

        if (from < 0)
            return null;

        var to = body.IndexOf("</summary>", from, StringComparison.Ordinal);

        if (to < 0)
            return null;

        try
        {
            // 截的那一段必须连着关标签：只取到 </summary> 之前就是 XDocument 眼里的"标签没合上"，
            // 一句写得好好的备注会被 XmlException 判成畸形。区间运算符在这里也用不了——本工程吐的是
            // netstandard2.0 的生成器程序集，那套引用里没有 System.Index / System.Range（CS0518，不是缺 using）。
            var fragment = body.Substring(from, to - from + "</summary>".Length);

            return Fold(XDocument.Parse(fragment).Root!.Value);
        }
        catch (XmlException)
        {
            return null;
        }
    }
    /// <summary>折成一行并去首尾空：注释按 C# 惯例是换行缩进写的，而备注要落进一条 <c>COMMENT ON</c> 的字符串
    /// 字面量里——换行留在库里，读回比对（§9.5 的那把尺子读的是目录里的备注文本）就永远差那一格。
    /// 全空的 <c>&lt;summary&gt; &lt;/summary&gt;</c> 与只有 <c>&lt;inheritdoc/&gt;</c> 而没有 summary 的那一种
    /// 都交回 null = 没声明，而不是空串（"说过而说的是没有备注"是另一档，见 <c>TableInfo.Comment</c>）。</summary>
    static string? Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pending = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                // 只在已经有内容时才记"欠一个空格"：注释首行紧跟在 <c>&lt;summary&gt;</c> 之后的那段换行不该变成前导空格。
                pending = builder.Length > 0;
                continue;
            }

            if (pending)
            {
                builder.Append(' ');
                pending = false;
            }

            builder.Append(ch);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    static BuildOutcome Blocked(BuildOutcome outcome, Location location, string reason)
    {
        outcome.Diagnostics.Add(Diagnostic.Create(QuickSqlDiagnostics.NotGenerated, location,
            $"该实体不走 G1（{reason}），运行期由 G2 反射解析兜住（§12.7 的三级路径）。"));

        return outcome;
    }

    static string GeneratedName(INamedTypeSymbol entity)
    {
        var chain = new List<string>();

        for (ISymbol? current = entity; current is not null; current = current.ContainingType)
            chain.Insert(0, current.Name);

        return "QuickSqlGenerated" + string.Join("_", chain);
    }

    static string TypeExpression(ITypeSymbol type)
        => type switch
        {
            IArrayTypeSymbol array => TypeExpression(array.ElementType) + "[]",
            INamedTypeSymbol { IsGenericType: true, ConstructedFrom.SpecialType: SpecialType.System_Nullable_T } nullable
                => "global::System.Nullable<" + TypeExpression(nullable.TypeArguments[0]) + ">",
            _ => "global::" + type.ToDisplayString(Qualified),
        };

    static bool IsNullableValue(ITypeSymbol type)
        => type is INamedTypeSymbol { IsGenericType: true, ConstructedFrom.SpecialType: SpecialType.System_Nullable_T };

    static ITypeSymbol Core(ITypeSymbol type) => IsNullableValue(type) ? ((INamedTypeSymbol)type).TypeArguments[0] : type;

    /// <summary>默认格式的 <c>ToDisplayString()</c> 把内建类型写成关键字（<c>decimal</c>），
    /// 那份名单是按全名匹配的，于是 decimal 会被判成导航属性并<b>静默少一列</b>。
    /// 用 Qualified 格式是为了拿到 <c>System.Decimal</c>。</summary>
    static string FullName(ITypeSymbol type) => type.ToDisplayString(Qualified);

    static bool IsMappable(ITypeSymbol type)
    {
        var core = Core(type);

        return core.SpecialType switch
        {
            SpecialType.System_String or SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double
                or SpecialType.System_Char or SpecialType.System_IntPtr or SpecialType.System_UIntPtr => true,
            _ => core.TypeKind == TypeKind.Enum || MappedByName.Contains(FullName(core)),
        };
    }

    static string? Name(AttributeData attribute) => attribute.AttributeClass is { } cls ? FullName(cls) : null;

    /// <summary><c>[SplitTable]</c> 的抄本。档名存的是<em>可直接落进代码的表达式</em>而不是成员名，
    /// 因为写注解的人也可以写 <c>(SplitKind)99</c> —— 那种值不该在生成期被咽掉：让它在运行期撞到
    /// <c>SplitRouting</c> 那句「未知的分片档」，与反射路径同一句话（两侧一致性由此而来，而不是靠这里再校一遍）。</summary>
    static SplitModel? SplitOf(IEnumerable<AttributeData> attributes)
    {
        var split = Find(attributes, QuickSqlSplit);

        if (split is null)
            return null;

        return new SplitModel
        {
            Kind = EnumExpression(split, 0),
            Member = Text(split, 1, "Field") ?? "",
            Buckets = (int)(Number(split, -1, "Buckets") ?? 0),
        };
    }

    /// <summary><c>[DataSource]</c> 的抄本：只抄集群名那一个字符串，<b>没打注解才是 null</b>。
    /// 打了注解却交出空值（<c>[DataSource(null)]</c>）要落成 <c>""</c> 而不是 null：反射侧那种写法交回的是
    /// <c>DataSourceFacts(null)</c>，两侧到 <c>EntityContractFactory</c> 撞的是同一句「名字是空的」（与
    /// <see cref="SplitOf"/> 里 <c>Member ?? ""</c> 同一 handoff）。在这里咽成 null 就成了
    /// 「G1 当没声明而照跑、G2 启动期抛」——那正是两侧不一致里最难查的那一种。</summary>
    static string? SourceOf(IEnumerable<AttributeData> attributes)
    {
        var source = Find(attributes, QuickSqlDataSource);

        return source is null ? null : Text(source, 0, "Cluster") ?? "";
    }

    /// <summary>把枚举实参写成 <c>global::…SplitKind.Month</c>；按数值找不到成员名时退回强制转换。
    /// <b>前缀在这里加、不在写手那里加</b>：三种退路（成员名、<c>(枚举)数值</c>、<c>default</c>）里只有前两种
    /// 该带 <c>global::</c>，把它们各自写成完整形态，写手那一格就只管抄。</summary>
    static string EnumExpression(AttributeData attribute, int position)
    {
        var argument = Argument(attribute, position);

        if (argument?.Value is null)
            return "default";

        // 两次 .Value：第一颗摘掉 <c>TypedConstant?</c> 的可空壳，第二颗才是实参那颗装箱的整数
        // （枚举实参在 Roslyn 里存的就是底层整数值，与 <see cref="Tri"/> 同一件事）。
        var raw = Convert.ToInt64(argument.Value.Value);

        if (argument?.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
        {
            foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
                if (member.HasConstantValue && member.ConstantValue is not null && Convert.ToInt64(member.ConstantValue) == raw)
                    return "global::" + FullName(enumType) + "." + member.Name;

            return "(global::" + FullName(enumType) + ")" + raw;
        }

        return raw.ToString();
    }

    static AttributeData? Find(IEnumerable<AttributeData> attributes, string metadataName)
        => attributes.FirstOrDefault(a => Name(a) == metadataName);

    static TypedConstant? Argument(AttributeData? attribute, int position)
        => attribute is not null && position >= 0 && attribute.ConstructorArguments.Length > position ? attribute.ConstructorArguments[position] : null;

    static TypedConstant? Named(AttributeData? attribute, string name)
        => attribute?.NamedArguments.FirstOrDefault(kv => kv.Key == name).Value;

    /// <summary>先按位置读构造参数，读不到再按名字读命名参数——<c>[Column("x")]</c> 与
    /// <c>[Column(Name = "x")]</c> 两种写法因此走同一条判定，与反射侧读到的属性值一致。</summary>
    static string? Text(AttributeData? attribute, int position, string name)
        => Argument(attribute, position)?.Value as string ?? Named(attribute, name)?.Value as string;

    static int? Number(AttributeData? attribute, int position, string name)
        => (int?)Argument(attribute, position)?.Value ?? (int?)Named(attribute, name)?.Value;

    static bool Bool(AttributeData? attribute, string name) => (bool)(Named(attribute, name)?.Value ?? false);

    /// <summary>自研属性的三态：<c>Unset=0 / Yes=1 / No=2</c>。枚举实参在 <see cref="TypedConstant"/> 里就是底层整数。</summary>
    static int Tri(AttributeData? attribute, string name) => (int)(Named(attribute, name)?.Value ?? 0);

    static List<string> Texts(AttributeData attribute, int position)
        => Argument(attribute, position) is { Kind: TypedConstantKind.Array } array
            ? [.. array.Values.Select(v => (string?)v.Value ?? "")]
            : [];

    static int? Positive(int? value) => value > 0 ? value : null;
}
