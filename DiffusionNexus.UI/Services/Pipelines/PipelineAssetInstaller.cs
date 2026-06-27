using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.Civitai;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services.Diffusion;
using Serilog;
using SdkVramSelector = DiffusionNexus.Installer.SDK.Services.Installation.Utilities.VramProfileHelper;
using VramConvert = DiffusionNexus.Installer.SDK.Models.Helpers.VramProfileHelper;

namespace DiffusionNexus.UI.Services.Pipelines;

/// <summary>
/// Default <see cref="IPipelineAssetInstaller"/>. HuggingFace assets are streamed directly
/// (attaching the user's HF token as a Bearer header for gated repos); Civitai LoRAs are
/// resolved via <see cref="ICivitaiClient"/> and downloaded through <see cref="LoraDownloadService"/>.
/// Every download is routed through <see cref="IDownloadCoordinator"/> so the status-bar flyout
/// and cancel button work for free.
/// </summary>
public sealed class PipelineAssetInstaller : IPipelineAssetInstaller, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<PipelineAssetInstaller>();

    private static readonly EnumerationOptions RecursiveSafe = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private readonly IDownloadCoordinator _coordinator;
    private readonly ICivitaiClient _civitai;
    private readonly LoraDownloadService _loraDownloadService;
    private readonly IAppSettingsService _settings;
    private readonly LocalDiffusionBackendProvider _backendProvider;
    private readonly IUnifiedLogger? _unifiedLogger;
    private readonly HttpClient _httpClient;

    public PipelineAssetInstaller(
        IDownloadCoordinator coordinator,
        ICivitaiClient civitai,
        LoraDownloadService loraDownloadService,
        IAppSettingsService settings,
        LocalDiffusionBackendProvider backendProvider,
        IUnifiedLogger? unifiedLogger = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _civitai = civitai ?? throw new ArgumentNullException(nameof(civitai));
        _loraDownloadService = loraDownloadService ?? throw new ArgumentNullException(nameof(loraDownloadService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _backendProvider = backendProvider ?? throw new ArgumentNullException(nameof(backendProvider));
        _unifiedLogger = unifiedLogger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromHours(4) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DiffusionNexus/1.0");
    }

    /// <inheritdoc />
    public async Task<string?> ResolveModelsRootAsync(CancellationToken cancellationToken = default)
    {
        var roots = await _backendProvider.GetComfyUiModelsRootsAsync(cancellationToken).ConfigureAwait(false);
        return roots.Count > 0 ? roots[0] : null;
    }

    /// <inheritdoc />
    public async Task<PipelineReadiness> CheckAsync(PipelineManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var roots = await _backendProvider.GetComfyUiModelsRootsAsync(cancellationToken).ConfigureAwait(false);
        return await ComputeReadinessAsync(manifest, roots, cancellationToken).ConfigureAwait(false);
    }

    // Disk scanning (recursive, plus reading Civitai sidecars) can touch large model trees, so
    // run it off the UI thread.
    private static Task<PipelineReadiness> ComputeReadinessAsync(
        PipelineManifest manifest, IReadOnlyList<string> roots, CancellationToken ct)
        => Task.Run(() => BuildReadiness(manifest, roots), ct);

    /// <inheritdoc />
    public async Task<PipelineReadiness> InstallMissingAsync(
        PipelineManifest manifest,
        int vramGb,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var roots = await _backendProvider.GetComfyUiModelsRootsAsync(cancellationToken).ConfigureAwait(false);
        if (roots.Count == 0)
            throw new InvalidOperationException(
                "No ComfyUI installation is registered, so there is no models folder to download into.");

        var before = await ComputeReadinessAsync(manifest, roots, cancellationToken).ConfigureAwait(false);
        var missing = before.Missing.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        if (missing.Count == 0)
            return before;

        // New downloads land in the primary (default) install's models tree; existing copies in
        // other roots (e.g. an extra_model_paths library) are still detected by the check above.
        var downloadRoot = roots[0];
        var hfToken = await _settings.GetHuggingfaceApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var civitaiKey = await _settings.GetCivitaiApiKeyAsync(cancellationToken).ConfigureAwait(false);

        foreach (var asset in manifest.Assets)
        {
            if (!missing.Contains(asset.Name))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            var targetDir = Path.Combine(downloadRoot, asset.TargetSubfolder);
            Directory.CreateDirectory(targetDir);

            if (asset.Kind == PipelineAssetKind.Lora && asset.CivitaiModelId is int modelId)
                await InstallCivitaiLoraAsync(asset, modelId, targetDir, civitaiKey, cancellationToken).ConfigureAwait(false);
            else
                await InstallHuggingFaceAssetAsync(asset, targetDir, vramGb, hfToken, cancellationToken).ConfigureAwait(false);
        }

        var afterRoots = await _backendProvider.GetComfyUiModelsRootsAsync(cancellationToken).ConfigureAwait(false);
        return await ComputeReadinessAsync(manifest, afterRoots, cancellationToken).ConfigureAwait(false);
    }

    private static PipelineReadiness BuildReadiness(PipelineManifest manifest, IReadOnlyList<string> roots)
    {
        var states = new List<PipelineAssetState>(manifest.Assets.Count);

        foreach (var asset in manifest.Assets)
        {
            string? resolved = asset.Kind == PipelineAssetKind.Lora
                ? FindLoraOnDisk(roots, asset)
                : FindHuggingFaceAssetOnDisk(roots, asset);

            states.Add(new PipelineAssetState(
                asset.Name,
                asset.Kind,
                resolved is not null,
                resolved is null ? null : Path.GetFileName(resolved)));
        }

        return new PipelineReadiness(states);
    }

    // ── HuggingFace ────────────────────────────────────────────────────────────

    private async Task InstallHuggingFaceAssetAsync(
        PipelineAsset asset, string targetDir, int vramGb, string? hfToken, CancellationToken ct)
    {
        // Build SDK links so we can reuse the proven VRAM-tier selection. Tier-less assets
        // (encoder, VAE) carry a null profile and are always selected.
        var links = asset.HuggingFaceLinks
            .Where(l => !string.IsNullOrWhiteSpace(l.Url))
            .Select(l => new ModelDownloadLink
            {
                Url = l.Url,
                VramProfile = l.VramGb is int gb ? VramConvert.FromGigabytes(gb) : null,
                Enabled = true,
            })
            .ToList();

        var selected = SdkVramSelector.SelectBestMatchingLinks(links, vramGb, logProgress: null, modelName: asset.Name);
        if (selected.Count == 0)
            throw new InvalidOperationException($"{asset.Name}: no download link matched the selected {vramGb} GB profile.");

        foreach (var link in selected)
        {
            ct.ThrowIfCancellationRequested();

            var url = HuggingFaceUrl.NormalizeResolveUrl(link.Url);
            if (string.IsNullOrEmpty(url))
                continue;

            var fileName = HuggingFaceUrl.GetFileName(url);
            if (string.IsNullOrEmpty(fileName))
                throw new InvalidOperationException($"{asset.Name}: could not determine a filename from '{link.Url}'.");

            var target = Path.Combine(targetDir, fileName);
            if (File.Exists(target))
                continue;

            var taskName = $"{asset.Name} — {fileName}";
            string? lastError = null;

            var ok = await _coordinator.EnqueueAsync(taskName, async (progress, downloadCt) =>
            {
                try
                {
                    return await DownloadHuggingFaceFileAsync(url, target, hfToken, progress, downloadCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lastError = "Cancelled.";
                    return false;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error(ex, "HuggingFace download failed for {Task}", taskName);
                    return false;
                }
            }, ct).ConfigureAwait(false);

            if (!ok)
                throw new InvalidOperationException(lastError ?? $"Download failed: {taskName}");

            _unifiedLogger?.Info(LogCategory.Download, "Pipelines", $"Downloaded {fileName} for pipeline asset '{asset.Name}'.");
        }
    }

    private async Task<bool> DownloadHuggingFaceFileAsync(
        string url, string targetPath, string? hfToken, IProgress<DownloadTaskProgress> progress, CancellationToken ct)
    {
        var fileName = Path.GetFileName(targetPath);
        var tempPath = targetPath + ".tmp";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(hfToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hfToken);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var hint = status is 401 or 403
                ? string.IsNullOrWhiteSpace(hfToken)
                    ? " This repository may be gated — add a HuggingFace token in Settings."
                    : " Your HuggingFace token may not have access to this gated repository."
                : string.Empty;
            throw new HttpRequestException($"HuggingFace returned {status} {response.ReasonPhrase} for {fileName}.{hint}");
        }

        var total = response.Content.Headers.ContentLength;
        progress.Report(new DownloadTaskProgress(0, $"Downloading {fileName}"));

        try
        {
            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                var lastPercent = -1;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    readTotal += read;
                    if (total is > 0)
                    {
                        var percent = (int)(readTotal * 100 / total.Value);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress.Report(new DownloadTaskProgress(percent, $"Downloading {fileName}"));
                        }
                    }
                }
            }

            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);
            progress.Report(new DownloadTaskProgress(100, $"Downloaded {fileName}"));
            return true;
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    // ── Civitai LoRAs ──────────────────────────────────────────────────────────

    private async Task InstallCivitaiLoraAsync(
        PipelineAsset asset, int modelId, string targetDir, string? civitaiKey, CancellationToken ct)
    {
        var model = await _civitai.GetModelAsync(modelId, civitaiKey, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{asset.Name}: Civitai model {modelId} not found (it may be gated — check your Civitai API key).");

        var version = model.ModelVersions.FirstOrDefault()
            ?? throw new InvalidOperationException($"{asset.Name}: Civitai model {modelId} has no downloadable versions.");

        var primary = version.Files.FirstOrDefault(f => f.Primary == true) ?? version.Files.FirstOrDefault();
        var url = primary?.DownloadUrl ?? version.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException($"{asset.Name}: no Civitai download URL for version {version.Id}.");

        var fileName = primary?.Name ?? $"civitai_{modelId}_{version.Id}.safetensors";
        var target = Path.Combine(targetDir, fileName);
        if (File.Exists(target))
            return;

        var earlyAccess = version.EarlyAccessTimeFrame > 0
            || string.Equals(version.Availability, "EarlyAccess", StringComparison.OrdinalIgnoreCase);

        var taskName = $"{asset.Name} — {fileName}";
        string? lastError = null;

        var ok = await _coordinator.EnqueueAsync(taskName, (progress, downloadCt) =>
        {
            var tcs = new TaskCompletionSource<bool>();
            // LoraDownloadService handles the Civitai 401/403 token retry itself and reports
            // outcome via the completed/failed callbacks (it never throws). reportToActivityLog
            // is false so we don't fight the coordinator for the single status slot.
            _ = _loraDownloadService.DownloadFileAsync(
                downloadUrl: url,
                targetPath: target,
                civitaiVersion: version,
                taskName: taskName,
                reportProgress: (fraction, message) =>
                    progress.Report(new DownloadTaskProgress((int)(fraction * 100), message)),
                completed: () => tcs.TrySetResult(true),
                failed: () => tcs.TrySetResult(false),
                externalCancellationToken: downloadCt,
                reportToActivityLog: false);
            return tcs.Task;
        }, ct).ConfigureAwait(false);

        if (!ok)
        {
            var hint = earlyAccess
                ? " This version is Early Access — it needs a Civitai membership/Supporter API key."
                : " Check your Civitai API key in Settings.";
            throw new InvalidOperationException(lastError ?? $"{asset.Name}: download failed.{hint}");
        }

        _unifiedLogger?.Info(LogCategory.Download, "Pipelines", $"Downloaded {fileName} for pipeline LoRA '{asset.Name}'.");
    }

    // ── Disk checks ────────────────────────────────────────────────────────────

    private static string? FindHuggingFaceAssetOnDisk(IReadOnlyList<string> roots, PipelineAsset asset)
    {
        // Candidate filenames: explicit ExpectedFileName, else the filename(s) parsed from each
        // HF link (the tiered diffusion model has one filename per VRAM variant — any present
        // variant satisfies the asset).
        var candidates = !string.IsNullOrWhiteSpace(asset.ExpectedFileName)
            ? new[] { asset.ExpectedFileName! }
            : asset.HuggingFaceLinks
                .Select(l => HuggingFaceUrl.GetFileName(l.Url))
                .Where(n => n.Length > 0)
                .ToArray();

        foreach (var name in candidates)
        {
            foreach (var root in roots)
            {
                var hit = FindExact(root, name);
                if (hit is not null)
                    return hit;
            }
        }

        return null;
    }

    private static string? FindLoraOnDisk(IReadOnlyList<string> roots, PipelineAsset asset)
    {
        // Primary, robust match: a Civitai sidecar (.civitai.info) whose modelId equals the
        // manifest's CivitaiModelId. This is how the app links a downloaded LoRA to its Civitai
        // model regardless of how the file was named on disk (e.g. "A2R_Klein_Standard.safetensors"
        // for model 1934100). Folder naming varies too (loras / Lora / LyCORIS), so we scan each
        // root recursively — the numeric modelId match makes false positives effectively impossible.
        if (asset.CivitaiModelId is int modelId)
        {
            foreach (var root in roots)
            {
                var hit = FindLoraByCivitaiModelId(root, modelId);
                if (hit is not null)
                    return hit;
            }
        }

        // Fallback: substring hint, for manually-placed LoRAs that have no Civitai sidecar.
        if (!string.IsNullOrWhiteSpace(asset.ExpectedFileName))
        {
            var sub = string.IsNullOrWhiteSpace(asset.TargetSubfolder) ? "loras" : asset.TargetSubfolder;
            foreach (var root in roots)
            {
                var dir = Path.Combine(root, sub);
                var hit = FindBySubstring(Directory.Exists(dir) ? dir : root, asset.ExpectedFileName!);
                if (hit is not null)
                    return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a LoRA previously downloaded from the given Civitai model by scanning for a
    /// <c>*.civitai.info</c> sidecar containing <c>"modelId": &lt;id&gt;</c>. Returns the matching
    /// weights file (same base name + <c>.safetensors</c>) when present, else the sidecar path.
    /// </summary>
    private static string? FindLoraByCivitaiModelId(string root, int modelId)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        // \b after the digits prevents 1934100 from matching 19341009.
        var pattern = $"\"modelId\"\\s*:\\s*{modelId}\\b";

        try
        {
            foreach (var sidecar in Directory.EnumerateFiles(root, "*.civitai.info", RecursiveSafe))
            {
                string text;
                try { text = File.ReadAllText(sidecar); }
                catch { continue; }

                if (!Regex.IsMatch(text, pattern))
                    continue;

                var baseName = sidecar[..^".civitai.info".Length];
                var weights = baseName + ".safetensors";
                return File.Exists(weights) ? weights : sidecar;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Civitai sidecar scan failed under {Root} for modelId {ModelId}", root, modelId);
        }

        return null;
    }

    private static string? FindExact(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(fileName))
            return null;
        try
        {
            return Directory.EnumerateFiles(root, fileName, RecursiveSafe).FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "FindExact failed under {Root} for {File}", root, fileName);
            return null;
        }
    }

    private static string? FindBySubstring(string dir, string hint)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) || string.IsNullOrWhiteSpace(hint))
            return null;
        try
        {
            return Directory.EnumerateFiles(dir, "*", RecursiveSafe)
                .FirstOrDefault(p => Path.GetFileName(p).Contains(hint, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "FindBySubstring failed under {Dir} for hint {Hint}", dir, hint);
            return null;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
