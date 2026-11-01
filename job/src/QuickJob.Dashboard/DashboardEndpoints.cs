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
        // ---- 页面（壳；数据全部走下面的 JSON API）
        app.MapGet(root, () => Results.Text(DashboardAssets.Html, "text/html; charset=utf-8"));

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
        app.MapGet(root + "/runs/pending", () => Results.Json(scheduler.PendingRuns()));
        app.MapGet(root + "/runs/running", () => Results.Json(scheduler.RunningRuns()));
        app.MapGet(root + "/events", (int? take) => Results.Json(ring.Recent(take ?? 50)));

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
