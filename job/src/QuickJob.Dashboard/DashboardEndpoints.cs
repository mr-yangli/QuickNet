namespace QuickJob.Dashboard;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

/// <summary>看板端点与请求 DTO。观察面始终挂载；操作面在 ReadOnly 档整体缺席。</summary>
static class DashboardEndpoints
{
    public static void Map(
        WebApplication app,
        string root,
        QuickJobScheduler scheduler,
        QuickJobDashboardOptions options,
        EventRing ring,
        bool authWall)
    {
        // ---- 页面（壳；数据全部走下面的 JSON API）。
        // @ROOT@ 由 JS 侧 fetch 面认领 —— 看板不再要求挂根 "/"；壳整页只替换一次。
        var page = DashboardAssets.Html.Replace("@ROOT@", root);
        app.MapGet(root, () => Results.Text(page, "text/html; charset=utf-8"));

        // ---- 登录面（非匿名档才有）
        if (authWall)
        {
            app.MapGet(root + "/login", () => Results.Challenge(new AuthenticationProperties { RedirectUri = root }));
            app.MapGet(root + "/logout", () => Results.SignOut(new AuthenticationProperties { RedirectUri = root }));
        }

        app.MapGet(root + "/me", (HttpContext http) =>
        {
            var authed = options.AuthMode != DashboardAuthMode.Anonymous
                         && (http.User?.Identity?.IsAuthenticated ?? false);
            var name = http.User?.FindFirst("email")?.Value
                       ?? http.User?.FindFirst("preferred_username")?.Value
                       ?? http.User?.FindFirst("name")?.Value
                       ?? http.User?.Identity?.Name;
            return Results.Json(new
            {
                authenticated = authed,
                name = authed ? name : null,
                readOnly = options.ReadOnly,
                authMode = options.AuthMode.ToString(),
            });
        });

        // ---- 观察面
        app.MapGet(root + "/stats", () => Results.Json(scheduler.Stats));
        app.MapGet(root + "/jobs", () => Results.Json(scheduler.ListJobs()));
        // 待跑面两条来源：已认领卡在并发门上的（管线内存量）+ 未到站的（库存 due 队列）。
        // cron/interval 的下一槽不预置进库存（到点才 Put），所以后者只剩 定时一次/once/排队/重试等待 这类真·未来档。
        app.MapGet(root + "/runs/pending", async (CancellationToken ct) =>
        {
            var now = scheduler.Options.Time.GetUtcNow();
            var queued = scheduler.PendingRuns().Select(r => new
            {
                executionId = r.ExecutionId, jobName = r.JobName, attempt = r.Attempt, trigger = r.Trigger,
                scheduledAt = r.ScheduledAt, state = "queued",
            });
            var ahead = await scheduler.Options.Store.PeekDueRunsAsync(200, ct).ConfigureAwait(false);
            var scheduled = ahead.Where(d => d.DueAt > now).Select(d => new
            {
                executionId = d.RunId, jobName = d.JobName, attempt = d.Attempt, trigger = d.Origin,
                scheduledAt = d.DueAt, state = "scheduled",
            });
            return Results.Json(queued.Concat(scheduled).OrderBy(r => r.scheduledAt).ToList());
        });

        app.MapGet(root + "/runs/running", () => Results.Json(scheduler.RunningRuns()));
        // 事件是抽象基类的派生 record —— 按 IReadOnlyList<QuickJobEvent> 声明序列化时 STJ 只写基类的
        // Kind/At，JobName/Error/Delay 全丢（面板事件流只能显 "?"）。抬成 object 元素让它走运行时类型。
        app.MapGet(root + "/events", (int? take) => Results.Json(
            ring.Recent(take ?? 50).Select(static e => (object)e).ToList()));

        app.MapGet(root + "/executions", async (string? job, int? take, CancellationToken ct) =>
        {
            var store = scheduler.Options.Store;
            var rows = await store.ReadExecutionsAsync(
                new ExecutionQuery
                {
                    Take = Math.Clamp(take ?? 100, 1, 1000),
                    JobName = string.IsNullOrWhiteSpace(job) ? null : job!.Trim(),
                }, ct).ConfigureAwait(false);
            return Results.Json(rows);
        });

        // ---- 操作面（只读档缺席）
        if (!options.ReadOnly)
        {
            app.MapPost(root + "/trigger", async (TriggerRequest req, CancellationToken ct) =>
                await PostOpAsync(() => scheduler.TriggerNowAsync(req.Job, req.Payload, ct)).ConfigureAwait(false));

            app.MapPost(root + "/schedule", async (ScheduleRequest req, CancellationToken ct) =>
                await PostOpAsync(() => scheduler.EnqueueAsync(req.Job, req.Payload, req.At, ct)).ConfigureAwait(false));

            app.MapPost(root + "/pause", (JobRequest req) => JobOp(() => scheduler.Pause(req.Job)));
            app.MapPost(root + "/resume", (JobRequest req) => JobOp(() => scheduler.Resume(req.Job)));
            app.MapPost(root + "/remove", (JobRequest req) => JobOp(() => scheduler.Remove(req.Job)));

            app.MapPost(root + "/cancel", async (CancelRequest req, CancellationToken ct) =>
            {
                try
                {
                    var hit = await scheduler.CancelAsync(req.RunId, ct).ConfigureAwait(false);
                    return Results.Json(new { ok = hit });
                }
                catch (Exception ex)
                {
                    return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
                }
            });
        }
    }

    /// <summary>返回 (Accepted, RunId) 的操作统一落 JSON 信封。</summary>
    static Task<IResult> PostOpAsync(Func<Task<(bool Accepted, Guid? RunId)>> op) => ExecuteAsync(async () =>
    {
        var (accepted, runId) = await op().ConfigureAwait(false);
        return accepted
            ? Results.Json(new { ok = true, runId })
            : Results.Json(new { ok = false, error = "scheduler is not running, or the job went away" }, statusCode: StatusCodes.Status409Conflict);
    });

    static async Task<IResult> ExecuteAsync(Func<Task<IResult>> op)
    {
        try
        {
            return await op().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    static IResult JobOp(Action op)
    {
        try
        {
            op();
            return Results.Json(new { ok = true });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}

// ------------------------------------------------------------ request DTOs

/// <summary>POST /trigger 的请求体。payload 走 JSON 直通（保留原文结构）。</summary>
public sealed record TriggerRequest(string Job, JsonElement? Payload);

/// <summary>POST /schedule 的请求体：指定时刻入队一次。</summary>
public sealed record ScheduleRequest(string Job, DateTimeOffset At, JsonElement? Payload);

/// <summary>POST /pause|resume|remove 的请求体。</summary>
public sealed record JobRequest(string Job);

/// <summary>POST /cancel 的请求体。</summary>
public sealed record CancelRequest(Guid RunId);
