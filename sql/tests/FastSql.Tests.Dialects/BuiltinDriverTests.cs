using FastSql.DriverLoading;
using Xunit;

namespace FastSql.Tests.Dialects;

/// <summary>§9.5 的另一头：<b>现场什么都没配</b>时驱动从哪来。
///
/// <para>上一版这里只有清单那一条路，而"清单没复制到输出目录"是<em>静默</em>的
/// （<see cref="DriverManifest.Load"/> 返回 <see cref="DriverManifest.Empty"/>），于是一个只报库种与串的宿主
/// 在启动期报出来的是"驱动不存在"，而真实原因是一份看不见的文件。现在内建默认表接住这一档，
/// 清单降级成纯粹的覆盖手段 —— 这两件事都要能离线钉住，所以本文件不连库、也不要任何一份真驱动。</para></summary>
public sealed class BuiltinDriverTests
{
    /// <summary>一个谁也不会去配的键，只属于最后那条"内建表里也没有"的用例。</summary>
    const string UnlistedKey = "nosuchprovider";

    [Fact]
    public void Every_db_kind_has_a_builtin_row()
    {
        // 这颗断言钉的是覆盖面：新增一颗 DbKind 而忘了配内建行，就会在只报库种与串的宿主上变成启动期报错，
        // 而那台机器上根本没有清单文件可查。key 的取法照 <see cref="DbKind"/> 名的小写（与清单同一套，无映射表）。
        foreach (var kind in Enum.GetValues<DbKind>())
        {
            var reference = BuiltinDrivers.Find(kind.ToString().ToLowerInvariant());

            Assert.NotNull(reference);
            Assert.False(string.IsNullOrEmpty(reference!.AssemblyName), $"{kind} 的内建行没有程序集名");
        }

        Assert.Equal(Enum.GetValues<DbKind>().Length, BuiltinDrivers.Drivers.Count);
    }

    [Fact]
    public void Builtin_rows_match_the_shipped_manifest()
    {
        // 两份表内容重复一次，就一定会漂移：仓库根那份是文档化的台账（每行带着自己的出处），代码这份是零配置的入口。
        // 这条把漂移钉成构建期红，而不是等到某台信创机器上"清单删了就装不上"才发现。
        var manifest = DriverManifest.Load(RepoManifest);

        Assert.NotEmpty(manifest.Drivers);

        var shipped = manifest.Drivers.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
        var builtin = BuiltinDrivers.Drivers.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(shipped.Keys.OrderBy(k => k, StringComparer.Ordinal), builtin.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (key, reference) in builtin)
        {
            Assert.Equal(shipped[key].AssemblyName, reference.AssemblyName);
            Assert.Equal(shipped[key].FactoryType, reference.FactoryType);
        }
    }

    [Fact]
    public void A_manifest_less_registry_still_reaches_the_assembly()
    {
        // 清单不在（传的是一份永远不存在的路径）：退到内建表之后要一路走到装载那一步，
        // 于是报错说的是"这颗程序集装不上"，而不是"清单里没有这一行"——两句话各有各的修法。
        var registry = DriverRegistry.Load(Path.Combine(Path.GetTempPath(), "fastsql-no-such-dir", "FastSqlDrivers.json"));

        var failure = Record.Exception(() => registry.Factory("kingbase"));

        Assert.IsType<DriverNotAvailableException>(failure);
        Checks.Has("Kdbndp", failure!.Message);
        Checks.Has("内建默认", failure.Message);
    }

    [Fact]
    public void A_key_the_builtin_table_does_not_know_still_points_at_the_manifest()
    {
        var registry = DriverRegistry.Load(Path.Combine(Path.GetTempPath(), "fastsql-no-such-dir", "FastSqlDrivers.json"));

        var failure = Record.Exception(() => registry.Factory(UnlistedKey));

        Assert.IsType<DriverNotAvailableException>(failure);
        Checks.Has("FastSqlDrivers.json", failure!.Message);
        Checks.Has(UnlistedKey, failure.Message);
        Checks.Has("\"assembly\"", failure.Message);
    }

    /// <summary>仓库根那份清单：与 <see cref="Golden"/> 同一种定位法（从输出目录往上找根），
    /// 因为跑测试的工作目录不固定。</summary>
    static string RepoManifest
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var file = Path.Combine(dir.FullName, "FastSqlDrivers.json");

                if (File.Exists(file))
                    return file;
            }

            throw new InvalidOperationException($"从 {AppContext.BaseDirectory} 向上找不到 FastSqlDrivers.json。");
        }
    }
}
