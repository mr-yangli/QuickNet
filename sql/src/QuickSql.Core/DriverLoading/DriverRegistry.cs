using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace QuickSql.DriverLoading;

/// <summary>一条驱动声明。<see cref="Path"/> 为空时先看 <c>drivers/</c>，再退到默认探测路径
/// （<c>Microsoft.Data.SqlClient</c> 这类公开包就走后者）；非空时从该文件加载 —— 信创现场把
/// DM/Kingbase 的 DLL 丢进 <c>drivers/</c> 即可，不改代码、不动 csproj（§1.2 原则 3）。</summary>
public sealed record DriverRef(string Key, string AssemblyName, string? Path, string? FactoryType);

/// <summary><c>QuickSqlDrivers.json</c> 的内容。手写 <see cref="JsonElement"/> 解析而不用
/// <c>JsonSerializer</c>：后者要反射，会让这条路径在 AOT 下当场失效，而它正是 AOT 场景唯一的驱动入口。
/// 文件不在 → <see cref="Empty"/>（<b>不是错误</b>：<see cref="DriverRegistry.Factory"/> 会退到
/// <see cref="BuiltinDrivers"/>，清单只用来覆盖名字或写 <c>path</c>）。</summary>
public sealed class DriverManifest(IReadOnlyList<DriverRef> drivers)
{
    public static DriverManifest Empty { get; } = new([]);

    public IReadOnlyList<DriverRef> Drivers { get; } = drivers;

    public DriverRef? Find(string key) => Drivers.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "QuickSqlDrivers.json");

    public static DriverManifest Load(string? path = null)
    {
        var file = path ?? Environment.GetEnvironmentVariable("QUICKSQL_DRIVERS_CONFIG") ?? DefaultPath;

        if (!File.Exists(file))
            return Empty;

        using var document = JsonDocument.Parse(File.ReadAllText(file));

        if (!document.RootElement.TryGetProperty("drivers", out var drivers) || drivers.ValueKind != JsonValueKind.Array)
            throw new QuickSqlException($"{file} 缺少 drivers 数组");

        var result = new List<DriverRef>();

        foreach (var driver in drivers.EnumerateArray())
            result.Add(new DriverRef(Required(driver, "key", file), Required(driver, "assembly", file), Optional(driver, "path"), Optional(driver, "factoryType")));

        return new DriverManifest(result);
    }

    static string Required(JsonElement element, string property, string file)
        => Optional(element, property) ?? throw new QuickSqlException($"{file} 的一项驱动缺少 {property} 字段");

    static string? Optional(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>磁盘上指定的驱动装在独立上下文里：国产库驱动常自带一份旧版 <c>System.Buffers</c> /
/// <c>Microsoft.Extensions.*</c>，混进默认上下文会变成"装不上且报不出原因"。
/// 这里刻意不覆写 <c>Load</c> —— 只有 <see cref="AssemblyLoadContext.LoadFromAssemblyPath(string)"/>
/// 的那一个程序集落在本上下文，它的依赖仍回退默认上下文，免得把 BCL 复制一份。</summary>
sealed class DriverLoadContext(string key) : AssemblyLoadContext(key);

/// <summary>驱动的装载与工厂解析。<b>反射只发生在这里，且只在第一次取用某条驱动时</b>：
/// 拿到 <see cref="DbProviderFactory"/> 之后，连接/命令/参数一律是虚调用（§12.7「加载可以反射，调用不行」）。</summary>
public sealed class DriverRegistry(DriverManifest manifest, string? root = null)
{
    readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    public string Root { get; } = root ?? AppContext.BaseDirectory;

    public static DriverRegistry Load(string? configPath = null)
        => new(DriverManifest.Load(configPath), Path.GetDirectoryName(configPath ?? DriverManifest.DefaultPath));

    /// <summary>注入一个已经在进程里的工厂：测试与「宿主自己就引用了驱动包」的场景用，
    /// 不构成绕过 —— 它同样是运行期取用，编译期没有任何驱动引用。</summary>
    public void Register(string key, DbProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_gate)
            _cache[key] = new CacheEntry(factory, null);
    }

    /// <summary>取驱动工厂。<paramref name="key"/> 用 <see cref="DbKind"/> 的小写名（<c>sqlserver</c>、<c>dameng</c>），
    /// 与 <c>QuickSqlDrivers.json</c> 里的 <c>key</c> 一一对应；<b>清单里没有这一行时退到
    /// <see cref="BuiltinDrivers"/> 那张内建默认表</b>，所以宿主什么都不配也能只报库种与串就装配起来。
    /// 需要反射的原因有两个，都写在这一层而不下沉：清单里给的是<b>类型名</b>，以及工厂的 <c>Instance</c> 是反射取的字段。
    /// AOT 现场的干净路径是 <see cref="Register"/>：宿主自己引用驱动包，把工厂交进来，编译期仍然没有驱动引用。</summary>
    [RequiresUnreferencedCode("按清单里的程序集名与类型名解析驱动工厂，被裁剪的类型会在这里报驱动不存在。")]
    public DbProviderFactory Factory(string key)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached.Failure is null)
                    return cached.Factory!;

                throw new DriverNotAvailableException(key, cached.Failure);
            }
        }

        var reference = manifest.Find(key);
        var fromManifest = reference is not null;

        // 清单没这一行不等于没得配：内建默认表接住「现场什么都没配」那一档（§9.5 的零配置入口）。
        reference ??= BuiltinDrivers.Find(key);

        if (reference is null)
            throw Fail(key, inner: null, Hint(key));

        DbProviderFactory factory;

        try
        {
            factory = Resolve(reference, Root);
        }
        catch (Exception ex)
        {
            // 失败也缓存：否则每次执行都要重走一遍抛异常的加载路径，故障现场的日志会被同一条堆栈刷满。
            throw Fail(key, ex, $"程序集 {reference.AssemblyName}，{(fromManifest ? "清单路径 " : "内建默认 ")}{reference.Path ?? "<probing>"}");
        }

        lock (_gate)
            _cache[key] = new CacheEntry(factory, null);

        return factory;
    }

    [RequiresUnreferencedCode("转调 Factory，反射解析驱动工厂。")]
    public bool TryFactory(string key, [NotNullWhen(true)] out DbProviderFactory? factory, out string? failure)
    {
        try
        {
            factory = Factory(key);
            failure = null;
            return true;
        }
        catch (DriverNotAvailableException ex)
        {
            factory = null;
            failure = ex.Message;
            return false;
        }
    }

    [RequiresUnreferencedCode("按类型名在运行时程序集里找 DbProviderFactory，裁剪后可能找不到。")]
    static DbProviderFactory Resolve(DriverRef reference, string root)
    {
        var assembly = LoadAssembly(reference, root);

        if (reference.FactoryType is { Length: > 0 } declared)
            return FromType(assembly.GetType(declared, throwOnError: true)!);

        foreach (var type in assembly.GetExportedTypes())
        {
            if (!type.IsAbstract && typeof(DbProviderFactory).IsAssignableFrom(type))
                return FromType(type);
        }

        throw new QuickSqlException($"{assembly.GetName().Name} 里没有可实例化的 DbProviderFactory 子类，请在清单里补 factoryType");
    }

    [RequiresUnreferencedCode("LoadFromAssemblyPath 拿到的程序集不在裁剪分析的引用图里。")]
    static Assembly LoadAssembly(DriverRef reference, string root)
    {
        string? file;

        if (reference.Path is { Length: > 0 } declared)
        {
            // 归一化不是整洁癖，是一格失败面：现场在清单里写 "drivers/XuguClient.dll"（正斜杠）时
            // File.Exists 与 LoadFromAssemblyPath 都照收，托管程序集也确实加载上了 —— 但"带原生同伴的驱动"
            // 要到第一次真正用到它才炸 DllNotFoundException，而前面每一格都是一片绿。v0.32 在一台活虚谷上
            // 量到了这一翻色：同一份 drivers/ 摆法、同一颗文件，只把声明里的分隔符从 '\' 换成 '/'，
            // OPEN 就从成功翻成失败（§13.5 那一行的五格 A/B）。GetFullPath 把这条差异从源头抹掉。
            file = Path.GetFullPath(Path.IsPathRooted(declared) ? declared : Path.Combine(root, declared));

            // 配了路径却找不到：宁可直接说清楚，也不要退化成"驱动不存在"这种看不出原因的失败。
            if (!File.Exists(file))
                throw new DriverNotAvailableException(reference.Key, $"声明的驱动文件不存在：{file}");
        }
        else
        {
            // 只探 drivers/ 这一层，不探输出根目录：那里能找到的公开包默认上下文本来就能加载，
            // 复制进独立上下文只会让同一类型出现两份。
            file = Path.Combine(root, "drivers", reference.AssemblyName + ".dll");

            if (!File.Exists(file))
                return Assembly.Load(new AssemblyName(reference.AssemblyName));
        }

        return new DriverLoadContext(reference.Key).LoadFromAssemblyPath(file);
    }

    /// <summary>驱动工厂都是单例：优先读它自己声明的 <c>Instance</c>，其次无参构造。两条都只在加载期走一次。</summary>
    [RequiresUnreferencedCode("读驱动工厂自己的 Instance 静态字段，或反射实例化它。")]
    static DbProviderFactory FromType(Type factoryType)
        => (factoryType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as DbProviderFactory)
           ?? (Activator.CreateInstance(factoryType) as DbProviderFactory)
           ?? throw new QuickSqlException($"驱动工厂 {factoryType.FullName} 既无 Instance 也无法实例化");

    string Hint(string key)
        => $"清单里没有 {key}。在 QuickSqlDrivers.json 的 drivers 数组里补一项：" +
           $$"""{"key":"{{key}}","assembly":"<驱动程序集名>","path":"drivers/<可选>","factoryType":"<DbProviderFactory 全名，可省略>"}""";

    DriverNotAvailableException Fail(string key, Exception? inner, string detail)
    {
        var message = inner is null ? detail : inner.Message + "；" + detail;

        lock (_gate)
            _cache[key] = new CacheEntry(null, message);

        return new DriverNotAvailableException(key, message, inner);
    }

    sealed record CacheEntry(DbProviderFactory? Factory, string? Failure);
}
