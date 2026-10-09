using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>What a folder-model download did.</summary>
/// <param name="Downloaded">Files fetched in this call.</param>
/// <param name="Failures">One line per file that is still missing or the wrong size afterwards.</param>
public sealed record EngineFolderModelDownloadResult(int Downloaded, IReadOnlyList<string> Failures)
{
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>Downloads an <see cref="EngineFolderModel"/> into the Engine.</summary>
public interface IEngineFolderModelDownloader
{
    Task<EngineFolderModelDownloadResult> DownloadAsync(
        string engineRoot,
        EngineFolderModel model,
        IProgress<DownloadProgress>? downloadProgress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Fetches only the files of a folder model that are missing or the wrong size, through the SDK's
/// <see cref="FileDownloader"/> (web-page rejection, partial-file cleanup on failure or cancel).
/// A file of the wrong size is deleted first, because the downloader skips any file that exists.
/// Every file is checked by size afterwards.
/// </summary>
public sealed class EngineFolderModelDownloader : IEngineFolderModelDownloader
{
    private const string LogSource = "Diffusion Nexus Engine";
    private static readonly ILogger Logger = Log.ForContext<EngineFolderModelDownloader>();

    private readonly HttpClient _httpClient;
    private readonly IUnifiedLogger? _unifiedLogger;

    public EngineFolderModelDownloader(HttpClient httpClient, IUnifiedLogger? unifiedLogger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _unifiedLogger = unifiedLogger;
    }

    public async Task<EngineFolderModelDownloadResult> DownloadAsync(
        string engineRoot,
        EngineFolderModel model,
        IProgress<DownloadProgress>? downloadProgress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentNullException.ThrowIfNull(model);

        var missing = model.MissingFiles(engineRoot);
        if (missing.Count == 0)
        {
            Info($"{model.Name}: already complete in {model.FolderPath(engineRoot)}.");
            return new EngineFolderModelDownloadResult(0, []);
        }

        Info($"{model.Name}: downloading {missing.Count} of {model.Files.Count} file(s) " +
             $"({FormatGb(missing.Sum(f => f.Size))}) into {model.FolderPath(engineRoot)}.");

        var downloader = new FileDownloader(_httpClient);
        var downloaded = 0;
        var failures = new List<string>();

        foreach (var file in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = model.FilePath(engineRoot, file);
            if (File.Exists(path))
            {
                Warn($"{model.Name}: {file.Path} is {new FileInfo(path).Length} bytes, expected {file.Size}; downloading it again.");
                File.Delete(path);
            }

            var result = await downloader.DownloadSingleFileAsync(
                model.FileUrl(file), Path.GetDirectoryName(path)!, file.Path,
                verboseLogging: false, logProgress: null, downloadProgress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var info = new FileInfo(path);
            if (result.Succeeded && info.Exists && info.Length == file.Size)
            {
                downloaded++;
                Info($"{model.Name}: {file.Path} downloaded.");
                continue;
            }

            var reason = !result.Succeeded
                ? result.ErrorMessage ?? result.Outcome.ToString()
                : info.Exists
                    ? $"it is {info.Length} bytes, expected {file.Size}"
                    : "the file is not where it was expected";
            if (result.Succeeded && info.Exists)
                TryDelete(path);
            failures.Add($"{file.Path}: {reason}");
            Warn($"{model.Name}: {file.Path} failed — {reason}");
        }

        Info(failures.Count == 0
            ? $"{model.Name}: complete."
            : $"{model.Name}: {failures.Count} file(s) failed.");
        return new EngineFolderModelDownloadResult(downloaded, failures);
    }

    private static string FormatGb(long bytes) => $"{bytes / 1_000_000_000d:0.0} GB";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Logger.Warning(ex, "Could not delete {Path}", path); }
    }

    private void Info(string message)
    {
        Logger.Information("Engine folder model: {Message}", message);
        _unifiedLogger?.Info(LogCategory.Installation, LogSource, message);
    }

    private void Warn(string message)
    {
        Logger.Warning("Engine folder model: {Message}", message);
        _unifiedLogger?.Warn(LogCategory.Installation, LogSource, message);
    }
}
