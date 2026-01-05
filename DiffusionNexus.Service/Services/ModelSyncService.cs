using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Entities;
using DiffusionNexus.DataAccess.Repositories;
using DiffusionNexus.Service.Classes;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.Service.Services;

public class ModelSyncService
{
    private readonly DiffusionNexusDbContext _context;
    private readonly ModelFileRepository _fileRepository;
    private readonly ICivitaiApiClient _apiClient;
    private readonly ModelDataImportService _importService;

    public ModelSyncService(
        DiffusionNexusDbContext context,
        ICivitaiApiClient apiClient,
        string apiKey = "")
    {
        _context = context;
        _fileRepository = new ModelFileRepository(context);
        _apiClient = apiClient;
        _importService = new ModelDataImportService(context);
    }

    public async Task<Model?> SyncLocalFileAsync(
        string filePath,
        string sha256Hash,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var existingFile = await _fileRepository.GetByLocalFilePathAsync(filePath);
        if (existingFile != null)
        {
            progress?.Report(new ProgressReport
            {
                StatusMessage = $"File already in database: {Path.GetFileName(filePath)}",
                LogLevel = LogSeverity.Info
            });
            return existingFile.ModelVersion.Model;
        }

        existingFile = await _fileRepository.GetBySHA256HashAsync(sha256Hash);
        if (existingFile != null)
        {
            existingFile.LocalFilePath = filePath;
            await _context.SaveChangesAsync(cancellationToken);
            progress?.Report(new ProgressReport
            {
                StatusMessage = $"Updated local path for known file: {Path.GetFileName(filePath)}",
                LogLevel = LogSeverity.Success
            });
            return existingFile.ModelVersion.Model;
        }

        try
        {
            progress?.Report(new ProgressReport
            {
                StatusMessage = $"Fetching metadata from Civitai API for {Path.GetFileName(filePath)}",
                LogLevel = LogSeverity.Info
            });

            string versionJson = await _apiClient.GetModelVersionByHashAsync(sha256Hash, string.Empty);
            var model = await _importService.ImportFromVersionResponseAsync(versionJson, cancellationToken);

            var modelFile = await _context.ModelFiles
                .FirstOrDefaultAsync(f => f.SHA256Hash == sha256Hash, cancellationToken);

            if (modelFile != null)
            {
                modelFile.LocalFilePath = filePath;
                await _context.SaveChangesAsync(cancellationToken);
            }

            progress?.Report(new ProgressReport
            {
                StatusMessage = $"Successfully synced: {Path.GetFileName(filePath)}",
                LogLevel = LogSeverity.Success
            });

            return model;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            progress?.Report(new ProgressReport
            {
                StatusMessage = $"Model not found on Civitai: {Path.GetFileName(filePath)}",
                LogLevel = LogSeverity.Warning
            });
            return null;
        }
    }

    public async Task<IEnumerable<Model>> GetAllModelsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Models
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TrainedWords)
            .Include(m => m.Tags)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<ModelFile>> GetLocalFilesAsync(CancellationToken cancellationToken = default)
    {
        return await _fileRepository.GetLocalFilesAsync();
    }
}
