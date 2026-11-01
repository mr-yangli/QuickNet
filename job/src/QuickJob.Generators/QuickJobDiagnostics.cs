using Microsoft.CodeAnalysis;

namespace QuickJob.Generators;

/// <summary>诊断族。话术修复在各自的分析器文件里现拼 —— 与运行期对应的那句
///（DurationText.Parse / PendingRegistrations.Add / CronSchedule.Parse）同语义但不同一格，
/// 因为编译期看见的是"形状"，运行期看见的是"值"。</summary>
static class QuickJobDiagnostics
{
    const string Category = "QuickJob.Registration";

    internal static readonly DiagnosticDescriptor MustBeStatic = new(
        "QJ0001", "[QuickJob] 方法必须 static", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedShape = new(
        "QJ0002", "handler 形状不在支持族", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor CronEveryConflict = new(
        "QJ0003", "Cron 与 Every 互斥", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnreachableMethod = new(
        "QJ0004", "方法或容器对生成的登记代码不可达", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DuplicateJobName = new(
        "QJ0005", "重复的 job 名", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DurationUnusable = new(
        "QJ0006", "时长文本解析不了", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor CronStructureUnusable = new(
        "QJ0007", "Cron 表达式结构不对", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor EveryNotPositive = new(
        "QJ0011", "Every 必须是正时长", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static Diagnostic MustBeStaticAt(Location location, string method)
        => Diagnostic.Create(MustBeStatic, location, $"[QuickJob] '{method}' must be static — the scheduler only runs static job bodies.");

    internal static Diagnostic UnsupportedShapeAt(Location location, string method)
        => Diagnostic.Create(UnsupportedShape, location,
            $"[QuickJob] '{method}' is not a supported handler shape. Supported: () / (CancellationToken) / (System.Threading.Tasks.JobRunContext) / (JobRunContext, CancellationToken) returning void, System.Threading.Tasks.Task or System.Threading.Tasks.ValueTask; with RequestType set: (RequestType) or (RequestType, CancellationToken) only.");

    internal static Diagnostic CronEveryConflictAt(Location location, string name)
        => Diagnostic.Create(CronEveryConflict, location,
            $"[QuickJob] '{name}' cannot declare both Cron and Every — recurring jobs pick one clock.");

    internal static Diagnostic UnreachableMethodAt(Location location, string method, string reason)
        => Diagnostic.Create(UnreachableMethod, location, $"[QuickJob] '{method}': generated registration cannot reach it — {reason}");

    internal static Diagnostic DuplicateJobNameAt(Location location, string name)
        => Diagnostic.Create(DuplicateJobName, location,
            $"[QuickJob] duplicate job name '{name}' — names must be unique across all declaring assemblies (runtime PendingRegistrations.Add would throw the same line).");

    internal static Diagnostic DurationUnusableAt(Location location, string optionName, string text)
        => Diagnostic.Create(DurationUnusable, location,
            $"[QuickJob] '{optionName}' is not a parseable duration: '{text}' — use TimeSpan text (\"00:05:00\") or suffix forms (\"30s\", \"5m\", \"2h\", \"2d\", \"1500ms\")");

    internal static Diagnostic CronStructureUnusableAt(Location location, string expression, string reason)
        => Diagnostic.Create(CronStructureUnusable, location,
            $"[QuickJob] cron '{expression}' fails {reason} (final semantics stay with runtime CronSchedule.Parse).");

    internal static Diagnostic EveryNotPositiveAt(Location location, string text)
        => Diagnostic.Create(EveryNotPositive, location,
            $"[QuickJob] 'Every' must be a positive duration, got '{text}' — interval definition needs a positive Every.");
}
