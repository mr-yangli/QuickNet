using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace QuickJob.Generators;

/// <summary>
/// [QuickJob] 静态方法的编译期登记生成器。与 QuickSql 的 EntityContractGenerator 同一触发链：
/// <c>ForAttributeWithMetadataName</c> 只看当前编译单元的源码，每个标注过的方法做「形状校验 +
/// 属性值搬运」，然后统一发射一段 <c>[ModuleInitializer]</c> —— 每个方法一行
/// <c>PendingRegistrations.Add(...)</c>，其余的（时长解析、并发闸门、竞态序）全部留给运行期
/// <see cref="PendingRegistrations"/> 点头才发生。零反射、AOT 干净。
/// </summary>
/// <remarks>边界与 QuickSql 的 provider 登记面同理：本生成器<b>不引用</b> QuickJob 运行时程序集，
/// 产出的代码里一律 <c>global::</c> 全名 —— 不引用就不会把生成器与运行时的版本锁成一对。
/// 方法本身所在编译单元带上了本生成器（通过 <c>QuickJob.Generators</c> 的 analyzer/dotnet/cs 包）
/// 就会得到登记代码；没有它，启动期 <c>QuickJobScheduler.StartAsync</c> 会点名"有声明走编译期"。</remarks>
[Generator(LanguageNames.CSharp)]
public sealed class QuickJobRegistrationGenerator : IIncrementalGenerator
{
    internal const string AttributeFqn = "QuickJob.QuickJobAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 逐方法分析（形状 + 属性搬运），罗列后交给一次性的文件输出。
        var batch = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeFqn,
                static (node, _) => node is MethodDeclarationSyntax { AttributeLists.Count: > 0 },
                static (genCtx, _) => JobAnalyzer.Analyze(genCtx))
            .Collect();

        context.RegisterSourceOutput(batch, static (spc, jobs) =>
        {
            // 诊断先报全名重复与坏形状；纯洁的条目才进发射名单。
            var emitList = EmitFilter.Select(spc, jobs);
            if (emitList.Count > 0)
            {
                spc.AddSource("QuickJobRegistrations.g.cs", RegistrationEmitter.Render(emitList));
            }
        });
    }
}

/// <summary>发射名单的筛选：报诊断、摘重名、排除带错的条目。</summary>
static class EmitFilter
{
    internal static List<JobModel> Select(SourceProductionContext spc, ImmutableArray<JobModel> batch)
    {
        var emit = new List<JobModel>(batch.Length);
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var model in batch)
        {
            foreach (var diag in model.Diagnostics)
                spc.ReportDiagnostic(diag);

            if (!model.Eligible) continue;
            if (!seen.Add(model.Name))
            {
                spc.ReportDiagnostic(QuickJobDiagnostics.DuplicateJobNameAt(model.Location, model.Name));
                continue;
            }
            emit.Add(model);
        }

        // 输出次序稳定（同一批条目按名字排一次），增量缓存才不需要重建整份生成文件。
        emit.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return emit;
    }
}
