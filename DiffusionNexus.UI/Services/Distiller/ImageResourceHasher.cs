using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services.Lora;

namespace DiffusionNexus.UI.Services.Distiller;

/// <summary>
/// A tracked model file from the library database: its filename, recorded on-disk path, and any
/// precomputed hashes (AutoV2 preferred; failing that SHA-256, whose first 10 hex chars ARE the AutoV2).
/// </summary>
internal readonly record struct TrackedModelFile(string FileName, string? LocalPath, string? AutoV2, string? Sha256);

/// <summary>
/// Computes AutoV2 (first 10 hex of SHA-256) hashes for a trace's checkpoint and LoRAs by resolving
/// their stems against the local catalogs. Names not found locally are omitted (name-only fallback).
/// Hashes are cached per (path,size,mtime) so a batch hashes each file once.
/// </summary>
internal sealed class ImageResourceHasher
{
    private static readonly string[] ModelExtensions = [".safetensors", ".ckpt", ".gguf", ".pt", ".sft", ".bin"];

    // Search a root and every subfolder, skip locked/no-permission subtrees, match case-insensitively
    // (Windows paths are case-insensitive and ComfyUI records model names in arbitrary case).
    private static readonly EnumerationOptions RecursiveSafe = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private readonly ILoraCatalog _loraCatalog;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _resolveModelsRoots;
    private readonly Func<CancellationToken, Task<IReadOnlyList<TrackedModelFile>>>? _resolveTrackedModelFiles;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _modelHashByStem = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<TrackedModelFile>? _trackedCache; // fetched once per hasher (a batch is sequential)

    public ImageResourceHasher(
        ILoraCatalog loraCatalog,
        Func<CancellationToken, Task<IReadOnlyList<string>>> resolveModelsRoots,
        Func<CancellationToken, Task<IReadOnlyList<TrackedModelFile>>>? resolveTrackedModelFiles = null)
    {
        _loraCatalog = loraCatalog;
        _resolveModelsRoots = resolveModelsRoots;
        _resolveTrackedModelFiles = resolveTrackedModelFiles;
    }

    public async Task<ResourceHashes> ComputeAsync(string? checkpointStem, IReadOnlyList<LoraInfo> loras, CancellationToken ct)
    {
        // LoRAs resolve via the same catalog the LoRA Viewer's Installed tab uses (DB-backed, spans
        // every configured source) — this path is known-good, so it is left untouched.
        var installed = await _loraCatalog.GetInstalledLorasAsync(null, ct).ConfigureAwait(false);
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in installed)
        {
            var stem = Path.GetFileNameWithoutExtension(l.FilePath);
            byStem.TryAdd(stem, l.FilePath);
        }

        var loraHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lora in loras)
        {
            ct.ThrowIfCancellationRequested();
            if (byStem.TryGetValue(lora.Name, out var path) && HashCached(path) is { } h)
                loraHashes[lora.Name] = h;
        }

        var modelHash = await ResolveCheckpointHashAsync(checkpointStem, ct).ConfigureAwait(false);

        return new ResourceHashes(modelHash, loraHashes);
    }

    /// <summary>
    /// Resolves the checkpoint's AutoV2 hash. Mirrors how LoRAs resolve (via the library DB) so a
    /// checkpoint living in ANY registered install is found — the old single-root disk scan silently
    /// missed models outside the primary ComfyUI install (Civitai then couldn't match the model or
    /// derive the base model). Resolution order:
    ///   1. Library DB match by filename stem — reuse the stored AutoV2 / SHA-256 (no re-hash), or
    ///      failing that hash the DB's recorded LocalPath.
    ///   2. Filesystem scan across ALL model roots (not just the primary) — hash what we find.
    /// Unresolved returns null: that one resource just won't match on Civitai; everything else still does.
    /// </summary>
    private async Task<string?> ResolveCheckpointHashAsync(string? checkpointStem, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(checkpointStem)) return null;
        // A batch usually shares one checkpoint; memoize so the (recursive) filesystem scan runs once
        // per stem rather than once per image.
        if (_modelHashByStem.TryGetValue(checkpointStem, out var cached)) return cached;

        var resolved = await ResolveCheckpointHashCoreAsync(checkpointStem, ct).ConfigureAwait(false);
        _modelHashByStem[checkpointStem] = resolved;
        return resolved;
    }

    private async Task<string?> ResolveCheckpointHashCoreAsync(string checkpointStem, CancellationToken ct)
    {
        // A stem may have several DB rows (same file in two installs); the loop skips rows that yield
        // no usable hash and keeps looking, so a hashed row later in the list still wins.
        foreach (var f in await GetTrackedModelFilesAsync(ct).ConfigureAwait(false))
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(f.FileName), checkpointStem, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ToAutoV2(f.AutoV2) is { } a) return a;
            if (ToAutoV2(f.Sha256) is { } sha) return sha;
            if (!string.IsNullOrWhiteSpace(f.LocalPath) && File.Exists(f.LocalPath) && HashCached(f.LocalPath!) is { } h)
                return h;
        }

        var roots = await _resolveModelsRoots(ct).ConfigureAwait(false);
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var file = FindModelFile(root, checkpointStem);
            if (file is not null && HashCached(file) is { } h) return h;
        }
        return null;
    }

    /// <summary>
    /// The library's tracked model files, fetched at most once per hasher — a distill batch runs the
    /// items sequentially, so the (potentially large) DB read is shared instead of repeated per image.
    /// </summary>
    private async Task<IReadOnlyList<TrackedModelFile>> GetTrackedModelFilesAsync(CancellationToken ct)
    {
        if (_trackedCache is not null) return _trackedCache;
        if (_resolveTrackedModelFiles is null) return _trackedCache = [];
        try { return _trackedCache = await _resolveTrackedModelFiles(ct).ConfigureAwait(false); }
        catch { return _trackedCache = []; } // DB unavailable: fall through to the disk scan
    }

    /// <summary>Normalizes a stored hash to an AutoV2 (lowercase, first 10 hex). Null/blank -> null.</summary>
    private static string? ToAutoV2(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return null;
        var h = hash.Trim().ToLowerInvariant();
        return h.Length >= 10 ? h[..10] : h;
    }

    private string? HashCached(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            var key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            return _cache.GetOrAdd(key, _ => ComputeAutoV2(filePath));
        }
        catch { return null; }
    }

    /// <summary>
    /// Finds a model file by stem anywhere beneath <paramref name="modelsRoot"/>, across every model
    /// extension. Searches the WHOLE root — not a fixed set of subfolder names — because ComfyUI's
    /// extra_model_paths.yaml routes categories to arbitrarily-named folders (e.g. a shared library's
    /// "DiffusionModels"/"StableDiffusion") and the resolver hands those category roots to us directly,
    /// so the model often sits right in the root with no checkpoints/ or diffusion_models/ subfolder.
    /// </summary>
    private static string? FindModelFile(string? modelsRoot, string stem)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot) || !Directory.Exists(modelsRoot)) return null;
        try
        {
            // OS-filter to files that start with the stem, then confirm exact stem + model extension
            // (the pattern's '.' is literal, so a stem like "name_v3.1" still matches "name_v3.1.safetensors").
            foreach (var file in Directory.EnumerateFiles(modelsRoot, stem + ".*", RecursiveSafe))
            {
                if (ModelExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) &&
                    Path.GetFileNameWithoutExtension(file).Equals(stem, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        catch { /* unreadable tree / bad pattern: treat as not found and let other roots try */ }
        return null;
    }

    /// <summary>AutoV2 = first 10 lowercase hex chars of the file's full SHA-256. Null on I/O error.</summary>
    public static string? ComputeAutoV2(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant()[..10];
        }
        catch { return null; }
    }
}
