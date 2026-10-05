using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastSql.Generators;

/// <summary>§10.2 解析链的编译期实现。<b>逐条对着 <c>ContractResolver</c> 写</b>：层优先级、每层的 note
/// 文本、成员被跳过的条件都要一致——两侧一致性由 §13.11 的逐字段相等断言兜住，所以这里改一句话术，
/// 那一层就会红，正是想要的接线。</summary>
sealed class BuildOutcome
{
    public EntityModel? Model;

    public List<Diagnostic> Diagnostics = [];
}

static class EntityFactsBuilder
{
    const string FastSqlTable = "FastSql.Metadata.TableAttribute";
    const string FastSqlColumn = "FastSql.Metadata.ColumnAttribute";
    const string FastSqlIndex = "FastSql.Metadata.IndexAttribute";
    const string FastSqlSplit = "FastSql.Metadata.SplitTableAttribute";
    const string FastSqlConcurrency = "FastSql.Metadata.ConcurrencyTokenAttribute";
    const string DataSchemaTable = "System.ComponentModel.DataAnnotations.Schema.TableAttribute";
    const string DataSchemaColumn = "System.ComponentModel.DataAnnotations.Schema.ColumnAttribute";
    const string DataNotMapped = "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute";
    const string DataForeignKey = "System.ComponentModel.DataAnnotations.Schema.ForeignKeyAttribute";
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

    public static BuildOutcome Build(GeneratorAttributeSyntaxContext context, bool viaFastSqlTable)
    {
        var outcome = new BuildOutcome();
        var entity = (INamedTypeSymbol)context.TargetSymbol;
        var at = context.TargetNode.GetLocation();
        var attributes = entity.GetAttributes();

        // 两个 [Table] 同时存在时只由自研属性那一层产出（§10.2 的优先级），否则同一个实体会生成两份。
        if (!viaFastSqlTable && Find(attributes, FastSqlTable) is not null)
            return outcome;

        if (entity.IsGenericType || entity.IsUnboundGenericType)
            return Blocked(outcome, at, "泛型实体");

        if (entity.BaseType is { SpecialType: not SpecialType.System_Object })
            return Blocked(outcome, at, "有基类：继承来的属性列序与反射侧的 MetadataToken 序对不齐");

        if (!entity.InstanceConstructors.Any(static c => c.Parameters.IsEmpty && c.DeclaredAccessibility == Accessibility.Public))
            return Blocked(outcome, at, "没有公共无参构造器，无法物化");

        var table = Find(attributes, FastSqlTable);
        var dataTable = Find(attributes, DataSchemaTable);

        // 同层冲突在 G2 是启动期抛，在 G1 就是编译错（§10.2「不自动取更严的那条」）。
        if (table is not null && dataTable is not null && Text(table, 0, "Name") != Text(dataTable, 0, "Name"))
        {
            outcome.Diagnostics.Add(Diagnostic.Create(FastSqlDiagnostics.MetadataConflict, at,
                $"类型 '{entity.Name}' 的同一层元数据来源互斥：[Table(\"{Text(table, 0, "Name")}\")] vs [Table(\"{Text(dataTable, 0, "Name")}\")]。FastSql 不自动取更严的那条。"));

            return outcome;
        }

        var model = new EntityModel
        {
            Namespace = entity.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
            Entity = TypeExpression(entity),
            Name = GeneratedName(entity),
            TableName = Text(table, 0, "Name") ?? Text(dataTable, 0, "Name") ?? entity.Name,
            TableSchema = Text(table, -1, "Schema") ?? Text(dataTable, -1, "Schema"),
            Split = SplitOf(attributes),
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
                if (IsReferenceNavigation(property.Type))
                    navigations.Add(property);

                continue;
            }

            var facts = ColumnOf(property, model, outcome, at, ref conflicts);

            if (facts is not null)
                model.Columns.Add(facts);
        }

        // 关系要等列收齐（约定那一档问的是"这张表上有没有 XxxId 那一列",而导航属性常写在前面）。
        RelationshipsOf(navigations, model, outcome, at, conflicts);

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
    static ColumnModel? ColumnOf(IPropertySymbol property, EntityModel model, BuildOutcome outcome, Location entityLocation, ref bool conflicts)
    {
        var declared = property.GetAttributes();
        var column = Find(declared, FastSqlColumn);
        var dataColumn = Find(declared, DataSchemaColumn);
        var type = property.Type;

        var required = Find(declared, DataRequired);
        var stringLength = Find(declared, DataStringLength);
        var maxLength = Find(declared, DataMaxLength);

        // 同层冲突：StringLength 与 MaxLength 都在 DataAnnotations 层。取严格者会把
        // 「建表按 20、校验按 50」藏三个月，所以 G1 侧同样拒绝——只是拒绝发生在编译期。
        if (stringLength is not null && maxLength is not null)
        {
            outcome.Diagnostics.Add(Diagnostic.Create(FastSqlDiagnostics.MetadataConflict, At(property, entityLocation),
                $"属性 '{property.Name}' 的同一层元数据来源互斥：[StringLength({Number(stringLength, 0, "MaximumLength") ?? 0})] vs [MaxLength({Number(maxLength, 0, "Length") ?? 0})]。FastSql 不自动取更严的那条。"));

            conflicts = true;

            return null;
        }

        var writable = property.SetMethod is { DeclaredAccessibility: Accessibility.Public } && !IsInitOnly(property);

        if (property.GetMethod is not { DeclaredAccessibility: Accessibility.Public } || !writable)
        {
            outcome.Diagnostics.Add(Diagnostic.Create(FastSqlDiagnostics.NotGenerated, entityLocation,
                $"实体 {model.Name} 的属性 {property.Name} 不是公共可读写的（init 或受限 set），G1 不生成该实体，退回 G2（§12.7）。"));

            conflicts = true;

            return null;
        }

        var name = property.Name;
        var declaredLength = Number(column, -1, "Length") ?? 0;

        var (columnName, columnNameLayer, columnNameNote) =
            Text(column, 0, "Name") is { } fastsqlName ? (fastsqlName, "FastSqlAttribute", $"[Column(\"{fastsqlName}\")]")
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
        var concurrency = Find(declared, FastSqlConcurrency) is not null;

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
            Comment = Text(column, -1, "Comment") ?? Text(Find(declared, DataDisplay), -1, "Description") ?? Text(Find(declared, DataDisplay), -1, "Name"),
            IsString = Core(type).SpecialType == SpecialType.System_String,
            DirectArm = DirectArmOf(type),
            CanBeNull = !(type.IsValueType && !IsNullableValue(type)),
        };
    }

    /// <summary>这一列有没有直读臂（§16.21）。<b>这里只出方法名，不出编号</b>：
    /// 编号是运行期按驱动交回的<em>实际</em>类型落的（<c>ReadOps.Probe</c>），名单与臂本体在
    /// <c>FastSql.Core/Materialization/ReadOps.cs</c> 那一个文件里，两侧唯一的连接就是这个名字 ——
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

        return Bool(Find(declared, FastSqlColumn), "IsIgnore") || Find(declared, DataNotMapped) is not null;
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

    static void RelationshipsOf(List<IPropertySymbol> navigations, EntityModel model, BuildOutcome outcome, Location at, bool alreadyFailed)
    {
        if (alreadyFailed)
            return;

        foreach (var property in navigations)
        {
            // 显式声明：写了却没那一列 = 配置错，编译期就得说（反射侧是启动期抛同一句话）。
            if (ForeignKeyOf(property) is { } declared)
            {
                foreach (var member in declared)
                {
                    if (!model.Columns.Any(c => c.Member == member))
                    {
                        outcome.Diagnostics.Add(Diagnostic.Create(FastSqlDiagnostics.ForeignKeyMemberUnmapped, At(property, at),
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

        foreach (var index in entity.GetAttributes().Where(a => Name(a) == FastSqlIndex))
        {
            var members = Texts(index, 1);
            var indexName = Text(index, 0, "Name");

            foreach (var member in members)
            {
                if (!model.Columns.Any(c => c.Member == member))
                {
                    // 反射侧是启动期抛 FastSqlException；编译期同样只能是错，不能默默少一个索引。
                    outcome.Diagnostics.Add(Diagnostic.Create(FastSqlDiagnostics.IndexMemberUnmapped, at,
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

    static BuildOutcome Blocked(BuildOutcome outcome, Location location, string reason)
    {
        outcome.Diagnostics.Add(Diagnostic.Create(FastSqlDiagnostics.NotGenerated, location,
            $"该实体不走 G1（{reason}），运行期由 G2 反射解析兜住（§12.7 的三级路径）。"));

        return outcome;
    }

    static string GeneratedName(INamedTypeSymbol entity)
    {
        var chain = new List<string>();

        for (ISymbol? current = entity; current is not null; current = current.ContainingType)
            chain.Insert(0, current.Name);

        return "FastSqlGenerated" + string.Join("_", chain);
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
        var split = Find(attributes, FastSqlSplit);

        if (split is null)
            return null;

        return new SplitModel
        {
            Kind = EnumExpression(split, 0),
            Member = Text(split, 1, "Field") ?? "",
            Buckets = (int)(Number(split, -1, "Buckets") ?? 0),
        };
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
