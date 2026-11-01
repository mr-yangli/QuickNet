namespace QuickJob;

using Microsoft.Extensions.DependencyInjection;

/// <summary>MEDI registration: the scheduler as a singleton, built with the DSL callback at startup.</summary>
public static class QuickJobServiceCollectionExtensions
{
    /// <summary>
    /// Adds one <see cref="QuickJobScheduler"/> singleton, constructed immediately with
    /// <paramref name="configure"/>. Start it from your host's own <c>IHostedService</c> (see the
    /// docs snippet — QuickJob deliberately does not reference the hosting abstraction, keeping the
    /// package's one-dependency promise to match QuickSql.Core).
    /// </summary>
    public static IServiceCollection AddQuickJob(
        this IServiceCollection services,
        Action<QuickJobBuilder>? configure = null,
        Action<QuickJobOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new QuickJobOptions();
        configureOptions?.Invoke(options);
        var scheduler = QuickJobScheduler.Create(configure, options);
        services.AddSingleton(scheduler);
        return services;
    }
}
