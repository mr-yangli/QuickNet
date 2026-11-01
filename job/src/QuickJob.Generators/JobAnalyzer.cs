using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace QuickJob.Generators;

/// <summary>一颗 [QuickJob] 方法的全量分析：可达性、handler 形状、属性搬运、时长/结构预检。
/// 所有诊断落在收回的 <see cref="JobModel"/> 上 —— 发明名单那一步只做摘重与排次。属性行某一格
/// 读取失败（写法跑偏、常量档不对）按"没写"处理：发射端跳过它，记录默认值顶上。</summary>
static class JobAnalyzer
{
    const string CancellationTokenDisplay = "global::System.Threading.CancellationToken";
    const string JobRunContextDisplay = "global::QuickJob.JobRunContext";
    const string TaskDisplay = "global::System.Threading.Tasks.Task";
    const string ValueTaskDisplay = "global::System.Threading.Tasks.ValueTask";

    static readonly ISet<Accessibility> ReachableAccessibility = new HashSet<Accessibility>
    {
        Accessibility.Public, Accessibility.Internal, Accessibility.ProtectedOrInternal,
    };

    internal static JobModel Analyze(GeneratorAttributeSyntaxContext genCtx)
    {
        var model = new JobModel();
        var method = (IMethodSymbol)genCtx.TargetSymbol;
        var attr = genCtx.Attributes[0];
        model.Location = method.Locations.Length > 0 ? method.Locations[0] : Location.None;
        var shortForm = ShortForm(method);
        var named = attr.NamedArguments;

        // ---- QJ0001: static
        if (!method.IsStatic)
        {
            model.Diagnostics.Add(QuickJobDiagnostics.MustBeStaticAt(model.Location, shortForm));
        }

        // ---- QJ0004: 可达性（private / 泛型容器 / 泛型方法）
        var reachable = true;
        foreach (var type in TypeChain(method.ContainingType))
        {
            if (type.IsGenericType)
            {
                model.Diagnostics.Add(QuickJobDiagnostics.UnreachableMethodAt(model.Location, shortForm,
                    $"containing type '{type.Name}' is generic — generated registration binds non-generic job classes only"));
                reachable = false;
            }
            if (!ReachableAccessibility.Contains(type.DeclaredAccessibility))
            {
                model.Diagnostics.Add(QuickJobDiagnostics.UnreachableMethodAt(model.Location, shortForm,
                    $"containing type '{ShortTypeForm(type)}' is {type.DeclaredAccessibility.ToString().ToLowerInvariant()} — the generated class can only reach internal-or-public chains"));
                reachable = false;
            }
        }
        if (method.IsGenericMethod)
        {
            model.Diagnostics.Add(QuickJobDiagnostics.UnreachableMethodAt(model.Location, shortForm,
                "the handler is a generic method — write the concrete shape at the [QuickJob] site"));
            reachable = false;
        }
        if (!ReachableAccessibility.Contains(method.DeclaredAccessibility))
        {
            model.Diagnostics.Add(QuickJobDiagnostics.UnreachableMethodAt(model.Location, shortForm,
                "the method is private (or protected) — generated registration can only call internal-or-public statics"));
            reachable = false;
        }

        // ---- 属性搬运
        model.Name = FirstCtorString(attr);
        model.Cron = TextRow(named, "Cron");
        model.Every = TextRow(named, "Every");
        model.TimeZone = TextRow(named, "TimeZone");
        model.MaxRetries = IntRow(named, "MaxRetries", 0);
        model.RetryBackoff = EnumRow(named, "RetryBackoff");
        model.RetryBaseDelay = TextRow(named, "RetryBaseDelay");
        model.RetryMaxDelay = TextRow(named, "RetryMaxDelay");
        model.RetryJitter = DoubleRow(named, "RetryJitter", 0.25);
        model.MaxConcurrentPerJob = IntRow(named, "MaxConcurrentPerJob", 1);
        model.OnOverlap = EnumRow(named, "OnOverlap");
        model.OverlapQueueLimit = IntRow(named, "OverlapQueueLimit", 128);
        model.Priority = EnumRow(named, "Priority");
        model.OnMissedSlot = EnumRow(named, "OnMissedSlot");
        model.Timeout = TextRow(named, "Timeout");
        model.Grace = TextRow(named, "Grace");
        model.Description = TextRow(named, "Description");
        model.Enabled = BoolRow(named, "Enabled", fallback: true);

        var requestTypeSymbol = Find(named, "RequestType") is { } requestRow ? requestRow.Value as ITypeSymbol : null;
        model.RequestType = requestTypeSymbol is null ? null : requestTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // ---- QJ0003: Cron/Every 互斥
        if (model.Cron is not null && model.Every is not null)
        {
            model.Diagnostics.Add(QuickJobDiagnostics.CronEveryConflictAt(model.Location, model.Name));
        }

        // ---- QJ0006/QJ0011: 时长预告检（语义向运行期 DurationText 对齐，Every 另加正经要求）
        ValidateDuration(model, "Every", model.Every, needsPositive: true);
        ValidateDuration(model, "Grace", model.Grace);
        ValidateDuration(model, "Timeout", model.Timeout);
        ValidateDuration(model, "RetryBaseDelay", model.RetryBaseDelay);
        ValidateDuration(model, "RetryMaxDelay", model.RetryMaxDelay);

        // ---- QJ0007: cron 结构预告检（最终语义以运行期 CronSchedule.Parse 为准）
        if (model.Cron is not null && CronShape.Validate(model.Cron) is { } cronError)
        {
            model.Diagnostics.Add(QuickJobDiagnostics.CronStructureUnusableAt(model.Location, model.Cron, cronError));
        }

        // ---- QJ0002: handler 形状 → 生成 From/From<T> 方法组
        if (!TryBuildHandler(method, requestTypeSymbol, out var handlerFactory))
        {
            model.Diagnostics.Add(QuickJobDiagnostics.UnsupportedShapeAt(model.Location, shortForm));
        }
        else if (reachable)
        {
            model.HandlerFactory = handlerFactory!;
        }

        return model;
    }

    // ------------------------------------------------------------ shape

    /// <summary>形状矩阵。无 RequestType：() / (ct) / (ctx) / (ctx, ct)；有 RequestType：
    /// (req) / (req, ct) —— 首参必须等于 RequestType。返回档都是 void / Task / ValueTask。
    /// 排出的 handler 工厂是方法组；重载选择交给运行期 JobHandlerBinder（九种无参 + 两种 RequestType）。</summary>
    static bool TryBuildHandler(IMethodSymbol method, ITypeSymbol? requestSymbol, out string? handlerFactory)
    {
        handlerFactory = null;

        if (!ReturnFits(method.ReturnType)) return false;

        var parameters = method.Parameters;
        var owner = method.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "";
        var group = $"{owner}.{method.Name}";

        if (requestSymbol is not null)
        {
            if (parameters.Length is < 1 or > 2) return false;
            if (!SymbolEqualityComparer.Default.Equals(parameters[0].Type, requestSymbol)) return false;
            if (parameters.Length == 2 && Display(parameters[1].Type) != CancellationTokenDisplay) return false;
            handlerFactory = $"global::QuickJob.JobHandlerBinder.From<{requestSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>({group})";
            return true;
        }

        switch (parameters.Length)
        {
            case 0:
                handlerFactory = Bind(group);
                return true;
            case 1:
            {
                var first = Display(parameters[0].Type);
                if (first is CancellationTokenDisplay or JobRunContextDisplay)
                {
                    handlerFactory = Bind(group);
                    return true;
                }
                return false;
            }
            case 2:
            {
                var first = Display(parameters[0].Type);
                var second = Display(parameters[1].Type);
                if (first == JobRunContextDisplay && second == CancellationTokenDisplay)
                {
                    handlerFactory = Bind(group);
                    return true;
                }
                return false;
            }
            default:
                return false;
        }

        static string Bind(string group) => $"global::QuickJob.JobHandlerBinder.From({group})";
    }

    static bool ReturnFits(ITypeSymbol returnType)
    {
        if (returnType.SpecialType == SpecialType.System_Void) return true;
        var display = Display(returnType);
        return display is TaskDisplay or ValueTaskDisplay;
    }

    static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // ------------------------------------------------------- attribute rows

    /// <summary>构造参数里剥出 job 名（非法/缺失按空串，后面 QJ0005 会拦重复）。</summary>
    static string FirstCtorString(AttributeData attr)
    {
        if (attr.ConstructorArguments.Length == 0) return "";
        var arg = attr.ConstructorArguments[0];
        return arg.Value as string ?? "";
    }

    static TypedConstant? Find(ImmutableArray<KeyValuePair<string, TypedConstant>> named, string key)
    {
        for (var i = 0; i < named.Length; i++)
        {
            if (named[i].Key == key) return named[i].Value;
        }
        return null;
    }

    static string? TextRow(ImmutableArray<KeyValuePair<string, TypedConstant>> named, string key)
        => Find(named, key) is { } row ? row.Value as string : null;

    static int IntRow(ImmutableArray<KeyValuePair<string, TypedConstant>> named, string key, int fallback)
    {
        if (Find(named, key) is not { } row) return fallback;
        return row.Value switch
        {
            int i => i,
            long l => unchecked((int)l),
            short s => s,
            byte b => b,
            _ => fallback,
        };
    }

    static double DoubleRow(ImmutableArray<KeyValuePair<string, TypedConstant>> named, string key, double fallback)
    {
        if (Find(named, key) is not { } row) return fallback;
        return row.Value switch
        {
            double d => d,
            int i => i,
            float f => f,
            long l => l,
            _ => fallback,
        };
    }

    static bool BoolRow(ImmutableArray<KeyValuePair<string, TypedConstant>> named, string key, bool fallback)
    {
        if (Find(named, key) is not { } row) return fallback;
        return row.Value is bool b ? b : fallback;
    }

    /// <summary>枚举常量 → 运行期枚举成员名（生成端写 <c>global::QuickJob.RetryBackoff.Linear</c> 这类全名）。
    /// 不是枚举常量（没写）返回 null：发射端跳过，记录默认顶上。</summary>
    static string? EnumRow(ImmutableArray<KeyValuePair<string, TypedConstant>> named, string key)
    {
        if (Find(named, key) is not { Type: INamedTypeSymbol enumType } row) return null;
        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol { ConstantValue: { } raw } field && object.Equals(raw, row.Value))
                return field.Name;
        }
        return null;
    }

    // --------------------------------------------------------------- duration

    static void ValidateDuration(JobModel model, string optionName, string? text, bool needsPositive = false)
    {
        if (text is null) return;
        if (!DurationShape.TryParse(text, out var value))
        {
            model.Diagnostics.Add(QuickJobDiagnostics.DurationUnusableAt(model.Location, optionName, text));
            return;
        }
        if (needsPositive && value <= TimeSpan.Zero)
        {
            model.Diagnostics.Add(QuickJobDiagnostics.EveryNotPositiveAt(model.Location, text));
        }
    }

    // --------------------------------------------------------------- helpers

    static string ShortForm(IMethodSymbol method)
    {
        var owner = method.ContainingType is null ? "" : method.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return owner.Length == 0 ? method.Name : owner + "." + method.Name;
    }

    static string ShortTypeForm(INamedTypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    static IEnumerable<INamedTypeSymbol> TypeChain(INamedTypeSymbol? containing)
    {
        for (var t = containing; t is not null; t = t.ContainingType) yield return t;
    }
}
