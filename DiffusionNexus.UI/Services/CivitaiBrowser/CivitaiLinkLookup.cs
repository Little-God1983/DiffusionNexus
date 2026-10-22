using System.Diagnostics;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>One model a link or id resolved to, and the version the link pointed at (if any).</summary>
/// <param name="Model">The full model, with every version — it becomes a normal browser card.</param>
/// <param name="VersionId">Version to pre-tick; null pre-ticks the latest.</param>
/// <param name="MatchLabel">Short card badge saying why the card is there ("Model ID 123").</param>
public sealed record CivitaiLinkMatch(CivitaiModel Model, int? VersionId, string MatchLabel);

/// <summary>Result of <see cref="CivitaiLinkLookup.ResolveAsync"/>: the cards to show, or why there are none.</summary>
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

    public async Task<CivitaiLinkLookupResult> ResolveAsync(CivitaiLinkQuery query, string? apiKey, CancellationToken ct)
    {
        _logger?.Info(LogCategory.Network, LogSource, $"Link lookup: {query}");

        if (query.IsLinkWithoutIds)
        {
            return new([], "That Civitai link does not point at a model. Paste a model page link (…/models/123).");
        }

        var matches = query.IsBareId
            ? await ResolveBareIdAsync(query.ModelId!.Value, apiKey, ct)
            : await ResolveLinkAsync(query, apiKey, ct);

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
            return new(showable, null);
        }

        var message = unavailable.Count > 0
            ? $"\"{unavailable[0].Model.Name}\" is no longer available on Civitai (archived or taken down)."
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

    private async Task<List<CivitaiLinkMatch>> ResolveLinkAsync(CivitaiLinkQuery query, string? apiKey, CancellationToken ct)
    {
        if (query.ModelId is int modelId)
        {
            var model = await GetModelAsync(modelId, apiKey, ct);
            if (model is null) return [];

            // A version link whose version the model no longer lists still shows the model; the
            // card then pre-ticks the latest, the same as a plain model link.
            var versionId = query.VersionId is int v && model.ModelVersions.Any(mv => mv.Id == v) ? v : (int?)null;
            if (query.VersionId is not null && versionId is null)
            {
                _logger?.Info(LogCategory.Network, LogSource,
                    $"Link lookup: version {query.VersionId} is not listed on model {modelId}; pre-ticking the latest instead.");
            }
            return [new(model, versionId, versionId is null ? $"Model ID {modelId}" : $"Version ID {versionId}")];
        }

        var byVersion = await GetModelOfVersionAsync(query.VersionId!.Value, apiKey, ct);
        return byVersion is null ? [] : [new(byVersion, query.VersionId, $"Version ID {query.VersionId}")];
    }

    private async Task<List<CivitaiLinkMatch>> ResolveBareIdAsync(int id, string? apiKey, CancellationToken ct)
    {
        var modelTask = GetModelAsync(id, apiKey, ct);
        var versionTask = GetModelOfVersionAsync(id, apiKey, ct);
        await Task.WhenAll(modelTask, versionTask);
        var asModel = modelTask.Result;
        var asVersion = versionTask.Result;

        // The number is a version of the very model it also names: one card, the version pre-ticked.
        if (asModel is not null && asVersion is not null && asModel.Id == asVersion.Id)
        {
            return [new(asVersion, id, $"Model + version ID {id}")];
        }

        var matches = new List<CivitaiLinkMatch>();
        if (asModel is not null) matches.Add(new(asModel, null, $"Model ID {id}"));
        if (asVersion is not null) matches.Add(new(asVersion, id, $"Version ID {id}"));
        return matches;
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
    private async Task<CivitaiModel?> GetModelOfVersionAsync(int versionId, string? apiKey, CancellationToken ct)
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
        return await GetModelAsync(version.ModelId, apiKey, ct);
    }
}
