using Avalonia.Threading;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Downloads LoRA files from Civitai and persists their metadata to the local database.
/// </summary>
public sealed class LoraDownloadService
{
    private readonly ICivitaiClient? _civitaiClient;
    private readonly IAppSettingsService? _settingsService;
    private readonly IUnifiedLogger? _logger;

    public LoraDownloadService(
        ICivitaiClient? civitaiClient,
        IAppSettingsService? settingsService,
        IUnifiedLogger? logger)
    {
        _civitaiClient = civitaiClient;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Streams a Civitai LoRA file to disk and persists model/version/file metadata afterward.
    /// </summary>
    // TODO: Linux Implementation for LoRA download task
    public async Task DownloadFileAsync(
        string downloadUrl,
        string targetPath,
        CivitaiModelVersion civitaiVersion,
        string taskName,
        Action<double, string>? reportProgress = null,
        Action? completed = null,
        Action? failed = null,
        int? existingModelId = null,
        CancellationToken externalCancellationToken = default,
        bool reportToActivityLog = true)
    {
        var taskTracker = App.Services?.GetService<ITaskTracker>();
        // When the caller is the Civitai download queue, the IDownloadCoordinator
        // already aggregates per-task progress into the status bar. Letting this
        // service ALSO push to the activity log makes concurrent downloads fight
        // for the single global progress slot ("jumping around"). Suppress when
        // told to.
        var activityLog = reportToActivityLog ? App.Services?.GetService<IActivityLogService>() : null;
        using var taskHandle = taskTracker?.BeginTask(taskName, LogCategory.Download);

        activityLog?.StartDownloadProgress(taskName);

        // Link the caller's cancellation token (per-job cancel from the Civitai
        // browser queue) with the task tracker's token (queue-wide cancel via
        // ITaskTracker). Either one firing aborts the download.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            externalCancellationToken,
            taskHandle?.CancellationToken ?? CancellationToken.None);

        try
        {
            taskHandle?.ReportIndeterminate("Connecting...");

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var httpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true
            });
            httpClient.Timeout = TimeSpan.FromHours(2);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DiffusionNexus/1.0");

            var ct = linkedCts.Token;
            _logger?.Trace(LogCategory.Download, "LoraDownload",
                $"GET (no auth) {downloadUrl}");
            var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);

            _logger?.Debug(LogCategory.Download, "LoraDownload",
                $"First attempt HTTP {(int)response.StatusCode} for '{taskName}'");

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                // Capture the body once so we can include Civitai's own error message in
                // the diagnostic log (their JSON error often says "Model requires
                // membership" / "Buzz balance" / "NSFW disabled").
                string? firstBody = null;
                try { firstBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
                response.Dispose();

                _logger?.Debug(LogCategory.Download, "LoraDownload",
                    $"Auth required for '{taskName}' — looking up Civitai API key…",
                    string.IsNullOrEmpty(firstBody) ? null : $"First response body: {Truncate(firstBody, 500)}");

                var apiKey = await GetApiKeyAsync();
                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger?.Warn(LogCategory.Download, "LoraDownload",
                        $"Cannot retry '{taskName}': no Civitai API key configured. Add one in Settings.",
                        $"Initial response: HTTP 401/403\nUrl: {downloadUrl}");
                    response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                else
                {
                    taskHandle?.ReportIndeterminate("Retrying with API key...");
                    var separator = downloadUrl.Contains('?') ? "&" : "?";
                    var authedUrl = $"{downloadUrl}{separator}token={apiKey}";
                    _logger?.Trace(LogCategory.Download, "LoraDownload",
                        $"GET (with token) {downloadUrl}{separator}token=…(redacted, len={apiKey.Length})");
                    response = await httpClient.GetAsync(authedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    _logger?.Debug(LogCategory.Download, "LoraDownload",
                        $"Retry HTTP {(int)response.StatusCode} for '{taskName}'");

                    if (!response.IsSuccessStatusCode)
                    {
                        // Read the body so the user sees Civitai's own diagnosis.
                        string? retryBody = null;
                        try { retryBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }

                        // If the version is flagged Early Access on Civitai, that's
                        // almost certainly *the* cause — surface it first instead of
                        // making the user guess between several possibilities.
                        var isEa = civitaiVersion.EarlyAccessTimeFrame > 0
                            || string.Equals(civitaiVersion.Availability, "EarlyAccess", StringComparison.OrdinalIgnoreCase);
                        var hint = (response.StatusCode, isEa) switch
                        {
                            (System.Net.HttpStatusCode.Unauthorized, true) or
                            (System.Net.HttpStatusCode.Forbidden, true) =>
                                $"This version is Early Access on Civitai (EarlyAccessTimeFrame={civitaiVersion.EarlyAccessTimeFrame}, availability={civitaiVersion.Availability ?? "(null)"}). EA content requires a Civitai Supporter / membership subscription on the account whose API key is in use. Either wait for EA to expire, or use a key from an account that has the entitlement.",
                            (System.Net.HttpStatusCode.Unauthorized, false) =>
                                "Civitai rejected the API key. Likely causes: (a) the key is invalid or expired — regenerate it on civitai.com under Account Settings → API Keys; (b) the account doesn't have NSFW enabled but the model is NSFW-tagged.",
                            (System.Net.HttpStatusCode.Forbidden, false) =>
                                "Civitai accepted the key but the account lacks permission for this model — usually a membership-locked resource.",
                            (System.Net.HttpStatusCode.NotFound, _) =>
                                "Model or version no longer exists on Civitai (404).",
                            _ => $"HTTP {(int)response.StatusCode}."
                        };
                        _logger?.Warn(LogCategory.Download, "LoraDownload",
                            $"Authenticated download still failed for '{taskName}': {hint}",
                            $"Url: {downloadUrl}\nResponse: HTTP {(int)response.StatusCode}\nEarly Access: {isEa}\nBody: {Truncate(retryBody, 800)}");
                    }
                }
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                long totalRead = 0;
                var tempPath = targetPath + ".tmp";

                await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    int bytesRead;
                    var lastProgressReport = Environment.TickCount64;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        totalRead += bytesRead;

                        var now = Environment.TickCount64;
                        if (now - lastProgressReport < 250) continue;
                        lastProgressReport = now;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            var progress = (double)totalRead / totalBytes.Value;
                            var mbDownloaded = totalRead / (1024.0 * 1024.0);
                            var mbTotal = totalBytes.Value / (1024.0 * 1024.0);
                            var message = $"{mbDownloaded:F1} / {mbTotal:F1} MB";
                            taskHandle?.ReportProgress(progress, message);
                            activityLog?.ReportDownloadProgress((int)(progress * 100), message);
                            reportProgress?.Invoke(progress, message);
                        }
                        else
                        {
                            var mbDownloaded = totalRead / (1024.0 * 1024.0);
                            var message = $"{mbDownloaded:F1} MB downloaded";
                            taskHandle?.ReportIndeterminate(message);
                            reportProgress?.Invoke(0, message);
                        }
                    }
                }

                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                File.Move(tempPath, targetPath);

                var finalMb = totalRead / (1024.0 * 1024.0);
                var sizeText = totalBytes.HasValue && totalBytes.Value > 0
                    ? $"{finalMb:F1} / {totalBytes.Value / (1024.0 * 1024.0):F1} MB"
                    : $"{finalMb:F1} MB";

                await PersistDownloadedModelAsync(targetPath, civitaiVersion, existingModelId);

                taskHandle?.Complete($"{Path.GetFileName(targetPath)} downloaded successfully — {sizeText}");
                activityLog?.CompleteDownloadProgress(true,
                    $"{Path.GetFileName(targetPath)} downloaded successfully — {sizeText}");
                completed?.Invoke();
            }
        }
        catch (OperationCanceledException ex)
        {
            taskHandle?.Fail(ex, $"Download cancelled: {Path.GetFileName(targetPath)}");
            activityLog?.CompleteDownloadProgress(false, $"Download cancelled: {Path.GetFileName(targetPath)}");
            CleanupTempFile(targetPath);
            failed?.Invoke();
        }
        catch (HttpRequestException httpEx)
        {
            // HTTP errors (401 / 403 / 404 / 5xx) are already explained by the
            // dedicated warn log emitted in the auth-retry block, so this just
            // needs to flip the task status without piling another stack trace
            // into the console.
            taskHandle?.Fail(httpEx,
                $"Download failed ({(int?)httpEx.StatusCode}): {Path.GetFileName(targetPath)}");
            activityLog?.CompleteDownloadProgress(false,
                $"Download failed: {Path.GetFileName(targetPath)} (HTTP {(int?)httpEx.StatusCode})");
            CleanupTempFile(targetPath);
            failed?.Invoke();
        }
        catch (Exception ex)
        {
            taskHandle?.Fail(ex, $"Failed to download: {ex.Message}");
            activityLog?.CompleteDownloadProgress(false, $"Download failed: {Path.GetFileName(targetPath)}");
            CleanupTempFile(targetPath);
            failed?.Invoke();
        }
    }

    /// <summary>
    /// Persists the downloaded model to <c>Diffusion_Nexus-core.db</c> with full Civitai metadata.
    /// </summary>
    public async Task PersistDownloadedModelAsync(string filePath, CivitaiModelVersion civitaiVersion, int? existingModelId = null)
    {
        try
        {
            var scopeFactory = App.Services?.GetService<IServiceScopeFactory>();
            if (scopeFactory is null)
            {
                _logger?.Warn(LogCategory.Download, "LoraDownload",
                    "Cannot persist to database: IServiceScopeFactory not available");
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var existingPaths = await unitOfWork.ModelFiles.GetExistingLocalPathsAsync();
            if (existingPaths.Contains(filePath))
            {
                _logger?.Debug(LogCategory.Download, "LoraDownload",
                    $"File already in database: {filePath}");
                return;
            }

            var fileInfo = new FileInfo(filePath);

            var effectiveVersion = civitaiVersion;
            if (civitaiVersion.ModelId <= 0 && civitaiVersion.Id > 0 && _civitaiClient is not null)
            {
                var apiKey = await GetApiKeyAsync();
                var fetched = await _civitaiClient.GetModelVersionAsync(civitaiVersion.Id, apiKey);
                if (fetched is not null)
                {
                    effectiveVersion = fetched;
                    _logger?.Debug(LogCategory.Download, "LoraDownload",
                        $"Resolved ModelId={fetched.ModelId} from version {fetched.Id}");
                }
            }

            CivitaiModel? civitaiModel = null;
            if (effectiveVersion.ModelId > 0 && _civitaiClient is not null)
            {
                var apiKey = await GetApiKeyAsync();
                civitaiModel = await _civitaiClient.GetModelAsync(effectiveVersion.ModelId, apiKey);
            }

            var modelPageId = civitaiModel?.Id ?? (effectiveVersion.ModelId > 0 ? effectiveVersion.ModelId : (int?)null);
            var bestVersion = civitaiModel?.ModelVersions
                .FirstOrDefault(v => v.Id == effectiveVersion.Id) ?? effectiveVersion;

            var civFile = bestVersion.Files.FirstOrDefault(f => f.Primary == true)
                          ?? bestVersion.Files.FirstOrDefault()
                          ?? civitaiVersion.Files.FirstOrDefault(f => f.Primary == true)
                          ?? civitaiVersion.Files.FirstOrDefault();

            var model = await unitOfWork.Models.FindByModelPageIdOrIdAsync(modelPageId, existingModelId);
            bool isExistingModel = false;

            if (model is not null)
            {
                isExistingModel = true;
                _logger?.Debug(LogCategory.Download, "LoraDownload",
                    $"Adding version to existing model '{model.Name}' (Id={model.Id}, PageId={modelPageId})");

                if (civitaiModel is not null)
                {
                    model.CivitaiId ??= modelPageId;
                    model.CivitaiModelPageId ??= modelPageId;
                    model.Name = civitaiModel.Name;
                    model.Description ??= civitaiModel.Description;
                    model.IsNsfw = civitaiModel.Nsfw;
                    model.IsPoi = civitaiModel.Poi;
                    model.Source = DataSource.CivitaiApi;
                    model.LastSyncedAt = DateTimeOffset.UtcNow;
                    model.AllowNoCredit = civitaiModel.AllowNoCredit;
                    model.AllowDerivatives = civitaiModel.AllowDerivatives;
                    model.AllowDifferentLicense = civitaiModel.AllowDifferentLicense;

                    if (civitaiModel.Creator is not null)
                    {
                        if (model.Creator is not null)
                        {
                            model.Creator.Username = civitaiModel.Creator.Username;
                            model.Creator.AvatarUrl ??= civitaiModel.Creator.Image;
                        }
                        else
                        {
                            var existingCreator = await unitOfWork.Models
                                .FindCreatorByUsernameAsync(civitaiModel.Creator.Username);

                            model.Creator = existingCreator ?? new Creator
                            {
                                Username = civitaiModel.Creator.Username,
                                AvatarUrl = civitaiModel.Creator.Image,
                            };
                        }
                    }
                }
                else if (modelPageId.HasValue)
                {
                    model.CivitaiModelPageId ??= modelPageId;
                }
            }
            else
            {
                model = new Model
                {
                    CivitaiId = modelPageId,
                    CivitaiModelPageId = modelPageId,
                    Name = civitaiModel?.Name ?? civitaiVersion.Model?.Name
                           ?? Path.GetFileNameWithoutExtension(filePath),
                    Description = civitaiModel?.Description,
                    Type = ModelType.LORA,
                    IsNsfw = civitaiModel?.Nsfw ?? civitaiVersion.Model?.Nsfw ?? false,
                    Source = DataSource.CivitaiApi,
                    LastSyncedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                if (civitaiModel is not null)
                {
                    model.AllowNoCredit = civitaiModel.AllowNoCredit;
                    model.AllowDerivatives = civitaiModel.AllowDerivatives;
                    model.AllowDifferentLicense = civitaiModel.AllowDifferentLicense;
                    model.IsPoi = civitaiModel.Poi;

                    if (civitaiModel.Creator is not null)
                    {
                        var existingCreator = await unitOfWork.Models
                            .FindCreatorByUsernameAsync(civitaiModel.Creator.Username);

                        model.Creator = existingCreator ?? new Creator
                        {
                            Username = civitaiModel.Creator.Username,
                            AvatarUrl = civitaiModel.Creator.Image,
                        };
                    }
                }
            }

            var version = new ModelVersion
            {
                CivitaiId = bestVersion.Id > 0 ? bestVersion.Id : null,
                Name = bestVersion.Name,
                Description = bestVersion.Description,
                BaseModel = ParseBaseModel(bestVersion.BaseModel),
                BaseModelRaw = bestVersion.BaseModel,
                DownloadUrl = bestVersion.DownloadUrl,
                PublishedAt = bestVersion.PublishedAt,
                EarlyAccessDays = bestVersion.EarlyAccessTimeFrame,
                DownloadCount = bestVersion.Stats?.DownloadCount ?? 0,
                CreatedAt = bestVersion.CreatedAt != default
                    ? bestVersion.CreatedAt
                    : DateTimeOffset.UtcNow,
                Model = model
            };

            var order = 0;
            foreach (var word in bestVersion.TrainedWords)
            {
                version.TriggerWords.Add(new TriggerWord { Word = word, Order = order++ });
            }

            var sortOrder = 0;
            foreach (var civImage in bestVersion.Images)
            {
                if (string.IsNullOrEmpty(civImage.Url)) continue;
                version.Images.Add(new ModelImage
                {
                    CivitaiId = civImage.Id,
                    Url = civImage.Url,
                    MediaType = civImage.Type,
                    IsNsfw = civImage.Nsfw,
                    Width = civImage.Width,
                    Height = civImage.Height,
                    BlurHash = civImage.Hash,
                    SortOrder = sortOrder++,
                    CreatedAt = civImage.CreatedAt,
                    PostId = civImage.PostId,
                    Username = civImage.Username,
                    Prompt = civImage.Meta?.Prompt,
                    NegativePrompt = civImage.Meta?.NegativePrompt,
                    Seed = civImage.Meta?.Seed,
                    Steps = civImage.Meta?.Steps,
                    Sampler = civImage.Meta?.Sampler,
                    CfgScale = civImage.Meta?.CfgScale,
                });
            }

            var modelFile = new ModelFile
            {
                CivitaiId = civFile?.Id,
                FileName = fileInfo.Name,
                LocalPath = filePath,
                SizeKB = fileInfo.Length / 1024.0,
                FileSizeBytes = fileInfo.Length,
                Format = GetFileFormat(fileInfo.Extension),
                IsPrimary = true,
                IsLocalFileValid = true,
                LocalFileVerifiedAt = DateTimeOffset.UtcNow,
                DownloadUrl = civFile?.DownloadUrl,
                HashSHA256 = civFile?.Hashes?.SHA256,
                HashAutoV2 = civFile?.Hashes?.AutoV2,
                HashCRC32 = civFile?.Hashes?.CRC32,
                HashBLAKE3 = civFile?.Hashes?.BLAKE3,
                ModelVersion = version
            };

            version.Files.Add(modelFile);

            var duplicateVersion = version.CivitaiId.HasValue
                ? model.Versions.FirstOrDefault(v => v.CivitaiId == version.CivitaiId)
                : null;

            if (duplicateVersion is not null)
            {
                _logger?.Debug(LogCategory.Download, "LoraDownload",
                    $"Version CivitaiId={version.CivitaiId} already exists on model '{model.Name}' — skipping add");
            }
            else
            {
                model.Versions.Add(version);
            }

            if (civitaiModel?.Tags is { Count: > 0 } tags)
            {
                var knownTags = await unitOfWork.Models.GetAllTagsLookupAsync();

                model.Tags.Clear();

                foreach (var tagName in tags)
                {
                    if (string.IsNullOrWhiteSpace(tagName)) continue;

                    var normalized = tagName.Trim().ToLowerInvariant();

                    if (!knownTags.TryGetValue(normalized, out var tag))
                    {
                        tag = new Tag { Name = tagName, NormalizedName = normalized };
                        knownTags[normalized] = tag;
                    }

                    model.Tags.Add(new ModelTag { Tag = tag });
                }
            }

            if (!isExistingModel)
            {
                await unitOfWork.Models.AddAsync(model);
            }

            await unitOfWork.SaveChangesAsync();

            _logger?.Info(LogCategory.Download, "LoraDownload",
                $"Persisted '{model.Name}' v'{version.Name}' to database ({(isExistingModel ? "added to existing" : "new model")})",
                $"ModelId={model.Id}, VersionId={version.Id}, CivitaiPageId={modelPageId}");
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.Download, "LoraDownload",
                $"Failed to persist model to database: {ex.Message}");
        }
    }

    private async Task<string?> GetApiKeyAsync()
    {
        if (App.Services is not null)
        {
            using var scope = App.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            return await settingsService.GetCivitaiApiKeyAsync();
        }

        return _settingsService is not null
            ? await _settingsService.GetCivitaiApiKeyAsync()
            : null;
    }

    /// <summary>
    /// Trims a string for log inclusion. Civitai's auth error bodies are sometimes
    /// multi-KB HTML pages; we want enough to read the relevant JSON message
    /// without dumping the whole page into the unified console.
    /// </summary>
    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length <= maxLength) return value;
        return value[..maxLength] + $"… (+{value.Length - maxLength} chars truncated)";
    }

    private static void CleanupTempFile(string targetPath)
    {
        try
        {
            var tempPath = targetPath + ".tmp";
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static BaseModelType ParseBaseModel(string? baseModelRaw)
        => BaseModelTypeExtensions.ParseCivitai(baseModelRaw);

    private static FileFormat GetFileFormat(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".safetensors" => FileFormat.SafeTensor,
            ".pt" => FileFormat.PickleTensor,
            ".ckpt" => FileFormat.Other,
            ".pth" => FileFormat.PickleTensor,
            _ => FileFormat.Unknown
        };
    }
}
