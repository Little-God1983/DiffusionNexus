using System.Text.Json;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.Lora;
using DiffusionNexus.Service.Services.Sync.Identity;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>Metadata resolved for one on-disk file outside the DB.</summary>
public sealed record ResolvedLoraMetadata(string? BaseModelRaw, int? CivitaiVersionId, string Sha256)
{
    /// <summary>
    /// Tag names for the model: from the file's <c>.civitai.info</c> sidecar (<c>model.tags</c>
    /// or a top-level <c>tags</c> array) when it has one, otherwise from the Civitai
    /// <c>/models/{id}</c> lookup that follows the by-hash call — the by-hash response itself
    /// carries no tags, only the owning <c>modelId</c>. Callers feed these to
    /// <see cref="SorterCategoryResolver"/> so a LoRA in a browsed folder lands in its category
    /// folder instead of being forced to Unknown. Empty when the model genuinely has no tags,
    /// or when nothing could be resolved at all.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// What the file NAME suggests, set only when nothing authoritative and no safetensors header
    /// could answer. Deliberately NOT folded into <see cref="BaseModelRaw"/>: the sorter turns that
    /// value into a physical move, and a name is a guess about a file rather than a reading of it.
    /// The caller decides whether to use it — the sorter does so behind an opt-in that tells the
    /// user how many files it would resolve.
    /// </summary>
    public string? NameGuess { get; init; }

    /// <summary>
    /// What this file IS — a LoRA, or one of the support assets a LoRA folder also holds (#527).
    /// Read from the safetensors tensor keys where there are any, else guessed from the file name.
    /// The sorter files a support asset into its own folder, so this decides a destination and not
    /// merely a label.
    /// </summary>
    public ModelType AssetKind { get; init; } = ModelType.LORA;
}

/// <summary>
/// What a file says about itself, with the two rungs kept apart because they do not carry the same
/// weight: <paramref name="FromHeader"/> read the actual weights, <paramref name="FromName"/> is a
/// guess about them. At most one is ever set — the header wins outright.
/// </summary>
public sealed record FileIdentity(string? FromHeader, string? FromName)
{
    public static FileIdentity None { get; } = new(null, null);

    /// <summary>
    /// What this file IS — a LoRA, or one of the support assets a LoRA folder also holds (#527).
    /// Read from the safetensors tensor keys where there are any, else guessed from the file name.
    /// The sorter files a support asset into its own folder, so this decides a destination and not
    /// merely a label.
    /// </summary>
    public ModelType AssetKind { get; init; } = ModelType.LORA;

    /// <summary>
    /// Whether <see cref="AssetKind"/> is a DEFAULT rather than a reading — see
    /// <see cref="AssetKindResolver.ContainerWasUnreadable"/>. A <c>.safetensors</c> we could not
    /// open deliberately answers <see cref="ModelType.LORA"/> there (a name guess on an unreadable
    /// container is the one verdict a user cannot undo), which makes a bare LORA ambiguous: it may
    /// mean "the weights say LoRA" or "we never saw the weights". Surfaced here rather than left for
    /// the caller to re-derive, because the caller would have to re-open the file to learn it, and
    /// the sorter's DB-known branch has a stored, weight-derived <c>Model.Type</c> that this default
    /// must not be allowed to demote.
    /// </summary>
    public bool ContainerWasUnreadable { get; init; }
}

/// <summary>
/// Resolves base-model/version metadata for a file the DB doesn't know about
/// (spec §3): a local <c>.civitai.info</c> sidecar wins outright (no hashing
/// needed), otherwise the file is hashed and looked up in a per-hash disk
/// cache, falling back to the Civitai by-hash API and caching that result
/// (including negative results, so a 404 stays resolved offline). When none of
/// those knows the file, it is asked to identify itself — its safetensors
/// header, then its file name. Never throws for a 404 or an offline API: a file
/// nothing can identify comes back with a null
/// <see cref="ResolvedLoraMetadata.BaseModelRaw"/> and sorts into Unknown.
/// </summary>
public sealed class SorterMetadataResolver
{
    private const string LogSource = "LoraSorter";

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ICivitaiClient? _civitaiClient;
    private readonly Func<Task<string?>> _apiKeyProvider;
    private readonly string _cacheDirectory;
    private readonly Func<string, string> _hashFile;
    private readonly IUnifiedLogger? _logger;

    /// <summary>Memoized API key read — see <see cref="ResetPerPassCaches"/>.</summary>
    private Task<string?>? _apiKeyTask;

    /// <summary>
    /// modelId → tag list for this pass, so a folder holding twenty versions of the same model
    /// costs one <c>/models/{id}</c> call instead of twenty. A null value is a remembered failure
    /// ("not resolved this pass"), which is exactly what the caller propagates so the hash cache
    /// stays open for a later retry — re-asking a model whose lookup just failed would only
    /// re-time-out once per file. Cleared per pass by <see cref="ResetPerPassCaches"/>.
    /// </summary>
    private readonly Dictionary<int, IReadOnlyList<string>?> _modelTagsMemo = [];

    /// <param name="cacheDirectory">Injected for tests; production uses
    /// Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    ///     "DiffusionNexus", "SorterCache").</param>
    /// <param name="hashFile">SHA256 hex digest of a file (any case) — injected for tests.</param>
    public SorterMetadataResolver(
        ICivitaiClient? civitaiClient,
        Func<Task<string?>> apiKeyProvider,
        string cacheDirectory,
        Func<string, string> hashFile,
        IUnifiedLogger? logger)
    {
        _civitaiClient = civitaiClient;
        _apiKeyProvider = apiKeyProvider;
        _cacheDirectory = cacheDirectory;
        _hashFile = hashFile;
        _logger = logger;
    }

    public static string DefaultCacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "SorterCache");

    /// <summary>
    /// Drops the per-pass memos (API key, model tags) so the next resolution pass re-reads them.
    /// Callers that keep a resolver alive across passes (the LoRA Sorter view model does) must call
    /// this once per pass, so a key the user changed in Settings meanwhile is picked up and a model
    /// whose tag lookup failed last pass gets one more chance.
    /// </summary>
    public void ResetPerPassCaches()
    {
        _apiKeyTask = null;
        _modelTagsMemo.Clear();
    }

    /// <summary>
    /// Resolution chain for one on-disk file: local <c>.civitai.info</c> sidecar → per-hash disk
    /// cache → Civitai by-hash API (result cached) → the file's own safetensors header → a guess
    /// from its file name. Never throws for a 404, an offline API, an unreadable file or a Civitai
    /// shape change — a file nothing can identify comes back with a null
    /// <see cref="ResolvedLoraMetadata.BaseModelRaw"/> and sorts into Unknown. Cancellation is the
    /// one thing that does propagate: it has to unwind the pass rather than be reported as one more
    /// unreadable file.
    /// </summary>
    /// <remarks>
    /// The last two rungs are the same ones the database-side identity chain uses
    /// (<see cref="SafetensorsHeaderReader"/> → <see cref="BaseModelHeaderMap"/>, then
    /// <see cref="FilenameBaseModelHeuristic"/>), called here directly rather than through
    /// <c>IdentifyModelStep</c>: that step is driven by database rows, and this resolver exists
    /// precisely for the files that have none. Without them a self-trained LoRA in a browsed folder
    /// was sorted into <c>Unknown\</c> even though its own header names the architecture it was
    /// trained on.
    /// <para>
    /// They run only when the authoritative rungs came up empty <i>with an answer</i> — see
    /// <see cref="AuthorityVerdict"/>. A file the API could not be asked about stays unresolved,
    /// because the sorter acts on this value by moving or copying bytes, and a wrong folder is
    /// worse than Unknown: Unknown is where the user looks.
    /// </para>
    /// </remarks>
    public async Task<ResolvedLoraMetadata> ResolveAsync(string filePath, CancellationToken ct = default)
    {
        var (resolved, verdict) = await ResolveFromSidecarCacheOrApiAsync(filePath, ct);
        if (verdict != AuthorityVerdict.NotKnown)
            return resolved;

        var identity = await IdentifyFromFileAsync(filePath, ct);

        // The header is applied outright; the name is only offered. See FileIdentity.
        var withKind = resolved with { AssetKind = identity.AssetKind };
        return identity.FromHeader is not null
            ? withKind with { BaseModelRaw = identity.FromHeader }
            : withKind with { NameGuess = identity.FromName };
    }

    /// <summary>
    /// Why the authoritative rungs did not produce a base model. The distinction is what licenses a
    /// guess: "Civitai does not know this file" is an answer; "we never got to ask" is not, and the
    /// sorter moves or copies bytes off this value.
    /// </summary>
    private enum AuthorityVerdict
    {
        /// <summary>An authoritative source supplied a base model. Nothing may overrule it.</summary>
        Answered,

        /// <summary>
        /// Everything that could answer did, and none of them knows this file: a Civitai 404 (live
        /// or negatively cached), a sidecar carrying no base model, or no client to ask at all.
        /// Guessing is licensed here — nothing better is coming.
        /// </summary>
        NotKnown,

        /// <summary>
        /// We could not ask: the file would not hash, or the call failed (rate limit, outage,
        /// response-shape change). A file left unresolved sorts into Unknown, which is the honest
        /// bucket for "not known yet" — and unlike a wrong folder, it is where the user looks.
        /// </summary>
        CouldNotAsk,
    }

    /// <summary>
    /// Whether an authoritative source actually supplied a base model. Uses
    /// <see cref="LoraPathBuilder.IsPlaceholderBaseModel"/> — the same predicate
    /// <c>BuildTargetDirectory</c> uses to pick the Unknown bucket — so "resolved enough to skip the
    /// file's own rungs" and "resolved enough to earn a real folder" cannot drift apart. A "???"
    /// arriving from a sidecar or an older cache entry is a placeholder to both, not just to one.
    /// </summary>
    private static AuthorityVerdict VerdictFor(string? baseModelRaw)
        => LoraPathBuilder.IsPlaceholderBaseModel(baseModelRaw)
            ? AuthorityVerdict.NotKnown
            : AuthorityVerdict.Answered;

    /// <summary>
    /// What the file says about itself: its safetensors header first (it read the actual weights),
    /// then its file name (a guess about them). <see cref="FileIdentity.None"/> when neither says
    /// anything usable. The two are returned separately, not resolved into one answer, because only
    /// the caller knows whether a guess is allowed to move a file.
    /// </summary>
    /// <remarks>
    /// Asked only once every authoritative source has come up empty — and only when they came up
    /// empty with an <i>answer</i> rather than a failure (see <see cref="AuthorityVerdict"/>) — so a
    /// sidecar or Civitai answer is never overruled by a guess about it. Public because the sorter's
    /// DB-known branch needs the same two rungs for rows still carrying the <c>"???"</c> placeholder
    /// <c>ModelFileSyncService</c> stamps on locally-discovered models; that branch already has the
    /// database's answer and must not re-hash or re-call the API to get these.
    /// <para>
    /// Both rungs emit verbatim Civitai display labels, so a file identified this way lands in the
    /// same base-model folder as a Civitai-identified one instead of a folder only it uses.
    /// </para>
    /// <para>
    /// The answer is deliberately NOT written to the per-hash cache. That cache means "what Civitai
    /// said for this hash", and the API-failure path writes no entry precisely so the next pass can
    /// retry — caching a guess there would kill that retry permanently and freeze the guess as
    /// though it were an answer. Re-deriving it each pass costs one size-capped header read.
    /// </para>
    /// </remarks>
    public async Task<FileIdentity> IdentifyFromFileAsync(string filePath, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(filePath);

        var header = await SafetensorsHeaderReader.TryReadAsync(filePath, ct);

        // From the SAME header read — the weights answer both questions (what it was trained on,
        // and what it is), and opening the file twice for them would be the duplication this
        // class's own remarks argue against.
        var assetKind = AssetKindResolver.Resolve(header, fileName);
        var containerWasUnreadable = AssetKindResolver.ContainerWasUnreadable(header, fileName);

        var fromHeader = header is null ? null : BaseModelHeaderMap.Map(header);
        if (fromHeader is not null)
        {
            _logger?.Debug(LogCategory.FileSystem, LogSource,
                $"{fileName}: nothing on record knows this file; its own safetensors header says {fromHeader}.");
            return new FileIdentity(fromHeader, null)
                { AssetKind = assetKind, ContainerWasUnreadable = containerWasUnreadable };
        }

        // GetFileNameWithoutExtension, matching IdentifyModelStep's call site exactly. The heuristic
        // strips a KNOWN model extension itself and ".pth" is not one of them, so passing the full
        // name would leave a stray "pth" token the database-side path never sees. Stripping here is
        // safe precisely because these paths always carry a real extension — they were enumerated by
        // one — so the double-strip that would eat the ".5" off "detailer_sd1.5" cannot arise.
        var fromName = FilenameBaseModelHeuristic.Guess(Path.GetFileNameWithoutExtension(filePath));
        if (fromName is not null)
        {
            _logger?.Debug(LogCategory.FileSystem, LogSource,
                $"{fileName}: no usable header either; its name suggests {fromName} — offered, not applied.");
        }

        return fromName is null
            ? FileIdentity.None with { AssetKind = assetKind, ContainerWasUnreadable = containerWasUnreadable }
            : new FileIdentity(null, fromName)
                { AssetKind = assetKind, ContainerWasUnreadable = containerWasUnreadable };
    }

    /// <summary>
    /// The authoritative rungs: local <c>.civitai.info</c> sidecar → per-hash disk cache → Civitai
    /// by-hash API (result cached), paired with a verdict saying whether an empty answer means
    /// "nothing knows this file" or "we could not ask" — only the first licenses a guess.
    /// </summary>
    private async Task<(ResolvedLoraMetadata Metadata, AuthorityVerdict Verdict)> ResolveFromSidecarCacheOrApiAsync(
        string filePath, CancellationToken ct)
    {
        // A sidecar carrying only an "id" (or a blank baseModel) is a hit that answers a different
        // question. It ends the chain without hashing or calling anything, so nothing better is
        // coming for the base model and the file itself may speak. Such a file does reach the
        // planner with an empty Sha256 — but it is perfectly hashable, and the planner hashes
        // lazily, so its duplicate guard still works. That is what separates this from the
        // could-not-hash branch below.
        if (TryReadSidecar(filePath, out var sidecarMeta))
            return (sidecarMeta!, VerdictFor(sidecarMeta!.BaseModelRaw));

        string sha;
        try
        {
            sha = _hashFile(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A .safetensors currently open in ComfyUI/A1111, a denied ACL, or a file that
            // vanished between enumeration and use. One bad file out of 3000 must not kill
            // the pass — it just sorts as unresolved.
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not hash {filePath}: {ex.Message}");

            // CouldNotAsk, not NotKnown. No hash means the by-hash lookup never happened — and the
            // file also reaches the planner with an empty Sha256, the one value its "identical
            // content is already there, skip it" guard needs, with no second chance if the lock
            // outlives the pass. The header opens FileShare.ReadWrite and so reads happily off a
            // file a trainer holds mid-checkpoint, which is exactly when the hasher's FileShare.Read
            // fails: guessing here would file that file into the populated folder where its twin
            // actually lives, turning a skip into a renamed duplicate. Unknown is the honest bucket.
            return (new ResolvedLoraMetadata(null, null, string.Empty), AuthorityVerdict.CouldNotAsk);
        }

        // A cache entry written before the model lookup existed — or by a pass whose model lookup
        // failed — carries no tag list at all, which is not the same as "this model has no tags".
        // Serving it would make a category-less result permanent for that hash, so it is re-resolved
        // once a client is available; a resolved-but-empty list is served as-is and never re-fetched.
        var hasCached = TryReadCache(sha, out var cached, out var tagsResolved);
        if (hasCached && (tagsResolved || _civitaiClient is null))
        {
            // A negatively cached hash IS an answer: Civitai was asked once and returned 404.
            var fromCache = cached! with { Sha256 = sha };
            return (fromCache, VerdictFor(fromCache.BaseModelRaw));
        }

        if (_civitaiClient is null)
        {
            // Permanent, not transient: no later pass will ask either, so the file itself is the
            // only thing that can ever answer for it.
            return (new ResolvedLoraMetadata(null, null, sha), AuthorityVerdict.NotKnown);
        }

        CivitaiModelVersion? version;
        try
        {
            version = await _civitaiClient.GetModelVersionByHashAsync(sha, await GetApiKeyAsync(), ct);
        }
        // JsonException: CivitaiClient.DeserializeOrThrow raises it on a response-shape
        // change, and this repo has been bitten by exactly that twice (allowCommercialUse
        // string→array, null stats counters). A shape change must degrade to "unresolved",
        // not abort a 3000-file preview.
        //
        // TaskCanceledException only when the caller did NOT cancel: HttpClient reports its own
        // timeout that way, which is a network failure like any other. A user pressing Cancel
        // during a 1000-file resolve raises the same type, and swallowing that logged the cancel
        // as "Civitai by-hash lookup failed … A task was canceled", returned unresolved metadata
        // for a file that was never looked up, and let the pass run one more file before the loop's
        // next ThrowIfCancellationRequested noticed. Cancellation must unwind the pass.
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger?.Warn(LogCategory.Network, LogSource,
                $"Civitai by-hash lookup failed for {filePath}: {ex.Message}");

            // A refresh attempted ONLY for a missing tag list must not cost the base model the
            // entry already carried. Every tag-less entry — written whenever TryReadModelTagsAsync
            // failed, plus every entry written before tag caching existed — reaches this call with
            // a perfectly good answer on record, and returning a blank record here both threw that
            // answer away and downgraded the verdict, which stopped the header rung from putting it
            // back. During the outage this branch is about ("one 429 tends to mean every file after
            // it") that moved a whole library of them into Unknown\.
            //
            // The recorded answer keeps its own verdict: a real base model is still an answer, and
            // a recorded blank is still Civitai saying so, which leaves the file's own rungs
            // licensed. The tag list stays unresolved either way, so the next pass retries it.
            if (hasCached)
            {
                var onRecord = cached! with { Sha256 = sha };
                return (onRecord, VerdictFor(onRecord.BaseModelRaw));
            }

            // CouldNotAsk. CivitaiClient.GetAsync returns null ONLY for a 404; a rate limit that
            // survived its three retries, an outage, a non-transient 4xx/5xx and a response-shape
            // change all arrive here instead, and used to be indistinguishable from "Civitai does
            // not know this file". This path is serial and unpaced, so one 429 tends to mean every
            // file after it — and a guess acted on by a move is not undone by "the next pass
            // retries", because in move mode the file has already left the source folder.
            return (new ResolvedLoraMetadata(null, null, sha), AuthorityVerdict.CouldNotAsk);
        }

        if (version is null)
        {
            // Fully resolved negative result: nothing to look tags up for, so the empty list is
            // final and this hash is never re-queried.
            WriteCache(sha, new CacheEntry(null, null, []));
            return (new ResolvedLoraMetadata(null, null, sha), AuthorityVerdict.NotKnown);
        }

        var tags = await TryReadModelTagsAsync(version.ModelId, ct);
        WriteCache(sha, new CacheEntry(version.BaseModel, version.Id, tags));
        var resolved = new ResolvedLoraMetadata(version.BaseModel, version.Id, sha) { Tags = tags ?? [] };

        // Civitai can answer with a blank baseModel. It answered, so nothing better is coming — and
        // the file may still be able to say what the response did not.
        return (resolved, VerdictFor(resolved.BaseModelRaw));
    }

    /// <summary>
    /// The by-hash endpoint returns a model <i>version</i>, and tags live on the owning model —
    /// so the category of a file with no sidecar (the whole point of "browse any folder": LoRAs
    /// downloaded outside DiffusionNexus) needs this second call. Failure is non-fatal and
    /// returns null, meaning "not resolved": the base-model/version-id result is still cached and
    /// returned, and the tag list is left open so a later pass can fill it in.
    /// Memoized per pass (see <see cref="_modelTagsMemo"/>) — a folder of sibling versions of one
    /// model otherwise paid this round-trip once per file.
    /// </summary>
    private async Task<IReadOnlyList<string>?> TryReadModelTagsAsync(int modelId, CancellationToken ct)
    {
        if (_civitaiClient is null || modelId <= 0) return [];

        if (_modelTagsMemo.TryGetValue(modelId, out var memoized))
            return memoized;

        try
        {
            var model = await _civitaiClient.GetModelAsync(modelId, await GetApiKeyAsync(), ct);
            var tags = model?.Tags ?? [];
            _modelTagsMemo[modelId] = tags;
            return tags;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger?.Warn(LogCategory.Network, LogSource,
                $"Civitai model lookup for tags failed (model {modelId}): {ex.Message} — the file keeps its " +
                "base model but sorts without a category this pass.");
            _modelTagsMemo[modelId] = null;
            return null;
        }
    }

    /// <summary>
    /// One key read per resolution pass, not per file: in production the provider opens a
    /// DI scope, resolves IAppSettingsService and queries the DB, so a folder of 1000
    /// unresolved LoRAs meant 1000 scopes, 1000 DbContexts and 1000 queries for a value
    /// that cannot change mid-pass. The task itself is cached, so overlapping callers share
    /// the single read; it never faults, because a missing key just means an anonymous
    /// by-hash request.
    /// </summary>
    private Task<string?> GetApiKeyAsync() => _apiKeyTask ??= LoadApiKeyAsync();

    private async Task<string?> LoadApiKeyAsync()
    {
        try
        {
            return await _apiKeyProvider();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.Network, LogSource,
                $"Could not read the Civitai API key; continuing unauthenticated: {ex.Message}");
            return null;
        }
    }

    private bool TryReadSidecar(string filePath, out ResolvedLoraMetadata? metadata)
    {
        metadata = null;
        var sidecarPath = Path.Combine(
            Path.GetDirectoryName(filePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(filePath) + ".civitai.info");

        if (!File.Exists(sidecarPath))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            var root = doc.RootElement;
            string? baseModel = root.TryGetProperty("baseModel", out var bmProp) && bmProp.ValueKind == JsonValueKind.String
                ? bmProp.GetString()
                : null;
            int? id = root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt32()
                : null;

            // Valid JSON with neither field present/non-null isn't a usable hit —
            // fall through to hash/cache/API instead of returning an empty result.
            if (baseModel is null && id is null)
                return false;

            metadata = new ResolvedLoraMetadata(baseModel, id, Sha256: "") { Tags = ReadTags(root) };
            return true;
        }
        catch (JsonException ex)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Malformed .civitai.info sidecar at {sidecarPath}: {ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not read .civitai.info sidecar at {sidecarPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Tag names from a <c>.civitai.info</c> document: <c>model.tags</c> (what the download
    /// pipeline writes) or a top-level <c>tags</c> array. Entries may be plain strings or
    /// objects carrying a <c>name</c> — both shapes occur in the wild.
    /// </summary>
    private static IReadOnlyList<string> ReadTags(JsonElement root)
    {
        if (root.TryGetProperty("model", out var model)
            && model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("tags", out var modelTags)
            && modelTags.ValueKind == JsonValueKind.Array)
        {
            return ToTagNames(modelTags);
        }

        return root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? ToTagNames(tags)
            : [];
    }

    private static IReadOnlyList<string> ToTagNames(JsonElement array)
    {
        var names = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var name = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("name", out var n)
                    && n.ValueKind == JsonValueKind.String => n.GetString(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Cache file for a digest. The name is lower-cased so the store is case-insensitive:
    /// the hasher now emits uppercase (<c>FileHasher.Sha256Upper</c>, the library-wide
    /// convention) and every entry written by an earlier build is lowercase — keying on the
    /// digest as-is would silently orphan the whole existing cache and re-fetch every file.
    /// </summary>
    private string CachePathFor(string sha) => Path.Combine(_cacheDirectory, $"{sha.ToLowerInvariant()}.json");

    /// <param name="tagsResolved">False when the entry predates tag caching or was written by a
    /// pass whose model lookup failed — see the call site.</param>
    private bool TryReadCache(string sha, out ResolvedLoraMetadata? metadata, out bool tagsResolved)
    {
        metadata = null;
        tagsResolved = false;
        var cachePath = CachePathFor(sha);
        if (!File.Exists(cachePath))
            return false;

        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(cachePath), CacheJsonOptions);
            if (entry is null)
                throw new JsonException("Deserialized to null.");

            metadata = new ResolvedLoraMetadata(entry.BaseModel, entry.VersionId, sha)
            {
                Tags = entry.Tags ?? []
            };
            tagsResolved = entry.Tags is not null;
            return true;
        }
        catch (JsonException ex)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Malformed cache entry at {cachePath}, discarding: {ex.Message}");
            try { File.Delete(cachePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not read cache entry at {cachePath}: {ex.Message}");
            return false;
        }
    }

    private void WriteCache(string sha, CacheEntry entry)
    {
        var cachePath = CachePathFor(sha);
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(entry, CacheJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not write cache entry at {cachePath}: {ex.Message}");
        }
    }

    /// <param name="Tags">Null means "tags were never resolved for this hash" (an entry written
    /// before tag caching existed, or one whose model lookup failed); an empty list means the
    /// model really has none. The distinction is what stops a transient API failure from making a
    /// file category-less forever.</param>
    private sealed record CacheEntry(string? BaseModel, int? VersionId, IReadOnlyList<string>? Tags = null);
}
