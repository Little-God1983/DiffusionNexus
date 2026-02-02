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
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Force software rendering to diagnose GPU issues
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software]
            })
            .WithInterFont()
            .LogToTrace();
}
