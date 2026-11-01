namespace QuickJob.Dashboard;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Headers;
using System.Security.Claims;

/// <summary>
/// 建组：先 <see cref="AddQuickJobDashboard"/>（选项 + 登录档注册），宿主管线里
/// <see cref="MapQuickJobDashboard"/>（端点挂载）。QuickJobScheduler 本体仍走 AddQuickJob。
/// 登录档用 aspnetcore 共享框架自带的 <c>AddOAuth</c>（通用授权码流）+ cookie —— 包本身不引第三方 NuGet。
/// </summary>
public static class QuickJobDashboardMount
{
    const string CookieScheme = "QuickJob.Cookie";
    const string OAuthScheme = "QuickJob.OAuth";

    /// <summary>注册看板选项与登录档。OAuth2 档下 cookie 是登录会话，回调路径 <c>prefix/auth/callback</c>
    /// —— 把这个串填进提供方的 redirect_uri。</summary>
    public static IServiceCollection AddQuickJobDashboard(
        this IServiceCollection services,
        Action<QuickJobDashboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new QuickJobDashboardOptions();
        configure?.Invoke(options);

        services.AddOptions<QuickJobDashboardOptions>().Configure(o =>
        {
            o.PathPrefix = options.PathPrefix;
            o.AuthMode = options.AuthMode;
            o.ClientId = options.ClientId;
            o.ClientSecret = options.ClientSecret;
            o.Scopes = options.Scopes;
            o.AuthorizationEndpoint = options.AuthorizationEndpoint;
            o.TokenEndpoint = options.TokenEndpoint;
            o.UserInformationEndpoint = options.UserInformationEndpoint;
            o.UserIsAllowed = options.UserIsAllowed;
            o.SessionLifetime = options.SessionLifetime;
            o.ReadOnly = options.ReadOnly;
        });

        if (options.AuthMode == DashboardAuthMode.OAuth2)
        {
            services.AddAuthentication(setup =>
            {
                setup.DefaultScheme = CookieScheme;
                setup.DefaultChallengeScheme = OAuthScheme;
            })
            .AddCookie(CookieScheme, CookieShape(options))
            .AddOAuth(OAuthScheme, o =>
            {
                o.ClientId = options.ClientId ?? throw new QuickJob.Dashboard.OptionsFaultException("ClientId");
                o.ClientSecret = options.ClientSecret ?? throw new QuickJob.Dashboard.OptionsFaultException("ClientSecret");
                o.CallbackPath = new(options.CallbackPath);
                o.AuthorizationEndpoint = options.AuthorizationEndpoint ?? throw new QuickJob.Dashboard.OptionsFaultException("AuthorizationEndpoint");
                o.TokenEndpoint = options.TokenEndpoint ?? throw new QuickJob.Dashboard.OptionsFaultException("TokenEndpoint");
                o.UserInformationEndpoint = options.UserInformationEndpoint ?? throw new QuickJob.Dashboard.OptionsFaultException("UserInformationEndpoint");
                o.SaveTokens = true;
                foreach (var scope in ScopeOf(options)) o.Scope.Add(scope);
                o.Events = new OAuthEvents
                {
                    // 登录落票时把用户信息端点的 claims 灌进主体；门禁不过就不掺。
                    OnCreatingTicket = async ctx =>
                    {
                        if (ctx.AccessToken is { } token)
                        {
                            ctx.Backchannel.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        }

                        var identity = await PullClaimsAsync(ctx);
                        var claimValue = identity.FindFirst("email")?.Value ?? identity.FindFirst("sub")?.Value;
                        if (options.UserIsAllowed is not null && !options.UserIsAllowed(claimValue))
                        {
                            ctx.HttpContext.Response.Redirect(options.MountRoot + "/?denied=1");
                            return;
                        }
                        ctx.Principal?.AddIdentity(identity);
                    },
                };
            });

            services.AddAuthorization();
        }

        // 看板内部登记：事件环（挂载时订阅）
        services.TryAddSingleton<EventRing>();
        return services;
    }

    /// <summary>把看板端点挂上宿主；scheduler 从 DI 取（AddQuickJob 注册的那颗）。</summary>
    public static void MapQuickJobDashboard(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<QuickJobDashboardOptions>>().Value;
        var scheduler = app.Services.GetRequiredService<QuickJobScheduler>();
        app.MapQuickJobDashboard(scheduler, options);
    }

    /// <summary>把看板端点挂上宿主（scheduler/选项手工给）。</summary>
    public static void MapQuickJobDashboard(this WebApplication app, QuickJobScheduler scheduler, QuickJobDashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(scheduler);
        var root = options.MountRoot;

        // 事件环：进程内订阅一次，容量 200。
        var ring = app.Services.GetRequiredService<EventRing>();
        if (!ring.Wired) ring.Wire(scheduler);

        var authWall = options.AuthMode != DashboardAuthMode.Anonymous;
        if (authWall)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        DashboardEndpoints.Map(app, root, scheduler, options, ring, authWall);
    }

    // ------------------------------------------------------------- helpers

    static Action<CookieAuthenticationOptions> CookieShape(QuickJobDashboardOptions options) => o =>
    {
        o.Cookie.Name = ".quickjob.dash";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = options.SessionLifetime < TimeSpan.FromMinutes(5) ? TimeSpan.FromHours(8) : options.SessionLifetime;
    };

    static IEnumerable<string> ScopeOf(QuickJobDashboardOptions options)
        => options.Scopes is { Count: > 0 } list ? list : ["profile", "email", "openid"];

    /// <summary>用户信息端点 → claims 行。端点拒绝/字段缺失都只当"少个 claim"，不再炸登录。</summary>
    static async Task<ClaimsIdentity> PullClaimsAsync(OAuthCreatingTicketContext ctx)
    {
        var identity = new ClaimsIdentity(OAuthScheme);
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await ctx.Backchannel.SendAsync(request, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return identity;

            var payload = await response.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted).ConfigureAwait(false);
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            foreach (var claim in document.RootElement.EnumerateObject())
            {
                var value = claim.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? claim.Value.GetString()
                    : claim.Value.ToString();
                if (value is null) continue;
                // 提供方差异收编：github 的 login / OIDC 的 preferred_username / 自家的 id 都抬成 sub
                var name = claim.Name switch
                {
                    "id" or "login" or "preferred_username" => "sub",
                    _ => claim.Name,
                };
                if (name is "sub" or "email" or "name") identity.AddClaim(new Claim(name, value));
            }
        }
        catch
        {
            // 用户信息面炸了不该挡登录：交给 UserIsAllowed 兑现
        }
        return identity;
    }
}

/// <summary>OAuth2 档的配置缺口：AddQuickJobDashboard 装配时立刻点名（不是等第一次登录才炸）。</summary>
public sealed class OptionsFaultException(string slot)
    : QuickJobException($"[QuickJob dashboard] '{slot}' is missing — OAuth2 auth mode needs ClientId/ClientSecret and the three endpoints");

/// <summary>事件环：dashboard 的 /events 读这一颗。容量 200 的环形缓冲，回落丢最旧。</summary>
public sealed class EventRing
{
    const int Capacity = 200;
    private readonly object _gate = new();
    private readonly QuickJobEvent[] _items = new QuickJobEvent[Capacity];
    private int _count;
    private int _next;
    internal bool Wired { get; private set; }
    private IDisposable? _sub;

    /// <summary>接线（幂等）。</summary>
    internal void Wire(QuickJobScheduler scheduler)
    {
        lock (_gate)
        {
            if (Wired) return;
            Wired = true;
            _sub = scheduler.Events.Observe(e =>
            {
                lock (_gate)
                {
                    _items[_next] = e;
                    _next = (_next + 1) % Capacity;
                    if (_count < Capacity) _count++;
                }
            });
        }
    }

    /// <summary>最近的事件，最新在前。</summary>
    public IReadOnlyList<QuickJobEvent> Recent(int take)
    {
        lock (_gate)
        {
            take = Math.Clamp(take, 1, Capacity);
            var list = new List<QuickJobEvent>(Math.Min(take, _count));
            for (var i = 0; i < Math.Min(take, _count); i++)
            {
                var idx = ((_next - 1 - i) + Capacity) % Capacity;
                list.Add(_items[idx]!);
            }
            return list;
        }
    }
}
