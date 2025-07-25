using System;
using System.IO;
using System.Reflection;
using Avalonia;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.Service.Classes;


namespace DiffusionNexus.UI
{
    internal sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            ConfigureFFmpeg();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        private static void ConfigureFFmpeg()
        {
            try
            {
                // Get the directory where the exe is located
                var exeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(exeDirectory))
                {
                    throw new InvalidOperationException("Could not determine executable directory");
                }

                var ffmpegDirectory = Path.Combine(exeDirectory, "ffmpeg");
                
                // Ensure the ffmpeg directory exists
                if (!Directory.Exists(ffmpegDirectory))
                {
                    Directory.CreateDirectory(ffmpegDirectory);
                }
                
                // Check if FFmpeg binaries already exist locally
                var ffmpegExe = Path.Combine(ffmpegDirectory, "ffmpeg.exe");
                var ffprobeExe = Path.Combine(ffmpegDirectory, "ffprobe.exe");
                
                if (!File.Exists(ffmpegExe) || !File.Exists(ffprobeExe))
                {
                    // Log that we're downloading FFmpeg
                    Console.WriteLine($"Downloading FFmpeg to: {ffmpegDirectory}");
                    
                    // Download FFmpeg to the local directory
                    FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, ffmpegDirectory).GetAwaiter().GetResult();
                    
                    Console.WriteLine("FFmpeg download completed");
                }
                else
                {
                    Console.WriteLine($"FFmpeg binaries found at: {ffmpegDirectory}");
                }
                
                // Set the executable path so Xabe.FFmpeg knows where to find the binaries
                FFmpeg.SetExecutablesPath(ffmpegDirectory);
                Console.WriteLine($"FFmpeg executable path set to: {ffmpegDirectory}");
                
                // Verify the executables are accessible
                if (File.Exists(ffmpegExe) && File.Exists(ffprobeExe))
                {
                    Console.WriteLine("FFmpeg configuration successful - binaries are accessible");
                }
                else
                {
                    Console.WriteLine("Warning: FFmpeg binaries not found after configuration");
                }
            }
            catch (Exception ex)
            {
                // Fallback: try to download to default location
                try
                {
                    Console.WriteLine($"Primary FFmpeg configuration failed: {ex.Message}. Trying fallback...");
                    
                    FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official).GetAwaiter().GetResult();
                    
                    // Try to find the downloaded FFmpeg in common locations
                    var possiblePaths = GetPossibleFFmpegPaths();
                    
                    foreach (var path in possiblePaths)
                    {
                        if (Directory.Exists(path) && File.Exists(Path.Combine(path, "ffmpeg.exe")) && File.Exists(Path.Combine(path, "ffprobe.exe")))
                        {
                            FFmpeg.SetExecutablesPath(path);
                            Console.WriteLine($"FFmpeg found and configured at fallback location: {path}");
                            return;
                        }
                    }
                    
                    // If we still can't find it, log the error but continue
                    Console.WriteLine($"Warning: Could not locate FFmpeg binaries after download. FFmpeg functionality may not work. Error: {ex.Message}");
                }
                catch (Exception fallbackEx)
                {
                    Console.WriteLine($"Error: Failed to download or configure FFmpeg: {fallbackEx.Message}");
                }
            }
        }

        private static string[] GetPossibleFFmpegPaths()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var tempPath = Path.GetTempPath();
            
            return new[]
            {
                Path.Combine(userProfile, ".nuget", "packages", "xabe.ffmpeg.downloader"),
                Path.Combine(localAppData, "FFmpeg"),
                Path.Combine(tempPath, "FFmpeg"),
                Path.Combine(localAppData, "Xabe.FFmpeg"),
                Path.Combine(tempPath, "Xabe.FFmpeg")
            };
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
