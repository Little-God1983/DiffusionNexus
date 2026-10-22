using System.Diagnostics;
using System.Runtime.ExceptionServices;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>One model a link or id resolved to, and the version the link pointed at (if any).</summary>
/// <param name="Model">The full model, with every version — it becomes a normal browser card.</param>
/// <param name="VersionId">Version to pre-tick; null pre-ticks the latest. Always one the model lists.</param>
/// <param name="MatchLabel">Short card badge saying why the card is there ("Model ID 123").</param>
public sealed record CivitaiLinkMatch(CivitaiModel Model, int? VersionId, string MatchLabel);

/// <summary>
/// Result of <see cref="CivitaiLinkLookup.ResolveAsync"/>: the cards to show, and a status line —
/// why there are none, or what the cards do not say on their own (a linked version the model does
/// not list, an id that could only be checked one way).
/// </summary>
public sealed record CivitaiLinkLookupResult(IReadOnlyList<CivitaiLinkMatch> Matches, string? Message);

/// <summary>
/// Turns a <see cref="CivitaiLinkQuery"/> into the models to show as browser cards (issue #630).
/// A link costs one request (two when it names only a version); a bare number is looked up as a
/// model id and as a version id in parallel, and each hit becomes its own card — "if it can be
/// two things it should yield two things".
/// </summary>
public sealed class CivitaiLinkLookup
{
    private const string LogSource = "CivitaiBrowser";

    private readonly ICivitaiClient _client;
    private readonly IUnifiedLogger? _logger;

    public CivitaiLinkLookup(ICivitaiClient client, IUnifiedLogger? logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>A version lookup: the version, and its model when Civitai still serves that model.</summary>
    private sealed record VersionHit(CivitaiModelVersion Version, CivitaiModel? Model);

    public async Task<CivitaiLinkLookupResult> ResolveAsync(CivitaiLinkQuery query, string? apiKey, CancellationToken ct)
    {
        _logger?.Info(LogCategory.Network, LogSource, $"Link lookup: {query}");

        if (query.IsLinkWithoutIds)
        {
            return new([], "That Civitai link does not point at a model. Paste a model page link (…/models/123).");
        }

        var notes = new List<string>();
        var matches = query.IsBareId
            ? await ResolveBareIdAsync(query.ModelId!.Value, apiKey, notes, ct)
            : await ResolveLinkAsync(query, apiKey, notes, ct);

        var unavailable = matches.Where(m => !IsShowable(m.Model)).ToList();
        foreach (var m in unavailable)
        {
            _logger?.Info(LogCategory.Network, LogSource,
                $"Link lookup: model {m.Model.Id} \"{m.Model.Name}\" skipped — archived, taken down or without versions (mode {m.Model.Mode?.ToString() ?? "none"}, {m.Model.ModelVersions.Count} versions).");
        }

        var showable = matches.Except(unavailable).ToList();
        if (showable.Count > 0)
        {
            _logger?.Info(LogCategory.Network, LogSource,
                $"Link lookup: showing {showable.Count} card(s): {string.Join(", ", showable.Select(m => $"{m.MatchLabel} → model {m.Model.Id} \"{m.Model.Name}\""))}");
            return new(showable, notes.Count == 0 ? null : string.Join(" ", notes));
        }

        var message = unavailable.Count > 0
            ? string.Join(" ", notes.Prepend($"\"{unavailable[0].Model.Name}\" is no longer available on Civitai (archived or taken down)."))
            : notes.Count > 0
                ? string.Join(" ", notes)
                : query.IsBareId
                    ? $"Civitai has no model and no version with ID {query.ModelId}."
                    : query.VersionId is int v && query.ModelId is null
                        ? $"Civitai has no version with ID {v}."
                        : $"Civitai has no model with ID {query.ModelId}.";
        _logger?.Info(LogCategory.Network, LogSource, $"Link lookup: nothing to show — {message}");
        return new([], message);
    }

    /// <summary>Same rule the name search applies to its pages (archived/taken-down and empty models are dropped).</summary>
    private static bool IsShowable(CivitaiModel model) => model.Mode is null && model.ModelVersions.Count > 0;

    private async Task<List<CivitaiLinkMatch>> ResolveLinkAsync(
        CivitaiLinkQuery query, string? apiKey, List<string> notes, CancellationToken ct)
    {
        if (query.ModelId is int modelId)
        {
            var model = await GetModelAsync(modelId, apiKey, ct);
            if (model is null) return [];
            return [MatchFor(model, query.VersionId, notes)];
        }

        var versionId = query.VersionId!.Value;
        var hit = await GetVersionAsync(versionId, apiKey, ct);
        if (hit is null) return [];
        if (hit.Model is null)
        {
            notes.Add(GoneModelNote(hit));
            return [];
        }
        return [MatchFor(hit.Model, versionId, notes)];
    }

    private async Task<List<CivitaiLinkMatch>> ResolveBareIdAsync(
        int id, string? apiKey, List<string> notes, CancellationToken ct)
    {
        // Each half on its own: a 429 or 5xx on one must not throw away what the other found.
        var modelTask = Attempt(() => GetModelAsync(id, apiKey, ct), ct);
        var versionTask = Attempt(() => GetVersionAsync(id, apiKey, ct), ct);
        await Task.WhenAll(modelTask, versionTask);
        var (asModel, modelError) = modelTask.Result;
        var (asVersion, versionError) = versionTask.Result;

        if (modelError is not null && versionError is not null)
        {
            ExceptionDispatchInfo.Capture(modelError).Throw();
        }
        // One half failed: say what the other half found too, so a lone failure note can't read as
        // if something had been found.
        if (modelError is not null)
        {
            if (asVersion is null) notes.Add($"Civitai has no version with ID {id}.");
            notes.Add($"Could not check whether {id} is a model ID ({modelError.Message}).");
        }
        if (versionError is not null)
        {
            if (asModel is null) notes.Add($"Civitai has no model with ID {id}.");
            notes.Add($"Could not check whether {id} is a version ID ({versionError.Message}).");
        }

        var matches = new List<CivitaiLinkMatch>();
        // The number is a version of the very model it also names: one card, the version pre-ticked.
        if (asModel is not null && asVersion?.Model is { } sameModel && sameModel.Id == asModel.Id)
        {
            matches.Add(MatchFor(sameModel, id, notes, listedLabel: $"Model + version ID {id}"));
            return matches;
        }

        if (asModel is not null) matches.Add(new(asModel, null, $"Model ID {id}"));
        if (asVersion is not null)
        {
            if (asVersion.Model is null) notes.Add(GoneModelNote(asVersion));
            else matches.Add(MatchFor(asVersion.Model, id, notes));
        }
        return matches;
    }

    /// <summary>
    /// Builds a card match that pre-ticks <paramref name="versionId"/> only when the model actually
    /// lists it. A version Civitai serves on its own endpoint but not in the model's list (unpublished,
    /// hidden) would otherwise leave a card badged "Version ID N" with the latest version ticked, so a
    /// queue click would download a version the badge does not name. The card then pre-ticks the
    /// latest under a model badge, and the status line says so.
    /// </summary>
    private CivitaiLinkMatch MatchFor(CivitaiModel model, int? versionId, List<string> notes, string? listedLabel = null)
    {
        if (versionId is not int v) return new(model, null, $"Model ID {model.Id}");
        if (model.ModelVersions.Any(mv => mv.Id == v)) return new(model, v, listedLabel ?? $"Version ID {v}");

        _logger?.Info(LogCategory.Network, LogSource,
            $"Link lookup: version {v} is not listed on model {model.Id}; pre-ticking the latest instead.");
        notes.Add($"Version {v} is not listed on \"{model.Name}\" on Civitai, so its latest version is ticked instead.");
        return new(model, null, $"Model ID {model.Id}");
    }

    private static string GoneModelNote(VersionHit hit) =>
        $"Version {hit.Version.Id} belongs to model {hit.Version.ModelId}, which Civitai no longer serves (deleted or private).";

    private static async Task<(T? Value, Exception? Error)> Attempt<T>(Func<Task<T?>> call, CancellationToken ct)
        where T : class
    {
        try
        {
            return (await call(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, ex);
        }
    }

    private async Task<CivitaiModel?> GetModelAsync(int modelId, string? apiKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var model = await _client.GetModelAsync(modelId, apiKey, ct);
        _logger?.Info(LogCategory.Network, LogSource,
            model is null
                ? $"Link lookup: GET models/{modelId} → not found ({sw.ElapsedMilliseconds} ms)"
                : $"Link lookup: GET models/{modelId} → \"{model.Name}\", {model.ModelVersions.Count} version(s) ({sw.ElapsedMilliseconds} ms)");
        return model;
    }

    /// <summary>The version endpoint does not carry the sibling versions, so the card needs the model too.</summary>
    private async Task<VersionHit?> GetVersionAsync(int versionId, string? apiKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var version = await _client.GetModelVersionAsync(versionId, apiKey, ct);
        if (version is null || version.ModelId <= 0)
        {
            _logger?.Info(LogCategory.Network, LogSource,
                $"Link lookup: GET model-versions/{versionId} → not found ({sw.ElapsedMilliseconds} ms)");
            return null;
        }
        _logger?.Info(LogCategory.Network, LogSource,
            $"Link lookup: GET model-versions/{versionId} → \"{version.Name}\" of model {version.ModelId} ({sw.ElapsedMilliseconds} ms)");
        return new(version, await GetModelAsync(version.ModelId, apiKey, ct));
    }
}
