using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FastSql.DriverLoading;
using FastSql.Execution;
using FastSql.Metadata;
using FastSql.Metadata.Fluent;
using FastSql.Providers;
using FastSql.Query;
using FastSql.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace FastSql;

/// <summary>EF Core 风格的注册入口。<b>默认 Scoped</b>：与 <c>AddDbContext</c> 同一条判据 ——
/// 虽然 <see cref="FastSqlOrm"/> 不持有连接（连接按语句从池取），但 <see cref="Query.Filters.FilterRegistry"/>
/// 是每实例一份，Scoped 下每个请求可以独立注册租户过滤而不影响其他请求。
/// 重对象（<see cref="IEntitySchemaRegistry"/>、<see cref="IDbProvider"/>）走 Singleton，
/// 与 EF Core 把 <c>IModel</c> 缓存在单例里同一条路。</summary>
public sealed class FastSqlOrmBuilder
{
    internal DbKind Kind { get; set; }
    internal string ConnectionString { get; set; } = "";
    internal Func<IServiceProvider, string>? ConnectionStringFactory { get; set; }
    internal DbProviderFactory? DriverFactory { get; set; }

    /// <summary>缺省集群 <c>main</c> 的从库清单（§16.4② 的读写分离那一档），按给出的顺序轮询。
    /// 空清单 = 这座库不分读写，读与写落在 <see cref="ConnectionString"/> 那一条串上，
    /// 也就是这一格落地之前的行为。</summary>
    public IReadOnlyList<string> Replicas { get; set; } = [];

    /// <summary><c>main</c> 之外的具名集群（分库那一档）。实体用 <c>[DataSource("名字")]</c> 点名自己住哪一座。
    /// <b>这里的串都是启动期常量</b>：按请求解析的那颗工厂只管 <c>main</c> 的主库串（多租户每库一串），
    /// 要让一座集群按请求换串，请为那一座另建一颗 orm，而不是在这一格里塞工厂。</summary>
    public IReadOnlyList<ClusterConfig> Clusters { get; set; } = [];

    /// <summary>Fluent 模型配置（§10.2 优先级 1）。null 表示只用 DataAnnotations 与约定。</summary>
    public ModelConfiguration? Model { get; set; }

    /// <summary>语句超时秒数。默认 30。</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>§9.4：无 ORDER BY 的分页查询怎么办。默认 <see cref="PagingOrderingPolicy.Throw"/>。</summary>
    public PagingOrderingPolicy PagingOrdering { get; set; } = PagingOrderingPolicy.Throw;

    /// <summary>§9.5：结构同步策略。默认 <see cref="SchemaSyncPolicy.Disabled"/>。</summary>
    public SchemaSyncPolicy SchemaSync { get; set; } = SchemaSyncPolicy.Disabled;

    /// <summary>§9.5：非 Development 环境下强制执行结构同步的开关。</summary>
    public bool ForceSchemaSync { get; set; }

    internal FastSqlOrmOptions BuildOptions(IDbProvider provider, string connectionString) => new()
    {
        Provider = provider,
        ConnectionString = connectionString,
        Replicas = Replicas,
        Clusters = Clusters,
        Model = Model,
        CommandTimeoutSeconds = CommandTimeoutSeconds,
        PagingOrdering = PagingOrdering,
        SchemaSync = SchemaSync,
        ForceSchemaSync = ForceSchemaSync,
    };
}

/// <summary>DI 注册入口。</summary>
public static class FastSqlServiceCollectionExtensions
{
    /// <summary>EF Core 风格的注册口。<b>默认 Scoped</b>，与 <c>AddDbContext</c> 同一条判据。
    /// 重对象（<see cref="IEntitySchemaRegistry"/>、<see cref="IDbProvider"/>）走 Singleton。
    /// 驱动工厂由 <see cref="DriverRegistry"/> 按 <c>FastSqlDrivers.json</c> 现场解析。</summary>
    [RequiresUnreferencedCode("FastSqlOrm 的 G2 兜底构造器按 Type 反射解析实体与注解。")]
    [RequiresDynamicCode("FastSqlOrm 的 G2 兜底构造器编译读写委托。")]
    public static IServiceCollection AddFastSqlOrm(
        this IServiceCollection services,
        DbKind kind,
        string connectionString,
        Action<FastSqlOrmBuilder>? configure = null)
    {
        var builder = new FastSqlOrmBuilder { Kind = kind, ConnectionString = connectionString };
        configure?.Invoke(builder);

        return RegisterCore(services, builder);
    }

    /// <summary>连接串按请求解析的注册口（多租户每库一串）。<paramref name="connectionStringFactory"/>
    /// 在每个 Scoped 解析时执行一次，可以从 <c>IServiceProvider</c> 取当前租户上下文决定走哪条串。
    /// 与 EF Core 的 <c>AddDbContext&lt;T&gt;(Action&lt;IServiceProvider, DbContextOptionsBuilder&gt;)</c> 同一姿态。</summary>
    [RequiresUnreferencedCode("FastSqlOrm 的 G2 兜底构造器按 Type 反射解析实体与注解。")]
    [RequiresDynamicCode("FastSqlOrm 的 G2 兜底构造器编译读写委托。")]
    public static IServiceCollection AddFastSqlOrm(
        this IServiceCollection services,
        DbKind kind,
        Func<IServiceProvider, string> connectionStringFactory,
        Action<FastSqlOrmBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(connectionStringFactory);

        var builder = new FastSqlOrmBuilder { Kind = kind, ConnectionStringFactory = connectionStringFactory };
        configure?.Invoke(builder);

        return RegisterCore(services, builder);
    }

    /// <summary>已知工厂的注册口。跳过清单解析，适合宿主自己引用了驱动包的场景。</summary>
    [RequiresUnreferencedCode("FastSqlOrm 的 G2 兜底构造器按 Type 反射解析实体与注解。")]
    [RequiresDynamicCode("FastSqlOrm 的 G2 兜底构造器编译读写委托。")]
    public static IServiceCollection AddFastSqlOrm(
        this IServiceCollection services,
        DbKind kind,
        string connectionString,
        DbProviderFactory driverFactory,
        Action<FastSqlOrmBuilder>? configure = null)
    {
        var builder = new FastSqlOrmBuilder { Kind = kind, ConnectionString = connectionString, DriverFactory = driverFactory };
        configure?.Invoke(builder);

        return RegisterCore(services, builder);
    }

    /// <summary>已知工厂 + 连接串按请求解析。多租户每库一串 + 宿主自持驱动的组合。</summary>
    [RequiresUnreferencedCode("FastSqlOrm 的 G2 兜底构造器按 Type 反射解析实体与注解。")]
    [RequiresDynamicCode("FastSqlOrm 的 G2 兜底构造器编译读写委托。")]
    public static IServiceCollection AddFastSqlOrm(
        this IServiceCollection services,
        DbKind kind,
        Func<IServiceProvider, string> connectionStringFactory,
        DbProviderFactory driverFactory,
        Action<FastSqlOrmBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(connectionStringFactory);

        var builder = new FastSqlOrmBuilder { Kind = kind, ConnectionStringFactory = connectionStringFactory, DriverFactory = driverFactory };
        configure?.Invoke(builder);

        return RegisterCore(services, builder);
    }

    [RequiresUnreferencedCode("注册路径含 G2 兜底构造器与 DriverRegistry 反射。")]
    [RequiresDynamicCode("注册路径含 G2 兜底构造器。")]
    static IServiceCollection RegisterCore(IServiceCollection services, FastSqlOrmBuilder builder)
    {
        // 启动期只校验静态串；工厂模式延到 Scoped 解析时再判。
        if (builder.ConnectionStringFactory is null && string.IsNullOrEmpty(builder.ConnectionString))
            throw new InvalidOperationException($"AddFastSqlOrm：DbKind.{builder.Kind} 的连接串为空。");

        // 从库与具名集群是启动期常量，而工厂解析的主库串每个请求一条：两者配在一起时
        // main 的主库属于当前租户、它的从库属于写配置时那一条串，症状是"读到别的租户的数据"。
        // 这一格不给静默的机会（要按请求换整座集群就为那一座另建一颗 orm，判据在 FastSqlOrmBuilder.Clusters）。
        if (builder.ConnectionStringFactory is not null && (builder.Replicas.Count > 0 || builder.Clusters.Count > 0))
            throw new InvalidOperationException(
                "AddFastSqlOrm：连接串工厂是按请求解析的，而从库与具名集群是启动期常量——两者混用会让 main 的主库与从库属于不同的租户。" +
                "要么整座集群都用静态串（去掉工厂），要么按请求换库时另建一颗 orm。");

        // 静态配置在这一格就把 ConnectionSources 建出来判一次：具名集群里出现 main、
        // 从库清单里有空串，都在注册期红，而不是等到第一条语句。
        if (builder.ConnectionStringFactory is null)
            new ConnectionSources(builder.ConnectionString, builder.Replicas, builder.Clusters);

        services.AddSingleton(sp =>
        {
            var factory = builder.DriverFactory ?? ResolveFactory(builder.Kind);
            if (!ProviderRegistry.TryCreate(builder.Kind, factory, out var provider))
                throw new InvalidOperationException(
                    $"DbKind.{builder.Kind} 未在 ProviderRegistry 中登记。可能原因：① 生成器 FastSql.Generators 未作为 Analyzer 挂进编译；② 该 provider 程序集未被引用。");
            return provider;
        });

        // EntitySchemaRegistry 只依赖 Provider + Model，都是启动期常量 → Singleton。
        // 不依赖 FastSqlOrmOptions（那是 Scoped 的），直接从 Provider 拿 Model。
        services.AddSingleton<IEntitySchemaRegistry>(sp =>
        {
            var provider = sp.GetRequiredService<IDbProvider>();
            return new EntitySchemaRegistry(new ContractResolver(provider, builder.Model));
        });

        // FastSqlOrmOptions 走 Scoped：连接串每个请求可以不同（多租户每库一串）。
        // 与 EF Core 的 DbContextOptions<T> 同一条判据。
        services.AddScoped(sp =>
        {
            var provider = sp.GetRequiredService<IDbProvider>();
            var connectionString = builder.ConnectionStringFactory?.Invoke(sp) ?? builder.ConnectionString;

            if (string.IsNullOrEmpty(connectionString))
                throw new InvalidOperationException(
                    $"AddFastSqlOrm：DbKind.{builder.Kind} 的连接串工厂返回了空值。" +
                    "检查租户解析逻辑：当前 IServiceProvider 里是否拿得到租户对应的连接串。");

            return builder.BuildOptions(provider, connectionString);
        });

        services.AddScoped(sp =>
        {
            var options = sp.GetRequiredService<FastSqlOrmOptions>();
            var schemas = sp.GetRequiredService<IEntitySchemaRegistry>();
            return new FastSqlOrm(options, schemas);
        });

        return services;
    }

    [RequiresUnreferencedCode("DriverRegistry 按清单反射解析 DbProviderFactory。")]
    static DbProviderFactory ResolveFactory(DbKind kind)
    {
        var driverKey = ProviderRegistry.DriverKeyOf(kind)
            ?? throw new InvalidOperationException(
                $"DbKind.{kind} 未在 ProviderRegistry 中登记（生成器未挂或 provider 未引用）。");
        return DriverRegistry.Load().Factory(driverKey);
    }
}
