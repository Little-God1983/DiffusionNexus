using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services;

public class CivitaiModelDownloadService
{
    private readonly CivitaiModelService _modelService;
    private readonly CivitaiFileDownloader _fileDownloader;

    public CivitaiModelDownloadService(CivitaiModelService modelService, CivitaiFileDownloader fileDownloader)
    {
        _modelService = modelService;
        _fileDownloader = fileDownloader;
    }

    public async Task<CivitaiModelDownloadResult> DownloadModelAsync(
        string civitaiUrl,
        string targetFolder,
        string apiKey,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_modelService.TryParseModelUrl(civitaiUrl, out var reference))
        {
            return new CivitaiModelDownloadResult(ModelDownloadResultType.Error, ErrorMessage: "Invalid Civitai link.");
        }

        CivitaiModelVersionInfo versionInfo;
        try
        {
            versionInfo = await _modelService.GetModelVersionInfoAsync(reference, apiKey, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new CivitaiModelDownloadResult(ModelDownloadResultType.Cancelled);
        }
        catch (Exception ex)
        {
            return new CivitaiModelDownloadResult(ModelDownloadResultType.Error, ErrorMessage: ex.Message);
        }

        if (versionInfo.Files.Count == 0)
        {
            return new CivitaiModelDownloadResult(ModelDownloadResultType.Error, ErrorMessage: "No downloadable files found for this model version.");
        }

        var file = SelectBestFile(versionInfo);
        if (file == null)
        {
            return new CivitaiModelDownloadResult(ModelDownloadResultType.Error, ErrorMessage: "No compatible model file found for download.");
        }

        var fileName = string.IsNullOrWhiteSpace(file.Name)
            ? $"{SanitizeFileName(versionInfo.VersionName)}.safetensors"
            : file.Name;

        var destinationPath = Path.Combine(targetFolder, fileName);

        if (File.Exists(destinationPath))
        {
            return new CivitaiModelDownloadResult(ModelDownloadResultType.AlreadyExists, destinationPath, versionInfo, file);
        }

        Directory.CreateDirectory(targetFolder);

        try
        {
            var uri = new Uri(file.DownloadUrl);
            await _fileDownloader.DownloadAsync(uri, destinationPath, apiKey, progress, cancellationToken);
            return new CivitaiModelDownloadResult(ModelDownloadResultType.Success, destinationPath, versionInfo, file);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(destinationPath);
            return new CivitaiModelDownloadResult(ModelDownloadResultType.Cancelled, VersionInfo: versionInfo, FileInfo: file);
        }
        catch (HttpRequestException ex)
        {
            SafeDelete(destinationPath);
            return new CivitaiModelDownloadResult(ModelDownloadResultType.Error, ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            SafeDelete(destinationPath);
            return new CivitaiModelDownloadResult(ModelDownloadResultType.Error, ErrorMessage: ex.Message);
        }
    }

    private static CivitaiModelFileInfo? SelectBestFile(CivitaiModelVersionInfo versionInfo)
    {
        return versionInfo.Files
            .OrderByDescending(f => f.IsPrimary)
            .ThenByDescending(f => IsPreferredExtension(f.Name))
            .ThenByDescending(f => string.Equals(f.Format, "safetensor", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.SizeBytes ?? 0)
            .FirstOrDefault();
    }

    private static bool IsPreferredExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".safetensors", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "model" : sanitized;
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }
}
