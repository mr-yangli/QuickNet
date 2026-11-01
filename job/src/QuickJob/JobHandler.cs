namespace QuickJob;

/// <summary>
/// The shape every job body compiles to. <see cref="JobRunContext.Token"/> carries the cooperative
/// cancellation signals (scheduler stop, per-run timeout, explicit cancel); <see cref="JobRunContext.Payload"/>
/// carries an optional UTF-8 JSON payload. Both the fluent DSL and the source generator funnel
/// method shapes into this one delegate.
/// </summary>
public delegate ValueTask JobHandler(JobRunContext ctx);

/// <summary>Conversions from the natural C# method shapes to <see cref="JobHandler"/> — the DSL's
/// overloads route here, and the generated registration code calls these for its typed-handler shapes.</summary>
public static class JobHandlerBinder
{
    /// <summary>Zero-arg sync.</summary>
    public static JobHandler From(Action body) => ctx =>
    {
        body();
        return ValueTask.CompletedTask;
    };

    /// <summary>Zero-arg async.</summary>
    public static JobHandler From(Func<Task> body) => async ctx => await body();

    /// <summary>Zero-arg ValueTask.</summary>
    public static JobHandler From(Func<ValueTask> body) => ctx => body();

    /// <summary>Cancellation-aware sync.</summary>
    public static JobHandler From(Action<CancellationToken> body) => ctx =>
    {
        body(ctx.Token);
        return ValueTask.CompletedTask;
    };

    /// <summary>Cancellation-aware async.</summary>
    public static JobHandler From(Func<CancellationToken, Task> body) => async ctx => await body(ctx.Token);

    /// <summary>Cancellation-aware ValueTask.</summary>
    public static JobHandler From(Func<CancellationToken, ValueTask> body) => ctx => body(ctx.Token);

    /// <summary>Context-only.</summary>
    public static JobHandler From(Action<JobRunContext> body) => ctx =>
    {
        body(ctx);
        return ValueTask.CompletedTask;
    };

    /// <summary>Context async.</summary>
    public static JobHandler From(Func<JobRunContext, Task> body) => async ctx => await body(ctx);

    /// <summary>Context ValueTask — the canonical shape; identity when already <see cref="JobHandler"/>.</summary>
    public static JobHandler From(Func<JobRunContext, ValueTask> body) => ctx => body(ctx);

    /// <summary>Context + token sync.</summary>
    public static JobHandler From(Action<JobRunContext, CancellationToken> body) => ctx =>
    {
        body(ctx, ctx.Token);
        return ValueTask.CompletedTask;
    };

    /// <summary>Context + token async.</summary>
    public static JobHandler From(Func<JobRunContext, CancellationToken, Task> body) => async ctx =>
        await body(ctx, ctx.Token);

    /// <summary>Context + token ValueTask.</summary>
    public static JobHandler From(Func<JobRunContext, CancellationToken, ValueTask> body) => ctx => body(ctx, ctx.Token);

    /// <summary>Identity passthrough, wired so interactive API (builder) and generated code resolve uniformly.</summary>
    public static JobHandler Wrap(JobHandler handler) => handler;

    /// <summary>Typed-payload shape (payload JSON deserialises into the request; null payload → null request).</summary>
    public static JobHandler From<TRequest>(Func<TRequest?, CancellationToken, Task> body) => async ctx =>
        await body(ctx.PayloadAs<TRequest>(), ctx.Token);

    /// <summary>Typed-payload + context shape.</summary>
    public static JobHandler From<TRequest>(Func<JobRunContext, TRequest?, Task> body) => async ctx =>
        await body(ctx, ctx.PayloadAs<TRequest>());

    /// <summary>Typed-payload ValueTask shape (source generator emits this for ValueTask handlers).</summary>
    public static JobHandler From<TRequest>(Func<TRequest?, CancellationToken, ValueTask> body) => ctx =>
        body(ctx.PayloadAs<TRequest>(), ctx.Token);
}
