namespace QuickJob.Dashboard;

/// <summary>Dashboard 登录档：完全开放，或通用 OAuth2 授权码流（aspnetcore 自带，不带 OIDC 发现）。</summary>
public enum DashboardAuthMode
{
    /// <summary>无登录墙（本机开发档）。</summary>
    Anonymous = 0,

    /// <summary>OAuth2 授权码流：四个端点 + cookie 会话。Keycloak / Auth0 / Entra 等都有裸 OAuth2
    /// 端点，直接填上就能走（共享框架里没有 OIDC 发现那一颗，.NET 10 起 OIDC 处理机被移出了
    /// Microsoft.AspNetCore.App —— 填包违背家规，所以走通用 code 流）。</summary>
    OAuth2 = 1,
}

/// <summary>
/// Dashboard 选项。登录走 aspnetcore 共享框架自带的 <c>AddOpenIdConnect</c>/<c>AddOAuth</c> +
/// cookie —— 包本身不引第三方 NuGet。
/// </summary>
public sealed class QuickJobDashboardOptions
{
    /// <summary>挂载路径前缀（默认 "/quickjob"）。所有端点在 <c>prefix/…</c> 下；登录回调在
    /// <c>prefix/auth/callback</c> —— 把这个串填进提供方的 redirect_uri。</summary>
    public string PathPrefix { get; set; } = "/quickjob";

    /// <summary>登录档。默认 Anonymous。</summary>
    public DashboardAuthMode AuthMode { get; set; } = DashboardAuthMode.Anonymous;

    /// <summary>登录应用 id。</summary>
    public string? ClientId { get; set; }

    /// <summary>登录应用密钥。</summary>
    public string? ClientSecret { get; set; }

    /// <summary>OAuth2 向提供方要的作用域。默认 ["profile","email","openid"]。</summary>
    public IReadOnlyList<string> Scopes { get; set; } = ["profile", "email", "openid"];

    /// <summary>OAuth2：授权端点（如 Keycloak 的 /realms/x/protocol/openid-connect/auth）。</summary>
    public string? AuthorizationEndpoint { get; set; }

    /// <summary>OAuth2：token 端点。</summary>
    public string? TokenEndpoint { get; set; }

    /// <summary>OAuth2：用户信息端点（登录后的 claims 从这取）。</summary>
    public string? UserInformationEndpoint { get; set; }

    /// <summary>登录后的准入判断（拿 email/用户名判是否放行）。null = 登录即放。</summary>
    public Func<string?, bool>? UserIsAllowed { get; set; }

    /// <summary>cookie 会话时长。默认 8 小时。</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>只读档：挡掉一切操作端点（trigger/pause/…），只留观察面。默认 false。</summary>
    public bool ReadOnly { get; set; }

    /// <summary>规范化的前缀（去尾斜杠、至少一级）。</summary>
    internal string MountRoot
    {
        get
        {
            var root = PathPrefix?.Trim() ?? "";
            if (!root.StartsWith('/')) root = "/" + root;
            return root.TrimEnd('/').Length == 0 ? "/quickjob" : root.TrimEnd('/');
        }
    }

    /// <summary>OAuth 登录回调相对路径（<see cref="MountRoot"/> + /auth/callback）。</summary>
    internal string CallbackPath => MountRoot + "/auth/callback";
}
