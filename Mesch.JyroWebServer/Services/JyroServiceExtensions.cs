using Mesch.Jyro;

namespace Mesch.JyroWebServer.Services;

public static class JyroServiceExtensions
{
    /// <summary>
    /// Registers Jyro script execution services and configures execution options.
    /// </summary>
    public static IServiceCollection AddJyroScriptServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Configure Jyro execution options from appsettings
        services.Configure<JyroExecutionOptions>(options =>
        {
            var jyroConfig = configuration.GetSection("Jyro");

            // Load configuration values with defaults
            options.MaxExecutionTime = TimeSpan.FromSeconds(
                jyroConfig.GetValue<int>("MaxExecutionTimeSeconds", 10));

            options.MaxStatements = jyroConfig.GetValue<int>("MaxStatements", 50_000);
            options.MaxLoops = jyroConfig.GetValue<int>("MaxLoops", 5_000);
            options.MaxStackDepth = jyroConfig.GetValue<int>("MaxStackDepth", 512);
            options.MaxCallDepth = jyroConfig.GetValue<int>("MaxCallDepth", 128);
            options.MaxScriptCallDepth = jyroConfig.GetValue<int>("MaxScriptCallDepth", 10);
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
