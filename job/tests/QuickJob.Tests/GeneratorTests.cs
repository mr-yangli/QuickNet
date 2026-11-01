namespace QuickJob.Tests;

using QuickJob;
using QuickJob.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;

/// <summary>
/// QuickJobRegistrationGenerator 的端到端：Roslyn 内存编译，故意写错的用法要逐条报出对应的
/// QJ 诊断，合法的用法要产出一段能编译过、每 job 一行 <c>PendingRegistrations.Add</c> 的登记代码。
/// </summary>
public class GeneratorTests
{
    [Fact]
    public void Valid_program_emits_a_compilable_registration_file()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using QuickJob;

            namespace Probe
            {
                public sealed class Jobs
                {
                    [QuickJob("tick", Every = "1s")]
                    public static void Tick() { }

                    [QuickJob("drain", Cron = "0 3 * * *")]
                    public static async Task Drain(CancellationToken ct) => await Task.CompletedTask;

                    [QuickJob("load", Every = "5m", MaxRetries = 2, OnOverlap = OverlapPolicy.Queue,
                        Priority = JobPriority.High, RetryJitter = 0.5, Description = "demo")]
                    public static ValueTask Load(JobRunContext ctx, CancellationToken ct) => ValueTask.CompletedTask;

                    [QuickJob("typed", Every = "30s", RequestType = typeof(Msg))]
                    public static Task HandleTyped(Msg msg, CancellationToken ct) => Task.CompletedTask;
                }

                public sealed record Msg(string Name);
            }
            """;

        var (run, _) = RunGenerator(source);
        Assert.Empty(run.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var generated = run.Results.SelectMany(r => r.GeneratedSources).Single(gs => gs.HintName == "QuickJobRegistrations.g.cs");
        var text = generated.SourceText!.ToString();

        Assert.Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]", text);
        Assert.Contains("Name = \"tick\"", text);
        Assert.Contains("Cron = \"0 3 * * *\"", text);
        Assert.Contains("Every = \"5m\"", text);
        Assert.Contains("MaxRetries = 2", text);
        Assert.Contains("global::QuickJob.OverlapPolicy.Queue", text);
        Assert.Contains("global::QuickJob.JobPriority.High", text);
        Assert.Contains("RetryJitter = 0.5", text);
        Assert.Contains("Description = \"demo\"", text);
        Assert.Contains("global::QuickJob.JobHandlerBinder.From", text);
        // 生成的登记代码真的编译得过（含方法组 → 重载解析）
        var compile = FinalCompilation(source, text);
        Assert.DoesNotContain(compile.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("QJ0001", """
        using QuickJob;
        internal class C
        {
            [QuickJob("bad", Every = "1s")]
            public void NotStatic() { }
        }
        """)]
    [InlineData("QJ0002", """
        using QuickJob;
        internal class C
        {
            [QuickJob("bad", Every = "1s")]
            public static void Weird(string garbage) { }
        }
        """)]
    [InlineData("QJ0003", """
        using QuickJob;
        internal class C
        {
            [QuickJob("bad", Cron = "0 0 * * *", Every = "1m")]
            public static void Both() { }
        }
        """)]
    [InlineData("QJ0004", """
        using QuickJob;
        internal class C
        {
            [QuickJob("bad", Every = "1s")]
            private static void Hidden() { }

            [QuickJob("bad2", Every = "1s")]
            public static void Ok() { } // 好的一条不碰诊断
        }
        """)]
    [InlineData("QJ0005", """
        using QuickJob;
        internal class C
        {
            [QuickJob("dupe", Every = "1s")]
            public static void A() { }

            [QuickJob("dupe", Every = "1m")]
            public static void B() { }
        }
        """)]
    [InlineData("QJ0006", """
        using QuickJob;
        internal class C
        {
            [QuickJob("bad", Every = "5x")]
            public static void BadDuration() { }
        }
        """)]
    [InlineData("QJ0007", """
        using QuickJob;
        internal class C
        {
            [QuickJob("bad", Cron = "not a cron")]
            public static void BadCron() { }
        }
        """)]
    [InlineData("QJ0011", """
        using QuickJob;
        internal class C
        {
            [QuickJob("bad", Every = "00:00:00")]
            public static void ZeroEvery() { }
        }
        """)]
    public void Broken_usages_surface_the_matching_diagnostic(string expectedId, string source)
    {
        var (run, _) = RunGenerator(source);
        var ids = run.Diagnostics
            .Where(d => d.Id.StartsWith("QJ", StringComparison.Ordinal))
            .Select(d => d.Id)
            .ToHashSet();

        Assert.Contains(expectedId, ids);
        Assert.DoesNotContain("QJ9999", ids); // 版式指纹：没有未登记的 ID
        foreach (var id in ids)
        {
            var registered = id is "QJ0001" or "QJ0002" or "QJ0003" or "QJ0004" or "QJ0005" or "QJ0006" or "QJ0007" or "QJ0011";
            Assert.True(registered, $"diagnostic {id} isn't in the release ledger");
        }
    }

    [Fact]
    public void Clean_program_gets_no_generator_noise()
    {
        const string source = """
            using QuickJob;
            internal class C
            {
                [QuickJob("ok", Every = "1h")]
                public static void Ok() { }
            }
            """;
        var (run, _) = RunGenerator(source);
        Assert.Empty(run.Diagnostics);
    }

    // ------------------------------------------------------------------ harness

    private static (GeneratorDriverRunResult Run, Compilation Compilation) RunGenerator(string source)
    {
        var compilation = BasicCompilation(source);
        var driver = CSharpGeneratorDriver.Create(new ISourceGenerator[] { new QuickJobRegistrationGenerator().AsSourceGenerator() });
        driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);
        return (driver.GetRunResult(), compilation);
    }

    private static Compilation BasicCompilation(string source)
        => CSharpCompilation.Create("gen-probe", [CSharpSyntaxTree.ParseText(source, encoding: Encoding.UTF8)],
            References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static Compilation FinalCompilation(string source, string generated)
    {
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(source, encoding: Encoding.UTF8),
            CSharpSyntaxTree.ParseText(generated, encoding: Encoding.UTF8),
        };
        return CSharpCompilation.Create("gen-probe-final", trees, References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static IReadOnlyList<MetadataReference> References()
    {
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
        // QuickJob 本体在测试进程里活着 —— 直接补上（属性 + 生成的回填目标都在这一颗里）
        refs.Add(MetadataReference.CreateFromFile(typeof(QuickJobAttribute).Assembly.Location));
        return refs;
    }
}
