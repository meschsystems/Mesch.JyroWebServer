using Mesch.JyroWebServer.Middleware;
using Mesch.JyroWebServer.Services;

namespace Mesch.JyroWebServer;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddHttpContextAccessor();

        builder.Services.AddJyroScriptServices(builder.Configuration);

        var app = builder.Build();

        // Razor template middleware - handles Jyro scripts with Razor template rendering
        // Must be before JyroScriptMiddleware to intercept first
        app.UseMiddleware<RazorTemplateMiddleware>();

        // Jyro script middleware - must be before routing to intercept requests
        app.UseMiddleware<JyroScriptMiddleware>();

        app.Run();
    }
}