using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace QuickJob.Generators;

/// <summary>一颗 [QuickJob] 方法，按生成器的视角：属性值全量搬运 + handler 工厂的表达式文本。
/// netstandard2.0 不带 init 安全，就干脆普通类 + 字段。</summary>
sealed class JobModel
{
    internal string Name = "";
    internal Location Location = Location.None;

    /// <summary>Handler 工厂的完整表达式："() => global::QuickJob.JobHandlerBinder.From(global::…)"。</summary>
    internal string HandlerFactory = "";

    internal string? Cron;
    internal string? Every;
    internal string? TimeZone;

    /// <summary>RequestType 的全名显示串（global:: 形式）；null 表示没写。</summary>
    internal string? RequestType;

    /// <summary>声明处展示全名「命名空间.类型链.方法」—— 看板详情面的来源栏；恒可得，不参与诊断。</summary>
    internal string Source = "";

    internal int MaxRetries;
    internal string? RetryBackoff;
    internal string? RetryBaseDelay;
    internal string? RetryMaxDelay;
    internal double RetryJitter;
    internal int MaxConcurrentPerJob;
    internal string? OnOverlap;
    internal int OverlapQueueLimit;
    internal string? Priority;
    internal string? OnMissedSlot;
    internal string? Timeout;
    internal string? Grace;
    internal string? Description;
    internal bool Enabled;

    /// <summary>所有收集到的诊断（Error/Warning）。</summary>
    internal readonly List<Diagnostic> Diagnostics = [];

    /// <summary>没有任何 Error 才进发射名单。</summary>
    internal bool Eligible
    {
        get
        {
            foreach (var d in Diagnostics)
                if (d.Severity == DiagnosticSeverity.Error) return false;
            return true;
        }
    }
}
