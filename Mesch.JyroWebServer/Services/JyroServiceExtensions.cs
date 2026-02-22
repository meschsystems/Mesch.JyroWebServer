using Mesch.Jyro;

namespace Mesch.JyroWebServer.Services;

public static class JyroServiceExtensions
{
    /// <summary>
    /// Registers Jyro script execution services and configures execution options.
    /// </summary>
    public static IServiceCollection AddJyroScriptServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Register JyroExecutionOptions as a singleton constructed from configuration.
        // JyroExecutionOptions is an immutable F# record and cannot be used with services.Configure<T>().
        services.AddSingleton(sp =>
        {
            var jyroConfig = configuration.GetSection("Jyro");

            return new JyroExecutionOptions(
                maxExecutionTime: TimeSpan.FromSeconds(
                    jyroConfig.GetValue<int>("MaxExecutionTimeSeconds", 10)),
                maxStatements: jyroConfig.GetValue<int>("MaxStatements", 50_000),
                maxLoopIterations: jyroConfig.GetValue<int>("MaxLoopIterations", 5_000),
                maxCallDepth: jyroConfig.GetValue<int>("MaxCallDepth", 128));
        });

        // Register Jyro script cache service (singleton for file watcher)
        services.AddSingleton(serviceProvider =>
        {
            var environment = serviceProvider.GetRequiredService<IWebHostEnvironment>();
            var logger = serviceProvider.GetRequiredService<ILogger<JyroScriptCacheService>>();
            var scriptsPath = Path.Combine(environment.ContentRootPath, "Scripts");
            return new JyroScriptCacheService(scriptsPath, logger);
        });

        // Register Jyro script service
        services.AddSingleton<IJyroScriptService, JyroScriptService>();

        return services;
    }
}
