using DiffusionNexus.Legacy.DataAccess.Data;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using DiffusionNexus.Service.Services;
using ModelMover.Core.Metadata;
using Serilog;
using System.Security.Cryptography;

namespace DiffusionNexus.Legacy.Service;

public class LocalFileImportService
{
    private readonly DiffusionNexusDbContext _context;
    private readonly ModelSyncService _syncService;
    private readonly ICivitaiApiClient _apiClient;

    public LocalFileImportService(DiffusionNexusDbContext context, ICivitaiApiClient apiClient, string apiKey = "")
    {
        _context = context;
        _apiClient = apiClient;
        _syncService = new ModelSyncService(context, apiClient, apiKey);
    }

    public async Task ImportDirectoryAsync(
        string directoryPath,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProgressReport
        {
            Percentage = 0,
            StatusMessage = $"Scanning directory: {directoryPath}",
            LogLevel = LogSeverity.Info
        });

        var modelFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories)
            .Where(f => StaticFileTypes.ModelExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        progress?.Report(new ProgressReport
        {
            Percentage = 0,
            StatusMessage = $"Found {modelFiles.Count} model files",
            LogLevel = LogSeverity.Info
        });

        int processed = 0;
        foreach (var filePath in modelFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                progress?.Report(new ProgressReport
                {
                    Percentage = (int)((processed / (double)modelFiles.Count) * 100),
                    StatusMessage = $"Processing: {Path.GetFileName(filePath)}",
                    LogLevel = LogSeverity.Info
                });

                await ImportFileAsync(filePath, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error importing file {FilePath}", filePath);
                progress?.Report(new ProgressReport
                {
                    StatusMessage = $"Error importing {Path.GetFileName(filePath)}: {ex.Message}",
                    LogLevel = LogSeverity.Error
                });
            }

            processed++;
        }

        progress?.Report(new ProgressReport
        {
            Percentage = 100,
            StatusMessage = $"Import complete. Processed {processed} files.",
            IsSuccessful = true,
            LogLevel = LogSeverity.Success
        });
    }

    public async Task ImportFileAsync(
        string filePath,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("File not found", filePath);
        }

        string hash = await Task.Run(() => ComputeSHA256(filePath), cancellationToken);

        var baseName = ModelMetadataUtils.ExtractBaseName(fileInfo.Name);
        var directory = fileInfo.Directory;
        var civitaiInfoFile = directory?.GetFiles($"{baseName}.civitai.info").FirstOrDefault();
        var jsonFile = directory?.GetFiles($"{baseName}.json").FirstOrDefault();

        if (civitaiInfoFile != null)
        {
            await ImportFromCivitaiInfoAsync(filePath, civitaiInfoFile.FullName, hash, progress, cancellationToken);
        }
        else if (jsonFile != null)
        {
            progress?.Report(new ProgressReport
            {
                StatusMessage = $"Found JSON metadata for {Path.GetFileName(filePath)}",
                LogLevel = LogSeverity.Info
            });
        }
        else
        {
            await _syncService.SyncLocalFileAsync(filePath, hash, progress, cancellationToken);
        }
    }

    private async Task ImportFromCivitaiInfoAsync(
        string filePath,
        string infoFilePath,
        string hash,
        IProgress<ProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(infoFilePath, cancellationToken);
        var importService = new ModelDataImportService(_context);

        try
        {
            await importService.ImportFromVersionResponseAsync(json, cancellationToken);
            await importService.UpdateLocalFilePathAsync(hash, filePath, cancellationToken);

            progress?.Report(new ProgressReport
            {
                StatusMessage = $"Imported from .civitai.info: {Path.GetFileName(filePath)}",
                LogLevel = LogSeverity.Success
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error importing from civitai.info for {FilePath}", filePath);
            progress?.Report(new ProgressReport
            {
                StatusMessage = $"Failed to import civitai.info for {Path.GetFileName(filePath)}",
                LogLevel = LogSeverity.Warning
            });
        }
    }

    private static string ComputeSHA256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return string.Concat(hash.Select(b => b.ToString("x2")));
    }
}
