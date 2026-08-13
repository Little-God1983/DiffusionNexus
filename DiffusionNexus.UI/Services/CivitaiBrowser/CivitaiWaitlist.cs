using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>
/// Early-access waitlist: versions the user wants once their paywall lapses.
/// Deadlines are captured from browse data at add time ("check once") and only
/// re-fetched on the explicit Update / Move-to-queue actions; the countdown and
/// tab badge are computed locally. Mirrors <see cref="CivitaiDownloadQueue"/>:
/// ObservableObject (not DI-registered), JSON snapshot in LocalAppData, and a
/// persist-path override so tests never touch the real file.
/// </summary>
public sealed class CivitaiWaitlist : ObservableObject
{
    private const string PersistFileName = "civitai-waitlist.json";

    private readonly string? _persistPathOverride;
    private readonly ICivitaiClient? _civitaiClient;
    private readonly IUnifiedLogger? _logger;

    public CivitaiWaitlist(
        ICivitaiClient? civitaiClient,
        IUnifiedLogger? logger,
        string? persistPathOverride = null)
    {
        _civitaiClient = civitaiClient;
        _logger = logger;
        _persistPathOverride = persistPathOverride;
        Entries.CollectionChanged += (_, _) => RaiseCounts();
        TryRestore();
        RefreshAvailability();
    }

    public ObservableCollection<CivitaiWaitlistEntry> Entries { get; } = [];

    /// <summary>Number of entries currently downloadable — drives the tab badge.</summary>
    public int AvailableCount => Entries.Count(e => e.IsAvailable);

    public bool HasAvailable => AvailableCount > 0;

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(AvailableCount));
        OnPropertyChanged(nameof(HasAvailable));
    }

    /// <summary>
    /// Adds a browse-result version to the waitlist. Rejects duplicates (by
    /// version id, same rule as the queue) and permanently paid versions —
    /// those never become free, so waiting is pointless. The deadline comes
    /// from the version data already loaded in the browser; no API call.
    /// </summary>
    public bool TryAdd(CivitaiResultViewModel result, CivitaiVersionPickItemViewModel pick, DateTimeOffset? utcNow = null)
    {
        if (result.Model is null) return false;

        if (pick.Version.IsPermanentlyPaid())
        {
            _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                $"Not waitlisted (permanently paid): {result.Name} — {pick.Name}");
            return false;
        }

        if (Entries.Any(e => e.VersionId == pick.Version.Id))
        {
            _logger?.Debug(LogCategory.Download, "CivitaiWaitlist",
                $"Duplicate waitlist add skipped: {result.Name} ({pick.Name}) — version {pick.Version.Id} already listed");
            return false;
        }

        var primary = pick.Version.Files.FirstOrDefault(f => f.Primary == true) ?? pick.Version.Files.FirstOrDefault();
        var entry = new CivitaiWaitlistEntry
        {
            ModelId = result.Model.Id,
            VersionId = pick.Version.Id,
            ModelName = result.Name,
            VersionName = pick.Name,
            BaseModel = pick.BaseModel,
            Category = result.Category,
            FileName = primary?.Name ?? $"{result.Name}_{pick.Version.Id}.safetensors",
            DownloadUrl = primary?.DownloadUrl ?? pick.Version.DownloadUrl ?? string.Empty,
            SizeBytes = pick.SizeBytes,
            SizeDisplay = pick.SizeDisplay,
            ExpectedSha256 = primary?.Hashes?.SHA256,
            PreviewImageUrl = pick.Version.Images.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Url))?.Url,
            IsNsfw = result.IsNsfw,
            AddedAt = utcNow ?? DateTimeOffset.UtcNow,
            EarlyAccessDeadline = pick.Version.EarlyAccessDeadline ?? pick.Version.PaidAccess?.EndsAt
        };
        entry.RefreshAvailability(utcNow);
        Entries.Add(entry);
        Persist();
        _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
            $"Waitlisted: {result.Name} — {pick.Name} (free {entry.EarlyAccessDeadline?.ToString("u") ?? "at unknown date"})",
            $"VersionId: {pick.Version.Id}\nDeadline: {entry.EarlyAccessDeadline?.ToString("u") ?? "(none)"}\nFile: {entry.FileName}");
        return true;
    }

    public void Remove(CivitaiWaitlistEntry entry)
    {
        Entries.Remove(entry);
        Persist();
        _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
            $"Removed from waitlist: {entry.ModelName} — {entry.VersionName}");
    }

    /// <summary>
    /// Local-only tick: recomputes every entry's countdown/availability and the
    /// badge counts from stored deadlines. Called by the UI timer — zero API calls.
    /// </summary>
    public void RefreshAvailability(DateTimeOffset? utcNow = null)
    {
        foreach (var e in Entries) e.RefreshAvailability(utcNow);
        RaiseCounts();
    }

    // CivitaiClient retries 429s but has NO client-side throttle — cap concurrent
    // re-checks the same way CivitaiResultViewModel gates video extraction.
    private static readonly SemaphoreSlim s_refreshGate = new(3, 3);

    /// <summary>
    /// Re-checks one entry against the API and applies the outcome matrix:
    /// still gated → deadline updated (creators extend early access); free →
    /// Available; permanent → flagged (never auto-removed); 404 → Unavailable;
    /// network error → CheckFailed with old data kept. Returns the fetched
    /// version so move-to-queue can reuse it without a second fetch.
    /// </summary>
    public async Task<CivitaiModelVersion?> RefreshEntryAsync(
        CivitaiWaitlistEntry entry, string? apiKey, CancellationToken ct = default, DateTimeOffset? utcNow = null)
    {
        if (_civitaiClient is null) return null;
        var now = utcNow ?? DateTimeOffset.UtcNow;

        await s_refreshGate.WaitAsync(ct);
        CivitaiModelVersion? version = null;
        try
        {
            _logger?.Debug(LogCategory.Download, "CivitaiWaitlist",
                $"Re-checking: {entry.ModelName} — {entry.VersionName} (version {entry.VersionId})");
            version = await _civitaiClient.GetModelVersionAsync(entry.VersionId, apiKey, ct);

            if (version is null)
            {
                entry.Status = WaitlistEntryStatus.Unavailable;
                entry.StatusDetail = "The model version no longer exists on Civitai.";
            }
            else if (version.IsPermanentlyPaid())
            {
                entry.Status = WaitlistEntryStatus.PermanentlyPaid;
                entry.StatusDetail = null;
            }
            else if (version.IsEarlyAccessActive(now))
            {
                entry.EarlyAccessDeadline = version.EarlyAccessDeadline ?? version.PaidAccess?.EndsAt;
                entry.Status = WaitlistEntryStatus.Waiting;
                entry.StatusDetail = null;
            }
            else
            {
                entry.EarlyAccessDeadline = version.EarlyAccessDeadline;
                entry.Status = WaitlistEntryStatus.Available;
                entry.StatusDetail = null;
            }
            entry.LastCheckedAt = now;
            _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                $"Re-check result: {entry.ModelName} — {entry.VersionName} → {entry.Status}" +
                (entry.EarlyAccessDeadline is { } d ? $" (deadline {d:u})" : ""));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Old deadline and LastCheckedAt survive — a flaky connection must
            // not wipe a countdown the user is relying on.
            entry.Status = WaitlistEntryStatus.CheckFailed;
            entry.StatusDetail = ex.Message;
            _logger?.Warn(LogCategory.Download, "CivitaiWaitlist",
                $"Re-check failed for {entry.ModelName} — {entry.VersionName}: {ex.Message}");
        }
        finally
        {
            s_refreshGate.Release();
        }

        entry.RefreshAvailability(utcNow);
        return version;
    }

    /// <summary>"Update all" — re-checks every entry (gated to 3 concurrent), then persists.</summary>
    public async Task RefreshAllAsync(string? apiKey, CancellationToken ct = default, DateTimeOffset? utcNow = null)
    {
        if (_civitaiClient is null)
        {
            RefreshAvailability(utcNow);
            return;
        }
        var entries = Entries.ToList();
        _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
            $"Updating waitlist: re-checking {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} against Civitai…");
        await Task.WhenAll(entries.Select(e => RefreshEntryAsync(e, apiKey, ct, utcNow)));
        Persist();
        RefreshAvailability(utcNow);
    }

    /// <summary>
    /// "Move ready to queue": takes entries whose countdown has ended, re-verifies
    /// each against the API (stored deadlines go stale — creators extend early
    /// access or flip it to permanent), enqueues the confirmed-free ones, and keeps
    /// the rest on the waitlist with their corrected state. Returns number moved.
    /// </summary>
    public async Task<int> MoveReadyToQueueAsync(
        CivitaiDownloadQueue queue, string? apiKey, CancellationToken ct = default, DateTimeOffset? utcNow = null)
    {
        var ready = Entries.Where(e => e.IsAvailable).ToList();
        if (ready.Count == 0) return 0;

        _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
            $"Move to queue: verifying {ready.Count} ready entr{(ready.Count == 1 ? "y" : "ies")}…");

        var moved = 0;
        foreach (var entry in ready)
        {
            CivitaiModelVersion? version = null;
            if (_civitaiClient is not null)
            {
                version = await RefreshEntryAsync(entry, apiKey, ct, utcNow);
                if (entry.Status != WaitlistEntryStatus.Available)
                {
                    _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                        $"Kept on waitlist after re-check: {entry.ModelName} — {entry.VersionName} ({entry.Status})");
                    continue;
                }
            }

            // A null job means the version is already queued — the entry's goal is
            // met either way, so it leaves the waitlist in both cases.
            queue.EnqueueFromWaitlist(entry, version);
            Entries.Remove(entry);
            moved++;
        }

        Persist();
        RefreshAvailability(utcNow);
        _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
            $"Move to queue complete: {moved} of {ready.Count} moved.");
        return moved;
    }

    #region Persistence

    private string GetPersistPath()
    {
        if (_persistPathOverride is not null) return _persistPathOverride;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffusionNexus");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, PersistFileName);
    }

    private void Persist()
    {
        try
        {
            var snapshot = Entries.Select(e => new PersistedEntry
            {
                ModelId = e.ModelId,
                VersionId = e.VersionId,
                ModelName = e.ModelName,
                VersionName = e.VersionName,
                BaseModel = e.BaseModel,
                Category = e.Category,
                FileName = e.FileName,
                DownloadUrl = e.DownloadUrl,
                SizeDisplay = e.SizeDisplay,
                SizeBytes = e.SizeBytes,
                ExpectedSha256 = e.ExpectedSha256,
                PreviewImageUrl = e.PreviewImageUrl,
                IsNsfw = e.IsNsfw,
                AddedAt = e.AddedAt,
                EarlyAccessDeadline = e.EarlyAccessDeadline,
                LastCheckedAt = e.LastCheckedAt,
                Status = e.Status,
                StatusDetail = e.StatusDetail
            }).ToList();
            File.WriteAllText(GetPersistPath(), JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Download, "CivitaiWaitlist", $"Persist failed: {ex.Message}");
        }
    }

    private void TryRestore()
    {
        try
        {
            var path = GetPersistPath();
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return;
            var snapshot = JsonSerializer.Deserialize<List<PersistedEntry>>(json);
            if (snapshot is null) return;
            foreach (var p in snapshot)
            {
                var entry = new CivitaiWaitlistEntry
                {
                    ModelId = p.ModelId,
                    VersionId = p.VersionId,
                    ModelName = p.ModelName,
                    VersionName = p.VersionName,
                    BaseModel = p.BaseModel,
                    Category = p.Category,
                    FileName = p.FileName,
                    DownloadUrl = p.DownloadUrl,
                    SizeDisplay = p.SizeDisplay,
                    SizeBytes = p.SizeBytes,
                    ExpectedSha256 = p.ExpectedSha256,
                    PreviewImageUrl = p.PreviewImageUrl,
                    IsNsfw = p.IsNsfw,
                    AddedAt = p.AddedAt,
                    EarlyAccessDeadline = p.EarlyAccessDeadline,
                    LastCheckedAt = p.LastCheckedAt,
                    Status = p.Status,
                    StatusDetail = p.StatusDetail
                };
                Entries.Add(entry);
            }
            _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                $"Restored {Entries.Count} waitlist entr{(Entries.Count == 1 ? "y" : "ies")} from disk.");
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Download, "CivitaiWaitlist", $"Restore failed: {ex.Message}");
        }
    }

    private sealed class PersistedEntry
    {
        public int ModelId { get; set; }
        public int VersionId { get; set; }
        public string ModelName { get; set; } = string.Empty;
        public string VersionName { get; set; } = string.Empty;
        public string BaseModel { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string SizeDisplay { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string? ExpectedSha256 { get; set; }
        public string? PreviewImageUrl { get; set; }
        public bool IsNsfw { get; set; }
        public DateTimeOffset AddedAt { get; set; }
        public DateTimeOffset? EarlyAccessDeadline { get; set; }
        public DateTimeOffset? LastCheckedAt { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WaitlistEntryStatus Status { get; set; }

        public string? StatusDetail { get; set; }
    }

    #endregion
}
