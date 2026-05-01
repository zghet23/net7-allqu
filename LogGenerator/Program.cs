using LogGenerator.Components;
using LogGenerator.Services;
using Serilog;
using Serilog.Formatting.Compact;

namespace LogGenerator;

public class Program
{
    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(new RenderedCompactJsonFormatter())
            .Enrich.FromLogContext()
            .Enrich.WithProperty("app", "log-generator")
            .Enrich.WithProperty("env", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "production")
            .CreateLogger();

        try
        {
            Log.Information("Starting LogGenerator application");
            var builder = WebApplication.CreateBuilder(args);

            builder.Host.UseSerilog();

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.AddSingleton<LogService>();
            builder.Services.AddHttpClient<ExternalCallService>(c =>
            {
                c.Timeout = TimeSpan.FromSeconds(15);
                c.DefaultRequestHeaders.Add("User-Agent", "LogGenerator-ECSFargate/1.0");
            });

            builder.Services.AddHealthChecks();

            var app = builder.Build();

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStaticFiles();
            app.UseAntiforgery();

            app.MapHealthChecks("/health");

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
