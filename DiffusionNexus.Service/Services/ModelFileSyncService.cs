using System.Security.Cryptography;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Utilities;
using DiffusionNexus.Service.Services.Sync.Identity;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for synchronizing local model files with the database.
/// Implements a database-first approach with background verification.
/// </summary>
public class ModelFileSyncService : IModelSyncService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAppSettingsService _settingsService;

    /// <summary>
    /// Number of bytes to read for partial hash (10MB).
    /// </summary>
    private const int PartialHashBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Supported model file extensions — the shared Sortable set, so what the library DISCOVERS and
    /// what the sorter enumerates cannot diverge. They had: a ".sft" was visible to the sorter and
    /// still never got a DB row, so the sorter would file a model the Viewer could never show.
    /// </summary>
    private static readonly string[] ModelExtensions = ModelFileExtensions.Sortable;

    public ModelFileSyncService(IUnitOfWork unitOfWork, IAppSettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(settingsService);
        _unitOfWork = unitOfWork;
        _settingsService = settingsService;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> LoadCachedModelsAsync(CancellationToken cancellationToken = default)
    {
        // Use the lightweight query that excludes ThumbnailData BLOBs from SQLite.
        // With 11K+ models this prevents SQLite OOM (Error 7). Thumbnails are
        // lazy-loaded per-tile on demand.
        var all = await _unitOfWork.Models
            .GetModelsWithLocalFilesLightAsync(cancellationToken)
            .ConfigureAwait(false);

        // Honor the IsEnabled toggle on LoRA sources: hide models whose only files
        // live under disabled (or removed) source folders. Without this, unchecking
        // a source in Settings + reloading still showed its LoRAs in the Installed
        // tab. If the user has no enabled sources at all, returns nothing — the
        // tab is empty, which matches "nothing scannable is configured".
        var enabledRoots = await _settingsService.GetEnabledLoraSourcesAsync(cancellationToken)
            .ConfigureAwait(false);

        var normalizedRoots = NormalizeRoots(enabledRoots);

        if (normalizedRoots.Count == 0)
        {
            return [];
        }

        var filtered = new List<Model>(all.Count);
        foreach (var model in all)
        {
            foreach (var version in model.Versions)
            {
                var anyMatch = false;
                foreach (var file in version.Files)
                {
                    if (string.IsNullOrEmpty(file.LocalPath)) continue;
                    // Issue #380: skip rows that have been explicitly verified as missing
                    // (file gone from disk). LocalFileVerifiedAt == null = legacy row that
                    // predates verification; trust it until verification updates the flag.
                    if (!file.IsLocalFileValid && file.LocalFileVerifiedAt != null) continue;
                    if (PathIsUnderAnyRoot(file.LocalPath, normalizedRoots))
                    {
                        anyMatch = true;
                        break;
                    }
                }
                if (anyMatch)
                {
                    filtered.Add(model);
                    break;
                }
            }
        }
        return filtered;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledModelFile>> LoadCachedFilesAsync(CancellationToken cancellationToken = default)
    {
        var all = await _unitOfWork.Models
            .GetModelsWithLocalFilesLightAsync(cancellationToken)
            .ConfigureAwait(false);

        var enabledRoots = await _settingsService.GetEnabledLoraSourcesAsync(cancellationToken)
            .ConfigureAwait(false);

        var normalizedRoots = NormalizeRoots(enabledRoots);

        if (normalizedRoots.Count == 0)
        {
            return [];
        }

        // Dedup by LocalPath so two ModelFile rows that somehow point at the same
        // file (legacy data from pre-fix re-discovery scans) collapse into one entry.
        var seen = new Dictionary<string, InstalledModelFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in all)
        {
            // The LoRA viewer is LoRA-family only — exclude upscalers, VAEs, checkpoints,
            // text encoders etc. that may share the configured folders. Matches the
            // "All LoRA types" preset in the Civitai browser.
            if (!IsLoraFamily(model.Type)) continue;

            foreach (var version in model.Versions)
            {
                foreach (var file in version.Files)
                {
                    if (string.IsNullOrEmpty(file.LocalPath)) continue;
                    if (!file.IsLocalFileValid && file.LocalFileVerifiedAt != null) continue;
                    var root = MatchEnabledRoot(file.LocalPath, normalizedRoots);
                    if (root is null) continue;
                    // When several rows survive for one path (a generic-filename
                    // collision mid-resolution), the most recently verified row is
                    // the one whose model actually owns the bytes on disk — the
                    // contested-path arbitration in VerifyAndSyncFilesAsync stamps
                    // it last. First-come-wins hid freshly downloaded models behind
                    // the stale row of the model they overwrote.
                    if (!seen.TryGetValue(file.LocalPath, out var existing)
                        || (file.LocalFileVerifiedAt ?? DateTimeOffset.MinValue)
                           > (existing.File.LocalFileVerifiedAt ?? DateTimeOffset.MinValue))
                    {
                        seen[file.LocalPath] = new InstalledModelFile(model, version, file, root);
                    }
                }
            }
        }

        return seen.Values.ToList();
    }

    /// <inheritdoc />
    public async Task<int> CountExcludedSupportAssetsAsync(CancellationToken cancellationToken = default)
    {
        var all = await _unitOfWork.Models
            .GetModelsWithLocalFilesLightAsync(cancellationToken)
            .ConfigureAwait(false);

        var enabledRoots = await _settingsService.GetEnabledLoraSourcesAsync(cancellationToken)
            .ConfigureAwait(false);
        var normalizedRoots = NormalizeRoots(enabledRoots);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in all)
        {
            // Only the kinds this feature classifies. A checkpoint is also kept out of the grid,
            // but it was never a LoRA the user expected to see there — counting it would make the
            // Viewer's explanation wrong rather than merely broad.
            if (!model.Type.IsSupportAsset()) continue;

            foreach (var file in model.Versions.SelectMany(v => v.Files))
            {
                if (string.IsNullOrEmpty(file.LocalPath)) continue;
                if (!file.IsLocalFileValid && file.LocalFileVerifiedAt != null) continue;
                if (MatchEnabledRoot(file.LocalPath, normalizedRoots) is null) continue;
                seen.Add(file.LocalPath);
            }
        }

        return seen.Count;
    }

    // Unknown is included so legacy rows (Type never set explicitly) still appear —
    // the explicit non-LoRA types (Checkpoint, Upscaler, VAE, TextualInversion, etc.)
    // are the ones we want filtered out of the LoRA viewer.
    private static bool IsLoraFamily(ModelType type) =>
        type is ModelType.LORA or ModelType.LoCon or ModelType.DoRA or ModelType.Unknown;

    /// <summary>
    /// Enabled LoRA-source roots, trimmed of a trailing separator and stripped of blanks — the one
    /// definition of "an enabled root", shared by every reader of <c>GetEnabledLoraSourcesAsync</c>
    /// in this class (<see cref="LoadCachedModelsAsync"/>, <see cref="LoadCachedFilesAsync"/>,
    /// <see cref="CountExcludedSupportAssetsAsync"/>) so the grid and this count cannot disagree
    /// about what counts as enabled.
    /// </summary>
    private static List<string> NormalizeRoots(IReadOnlyList<string> enabledRoots) =>
        enabledRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();

    /// <summary>
    /// Returns the normalized root that contains <paramref name="filePath"/>, or
    /// null if it lives outside every enabled source.
    /// </summary>
    /// <remarks>
    /// The predicate itself lives in <see cref="LocalPathRoots"/> because the library sync asks the
    /// same question of the same rows (R6). It used to be spelled out here and again, differently,
    /// in <c>SyncStateRepository</c> — so a file this method accepted could be one the sync could
    /// not see, and the user got a grid full of models and a plan with nothing in it.
    /// </remarks>
    private static string? MatchEnabledRoot(string filePath, IReadOnlyList<string> normalizedRoots)
    {
        foreach (var root in normalizedRoots)
        {
            if (LocalPathRoots.IsUnder(filePath, root)) return root;
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="filePath"/> is exactly one of, or sits
    /// inside any of, the given normalized roots (case-insensitive, separator-aware
    /// so <c>C:\Foo\Bar</c> isn't accidentally matched by root <c>C:\Foo</c>).
    /// </summary>
    private static bool PathIsUnderAnyRoot(string filePath, IReadOnlyList<string> normalizedRoots)
    {
        foreach (var root in normalizedRoots)
        {
            if (LocalPathRoots.IsUnder(filePath, root))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverNewFilesAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new SyncProgress
        {
            Phase = "Getting configured folders"
        });

        // Get enabled source folders
        var sourceFolders = await _settingsService.GetEnabledLoraSourcesAsync(cancellationToken);
        
        progress?.Report(new SyncProgress
        {
            Phase = $"Found {sourceFolders.Count} configured folders"
        });
        
        if (sourceFolders.Count == 0)
        {
            progress?.Report(new SyncProgress
            {
                Phase = "No source folders configured - add folders in Settings"
            });
            return new DiscoveryResult();
        }

        progress?.Report(new SyncProgress
        {
            Phase = "Scanning for model files",
            TotalCount = sourceFolders.Count
        });

        // Get all existing local paths from database
        var existingPaths = await _unitOfWork.ModelFiles
            .GetExistingLocalPathsAsync(cancellationToken)
            .ConfigureAwait(false);

        // Scan all folders for model files
        var allFiles = new List<string>();
        foreach (var folder in sourceFolders)
        {
            if (Directory.Exists(folder))
            {
                var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(f => ModelFileExtensions.Matches(f, ModelExtensions));
                allFiles.AddRange(files);
                
                progress?.Report(new SyncProgress
                {
                    Phase = $"Scanned {folder}",
                    CurrentItem = $"Found {allFiles.Count} files so far"
                });
            }
            else
            {
                progress?.Report(new SyncProgress
                {
                    Phase = $"Folder not found: {folder}"
                });
            }
        }

        // Filter to only new files
        var newFiles = allFiles
            .Where(f => !existingPaths.Contains(f))
            .ToList();

        progress?.Report(new SyncProgress
        {
            Phase = $"Found {allFiles.Count} total files, {newFiles.Count} are new"
        });

        var newModels = new List<Model>();
        var repointed = 0;

        // Deliberately NOT an early return when newFiles.Count == 0 (#527 round 2): a returning
        // library that has already been fully indexed once hits exactly this branch on every
        // ordinary refresh, and that — not "new files just appeared" — is the common case a
        // pre-#527 library's mislabelled rows need the backfill below to reach. An early return
        // here is precisely how the passive Refresh/reconcile path (which never calls
        // ReclassifySupportAssetsAsync on its own) used to never trigger it at all.
        if (newFiles.Count > 0)
        {
            progress?.Report(new SyncProgress
            {
                Phase = "Processing new files",
                TotalCount = newFiles.Count
            });

            var processedCount = 0;

            foreach (var filePath in newFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(filePath);
                progress?.Report(new SyncProgress
                {
                    Phase = "Processing new files",
                    CurrentItem = fileName,
                    ProcessedCount = processedCount,
                    TotalCount = newFiles.Count
                });

                // First check if we can match by hash (file was moved)
                var fileInfo = new FileInfo(filePath);
                var matchedFile = await TryMatchByHashAndSizeAsync(filePath, fileInfo.Length, cancellationToken);

                if (matchedFile is not null)
                {
                    // Update the existing file's path. Counted (#537): the row was stamped invalid —
                    // hidden from the grid — so this is a grid-visible change, just not a new file.
                    matchedFile.LocalPath = filePath;
                    matchedFile.IsLocalFileValid = true;
                    matchedFile.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                    repointed++;
                }
                else
                {
                    // Create new model entry
                    var model = await CreateModelFromFileAsync(filePath, fileInfo, cancellationToken).ConfigureAwait(false);
                    await _unitOfWork.Models.AddAsync(model, cancellationToken).ConfigureAwait(false);
                    newModels.Add(model);
                }

                processedCount++;
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            progress?.Report(new SyncProgress
            {
                Phase = "Discovery complete",
                ProcessedCount = newFiles.Count,
                TotalCount = newFiles.Count
            });
        }

        // Legacy-library backfill (#527 round 2): runs on every discovery call, not only the bulk
        // "Download Missing Metadata" button. ReclassifySupportAssetsAsync used to be reachable
        // only through DiscoverFilesStep's own separate call, so the passive background reconcile
        // path — which calls this method directly and never went through that step — never ran it
        // at all. Placed after the SaveChangesAsync above (same reasoning RepointedCount already
        // follows: a moved-in support asset discovered THIS scan is caught up in the same call)
        // but honours cancellationToken itself and only saves after its own loop completes in
        // full, so a cancellation here throws rather than reporting a partial pass as a finished
        // count — it never leaves this call returning a result the caller reads as complete.
        //
        // excludeModelIds protects newModels specifically: those rows were JUST classified from
        // their weights by CreateModelFromFileAsync above (the accurate rung), and — having no
        // ModelSyncState row yet, same as a genuinely old row — would otherwise look like an
        // ordinary candidate to the query below. A file correctly discovered as LORA by its
        // weights despite a name like "vae_finetune_lora.safetensors" must not have that correct
        // verdict immediately overwritten by the weaker name-only guess this pass makes.
        var reclassified = await ReclassifySupportAssetsAsync(
            cancellationToken,
            excludeModelIds: newModels.Count > 0 ? newModels.Select(m => m.Id).ToHashSet() : null)
            .ConfigureAwait(false);

        return new DiscoveryResult { NewModels = newModels, RepointedCount = repointed, ReclassifiedCount = reclassified };
    }

    /// <inheritdoc />
    public async Task<FileSyncResult> VerifyAndSyncFilesAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new SyncProgress
        {
            Phase = "Loading files to verify"
        });

        // Get all files with local paths
        var files = await _unitOfWork.ModelFiles
            .GetAllWithLocalPathAsync(cancellationToken)
            .ConfigureAwait(false);

        if (files.Count == 0)
        {
            return new FileSyncResult();
        }

        var verified = 0;
        var missing = 0;
        var moved = 0;
        var processedCount = 0;

        progress?.Report(new SyncProgress
        {
            Phase = "Verifying files",
            TotalCount = files.Count
        });

        // Group rows claiming the same path: existence alone can't verify a contested
        // path (a generic-filename collision means the bytes belong to only ONE of the
        // claimants), so those need hash arbitration instead of the fast path.
        var byPath = files
            .Where(f => !string.IsNullOrEmpty(f.LocalPath))
            .GroupBy(f => f.LocalPath!, StringComparer.OrdinalIgnoreCase);

        foreach (var claim in byPath)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var claimants = claim.ToList();

            progress?.Report(new SyncProgress
            {
                Phase = "Verifying files",
                CurrentItem = claimants[0].FileName,
                ProcessedCount = processedCount,
                TotalCount = files.Count
            });

            if (!File.Exists(claim.Key))
            {
                foreach (var file in claimants)
                {
                    // File is missing - try to find by hash
                    var newPath = await TryFindMovedFileAsync(file, cancellationToken);
                    if (newPath is not null)
                    {
                        file.LocalPath = newPath;
                        file.IsLocalFileValid = true;
                        file.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                        moved++;
                    }
                    else
                    {
                        file.IsLocalFileValid = false;
                        file.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                        missing++;
                    }
                }
            }
            else if (claimants.Count == 1)
            {
                var file = claimants[0];
                file.IsLocalFileValid = true;
                file.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                verified++;
            }
            else
            {
                // Contested: hash the file once and validate only the claimant whose
                // recorded SHA256 matches the actual bytes. Blind existence checks here
                // used to resurrect the overwritten model's row, which then shadowed the
                // real owner in the Installed tab ("downloaded but never shows up").
                string? actualHash = null;
                try
                {
                    actualHash = await ComputeFullSha256Async(claim.Key, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Locked/unreadable right now — leave the rows as they are and let a
                    // later pass arbitrate rather than guessing ownership.
                    processedCount += claimants.Count;
                    continue;
                }

                foreach (var file in claimants)
                {
                    var owns = !string.IsNullOrWhiteSpace(file.HashSHA256)
                        && string.Equals(file.HashSHA256, actualHash, StringComparison.OrdinalIgnoreCase);
                    file.IsLocalFileValid = owns;
                    file.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                    if (owns) verified++; else missing++;
                }

                Serilog.Log.Warning(
                    "Contested path {Path}: {Count} database rows claim it; bytes match SHA256 {Hash} — validated {Owners} owner(s), invalidated the rest",
                    claim.Key, claimants.Count, actualHash,
                    claimants.Count(f => f.IsLocalFileValid));
            }

            processedCount += claimants.Count;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(new SyncProgress
        {
            Phase = "Verification complete",
            ProcessedCount = files.Count,
            TotalCount = files.Count
        });

        return new FileSyncResult
        {
            VerifiedCount = verified,
            MissingCount = missing,
            MovedCount = moved
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> FullSyncAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Phase 1: Load cached (fast)
        progress?.Report(new SyncProgress { Phase = "Loading cached models" });
        var cachedModels = await LoadCachedModelsAsync(cancellationToken);

        // Phase 2: Discover new files
        var discovery = await DiscoverNewFilesAsync(progress, cancellationToken);
        var newModels = discovery.NewModels;

        // Phase 3: Verify existing (background - can be slow)
        _ = Task.Run(async () =>
        {
            try
            {
                await VerifyAndSyncFilesAsync(progress, CancellationToken.None);
            }
            catch
            {
                // Log but don't throw - this is background work
            }
        }, CancellationToken.None);

        // Combine and return all models
        var allModels = cachedModels.Concat(newModels).ToList();
        return allModels;
    }

    /// <summary>
    /// Full-file SHA256, uppercase hex — comparable to the Civitai-recorded
    /// <see cref="ModelFile.HashSHA256"/>. Unlike <see cref="ComputeFileHashAsync"/>
    /// (a first-10MB partial for cheap moved-file candidate checks), ownership
    /// arbitration of a contested path must hash the whole file: partial hashes
    /// never equal a stored full-file SHA256.
    /// </summary>
    private static async Task<string> ComputeFullSha256Async(string filePath, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <inheritdoc />
    public async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        using var sha256 = SHA256.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);

        // For large files, only hash first 10MB for performance
        var fileSize = stream.Length;
        if (fileSize > PartialHashBytes)
        {
            var buffer = new byte[PartialHashBytes];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, PartialHashBytes), cancellationToken);
            var hash = sha256.ComputeHash(buffer, 0, bytesRead);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        else
        {
            var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    /// <inheritdoc />
    public async Task<int> ReclassifySupportAssetsAsync(
        CancellationToken cancellationToken = default,
        IReadOnlySet<int>? excludeModelIds = null)
    {
        var candidates = await _unitOfWork.Models
            .GetSupportAssetBackfillCandidatesAsync(cancellationToken)
            .ConfigureAwait(false);

        var changed = 0;
        foreach (var model in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // See the interface doc: a model DiscoverNewFilesAsync just created in the same call
            // is already weight-classified and must never be re-guessed from its name here.
            if (excludeModelIds is not null && excludeModelIds.Contains(model.Id)) continue;

            // The file name, not the model name: a user may have renamed the model in the app,
            // and it is the file on disk whose name carries the marker.
            var fileName = model.Versions
                .SelectMany(v => v.Files)
                .FirstOrDefault(f => f.IsPrimary)?.FileName
                ?? model.Versions.SelectMany(v => v.Files).FirstOrDefault()?.FileName;
            if (fileName is null) continue;

            // A safetensors container's real kind is a fact IdentifyModelStep can read directly
            // from its weights — guessing from its name here would be strictly worse evidence, and
            // the row is not left stuck waiting for that: every candidate this query selects is
            // NotIdentified/None, which IdentifyModelStep already treats as due. Only a pickle
            // (.ckpt/.pt/.pth — Sortable minus SafetensorsContainers) has no header to fall back
            // on, so its file name is the only evidence this pass, or anything else, will ever
            // have for it — that is where a name guess is actually warranted.
            if (ModelFileExtensions.Matches(fileName, ModelFileExtensions.SafetensorsContainers)) continue;

            var kind = AssetKindClassifier.Classify(fileName);
            if (!kind.IsSupportAsset()) continue;

            model.Type = kind;
            changed++;
        }

        if (changed > 0)
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return changed;
    }

    /// <summary>
    /// Tries to match a file by hash and size to find moved files.
    /// </summary>
    private async Task<ModelFile?> TryMatchByHashAndSizeAsync(string filePath, long fileSize, CancellationToken cancellationToken)
    {
        // First try to match by exact size (fast check)
        var candidatesBySize = await _unitOfWork.ModelFiles
            .FindBySizeWithInvalidPathAsync(fileSize, cancellationToken)
            .ConfigureAwait(false);

        if (candidatesBySize.Count == 0)
        {
            return null;
        }

        // If we have candidates, compute hash and try to match
        var fileHash = await ComputeFileHashAsync(filePath, cancellationToken);

        // Try to find by SHA256 hash
        var matchByHash = candidatesBySize.FirstOrDefault(f =>
            string.Equals(f.HashSHA256, fileHash, StringComparison.OrdinalIgnoreCase));

        return matchByHash;
    }

    /// <summary>
    /// Tries to find a moved file by scanning configured folders.
    /// </summary>
    private async Task<string?> TryFindMovedFileAsync(ModelFile file, CancellationToken cancellationToken)
    {
        // Only try if we have hash or size info
        if (string.IsNullOrEmpty(file.HashSHA256) && !file.FileSizeBytes.HasValue)
        {
            return null;
        }

        var sourceFolders = await _settingsService.GetEnabledLoraSourcesAsync(cancellationToken);

        foreach (var folder in sourceFolders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            // Search for files with matching name first (common case: file renamed in same location)
            var matchingFiles = Directory.EnumerateFiles(folder, file.FileName, SearchOption.AllDirectories);

            foreach (var candidatePath in matchingFiles)
            {
                var candidateInfo = new FileInfo(candidatePath);

                // Quick size check
                if (file.FileSizeBytes.HasValue && candidateInfo.Length != file.FileSizeBytes.Value)
                {
                    continue;
                }

                // Hash check for confirmation
                if (!string.IsNullOrEmpty(file.HashSHA256))
                {
                    var candidateHash = await ComputeFileHashAsync(candidatePath, cancellationToken);
                    if (string.Equals(candidateHash, file.HashSHA256, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidatePath;
                    }
                }
                else
                {
                    // If no hash, trust size match with same filename
                    return candidatePath;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the row for a newly discovered file. Async because the file's KIND is read from its
    /// safetensors header rather than assumed: this method used to stamp
    /// <c>Type = ModelType.LORA</c> unconditionally, which made every VAE, text encoder,
    /// ControlNet and upscaler in a LoRA folder indistinguishable from a LoRA everywhere
    /// downstream (#527). One bounded header read per NEW file — the same order of I/O as the
    /// 10 MB partial hash this loop already takes, and paid once per file ever.
    /// </summary>
    private static async Task<Model> CreateModelFromFileAsync(
        string filePath, FileInfo fileInfo, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var kind = await AssetKindResolver.ResolveAsync(filePath, cancellationToken).ConfigureAwait(false);

        var model = new Model
        {
            Name = fileName,
            Type = kind,
            Source = DataSource.LocalFile,
            CreatedAt = fileInfo.CreationTimeUtc
        };

        var version = new ModelVersion
        {
            Name = fileName,
            BaseModelRaw = "???", // Unknown without metadata
            BaseModel = BaseModelType.Other,
            CreatedAt = fileInfo.CreationTimeUtc,
            Model = model
        };

        var modelFile = new ModelFile
        {
            FileName = fileInfo.Name,
            LocalPath = filePath,
            SizeKB = fileInfo.Length / 1024.0,
            FileSizeBytes = fileInfo.Length,
            Format = FileFormatMapper.FromExtension(fileInfo.Extension),
            IsPrimary = true,
            IsLocalFileValid = true,
            LocalFileVerifiedAt = DateTimeOffset.UtcNow,
            ModelVersion = version
        };

        version.Files.Add(modelFile);
        model.Versions.Add(version);

        return model;
    }
}
