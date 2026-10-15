using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;

namespace QuickSql.Schema;

/// <summary>G1 注册表（§12.7 A 的第一级）：实体事实由源生成器在编译期算好，这里只做两件事——
/// 让 <see cref="EntityContractFactory"/> 补上 provider 那一半，以及把生成的 schema/validator 缓存住
/// （缓存用 <see cref="TypeTable{TValue}"/>：命中路径一次易失读加一次冻结表查找，没有锁，§12.7 B）。
/// 命中时全程零反射、零表达式树、零 <c>MakeGenericType</c>。</summary>
/// <remarks>未命中的实体（没标注 <c>[Table]</c>、有基类、<b>或被 Fluent 配置过</b>）逐条退回
/// <see cref="EntitySchemaRegistry"/>，所以这是「按实体混合」而不是「整库二选一」。Fluent 那条退回是硬要求：
/// §10.2 里 Fluent 压在注解之上，而生成器看不到运行期的 <see cref="ModelConfiguration"/>，
/// 让它接管就会把高优先层的配置静默吃掉。
/// 也正因为这个兜底口在，标注才打在类上：构造这个注册表就等于承诺「某些实体可能反射解析」。
/// 想在 trim/AOT 下拿到「0 动态代码」，条件不是换注册表而是<b>每个实体都被生成过</b>——
/// 那件事由 <c>QuickSql003</c>（哪个实体没走 G1）保证，不是由这里默默假设。</remarks>
[RequiresUnreferencedCode("G1 未命中的实体退回反射解析：按运行期 Type 走属性与注解（§12.7 A 的 G2 级）。")]
[RequiresDynamicCode("G1 未命中的实体退回 G2，而 G2 的读取器是表达式树编译出来的。")]
public sealed class GeneratedEntitySchemaRegistry : IEntitySchemaRegistry
{
    readonly GeneratedEntityLookup _lookup;
    readonly EntitySchemaRegistry _fallback;
    readonly ITypeMap _types;
    readonly TypeTable<EntityContract> _contracts = new();
    readonly TypeTable<object> _schemas = new();
    readonly TypeTable<object> _validators = new();
    readonly TypeTable<Func<DbDataReader, ReadPlan, object>> _readers = new();
    readonly bool _integralPrimaryKeyAutoIncrement;

    public GeneratedEntitySchemaRegistry(IDbProvider provider, GeneratedEntityLookup lookup, EntitySchemaOptions? options = null, ModelConfiguration? model = null,
        bool integralPrimaryKeyAutoIncrement = true)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(lookup);

        _types = provider.Types;
        _lookup = lookup;
        Options = options ?? new EntitySchemaOptions();
        _integralPrimaryKeyAutoIncrement = integralPrimaryKeyAutoIncrement;
        Resolver = new ContractResolver(provider, model, integralPrimaryKeyAutoIncrement);
        _fallback = new EntitySchemaRegistry(Resolver, Options);
    }

    /// <summary>退回口用的解析器。G1 命中的实体不会经过它。</summary>
    public ContractResolver Resolver { get; }

    public EntitySchemaOptions Options { get; }

    public IReadOnlyCollection<Type> RegisteredTypes => _fallback.RegisteredTypes;

    public EntityContract Contract(Type clrType)
        => Find(clrType) is null
            ? _fallback.Contract(clrType)
            : _contracts.GetOrAdd(clrType, static (type, self) => self.BuildContract(type), this);

    public EntityContract Contract<T>() where T : class => Contract(typeof(T));

    public IEntityTypeSchema<T> Schema<T>() where T : class
        => Find(typeof(T)) is null
            ? _fallback.Schema<T>()
            : (IEntityTypeSchema<T>)_schemas.GetOrAdd(typeof(T), static (type, self) => self.BuildSchema(type), this);

    public IEntityValidator<T> Validator<T>() where T : class
        => GeneratedValidator(typeof(T)) is null
            ? _fallback.Validator<T>()
            : (IEntityValidator<T>)_validators.GetOrAdd(typeof(T), static (type, self) => self.BuildValidator(type), this);

    /// <summary>命中 G1 时读取器直接来自生成的 <see cref="IBoxedEntityTypeSchema"/>：
    /// 反射装箱那一步（<c>EntitySchemaRegistry</c> 里的 <c>MakeGenericMethod</c>）在这条路径上不存在。</summary>
    public Func<DbDataReader, ReadPlan, object> ReadDelegate(Type clrType)
        => Find(clrType) is null
            ? _fallback.ReadDelegate(clrType)
            : _readers.GetOrAdd(clrType, static (type, self) => ((IBoxedEntityTypeSchema)self.BuildSchema(type)).BoxedReader, this);

    GeneratedEntityMetadata? Find(Type clrType)
        => _lookup(clrType, out var metadata) && Resolver.Configuration.Find(clrType) is null ? metadata : null;

    /// <summary>只有 <see cref="ValidationMode.ContractOnly"/> 档有生成的校验器：再往上一档要读注解实例
    /// （<c>ValidationAttribute.IsValid</c>），那是运行期的东西，交给反射兜底路径（§10.6 的分工）。</summary>
    Func<EntityContract, object>? GeneratedValidator(Type clrType)
        => Options.Mode == ValidationMode.ContractOnly && Find(clrType) is { CreateValidator: { } create } ? create : null;

    /// <summary>编译期事实 + provider 那一半 = 最终契约。与 G2 共用这一个收尾器，
    /// 所以「生成路径与反射路径逐字段相等」（§13.11）不是巧合，而是同一段代码的两个输入。</summary>
    EntityContract BuildContract(Type clrType)
    {
        var metadata = Find(clrType)!;

        return EntityContractFactory.Build(metadata.ClrType, metadata.Table, metadata.Columns, metadata.Indexes, metadata.Relationships, _types,
            _integralPrimaryKeyAutoIncrement);
    }

    object BuildSchema(Type clrType)
    {
        var metadata = Find(clrType)!;

        return metadata.CreateSchema(Contract(metadata.ClrType));
    }

    object BuildValidator(Type clrType)
    {
        var metadata = Find(clrType)!;

        return metadata.CreateValidator!(Contract(metadata.ClrType));
    }
}
