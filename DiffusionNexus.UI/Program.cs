using Avalonia;
using Avalonia.Controls;
using DiffusionNexus.UI.Services;
using Serilog;

namespace DiffusionNexus.UI;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffusionNexus",
            "Logs");

        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDirectory, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("DiffusionNexus application starting...");
            FileLogger.Log("Application starting via Program.Main");
            
            Log.Information("Building Avalonia app...");
            var app = BuildAvaloniaApp();
            
            Log.Information("Starting desktop lifetime...");
            app.StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application crashed unexpectedly");
            FileLogger.LogError("FATAL CRASH in Program.Main", ex);
            throw;
        }
        finally
        {
            Log.Information("DiffusionNexus application shutting down");
            FileLogger.Log("Application shutting down");
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        // Hardware rendering by default (ANGLE, with Avalonia's built-in software
        // fallback). DIFFUSIONNEXUS_SOFTWARE_RENDERING=1 forces the software
        // compositor on machines with broken GPU drivers.
        // TODO: Linux Implementation — the override below is Win32-specific.
        if (Startup.RenderingConfig.UseSoftwareRendering(Environment.GetEnvironmentVariable))
        {
            Log.Information("Rendering: software compositor forced via {EnvVar}",
                Startup.RenderingConfig.SoftwareRenderingEnvVar);
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software]
            });
        }

        return builder;
    }
}
