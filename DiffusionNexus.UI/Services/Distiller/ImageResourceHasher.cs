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
/// Computes AutoV2 (first 10 hex of SHA-256) hashes for a trace's checkpoint and LoRAs by resolving
/// their stems against the local catalogs. Names not found locally are omitted (name-only fallback).
/// Hashes are cached per (path,size,mtime) so a batch hashes each file once.
/// </summary>
internal sealed class ImageResourceHasher
{
    private static readonly string[] ModelExtensions = [".safetensors", ".ckpt", ".gguf", ".pt", ".sft", ".bin"];
    private static readonly string[] CheckpointSubfolders = ["checkpoints", "diffusion_models", "unet"];

    private readonly ILoraCatalog _loraCatalog;
    private readonly Func<CancellationToken, Task<string?>> _resolveModelsRoot;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageResourceHasher(ILoraCatalog loraCatalog, Func<CancellationToken, Task<string?>> resolveModelsRoot)
    {
        _loraCatalog = loraCatalog;
        _resolveModelsRoot = resolveModelsRoot;
    }

    public async Task<ResourceHashes> ComputeAsync(string? checkpointStem, IReadOnlyList<LoraInfo> loras, CancellationToken ct)
    {
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

        string? modelHash = null;
        if (!string.IsNullOrWhiteSpace(checkpointStem))
        {
            var root = await _resolveModelsRoot(ct).ConfigureAwait(false);
            var file = FindModelFile(root, checkpointStem);
            if (file is not null) modelHash = HashCached(file);
        }

        return new ResourceHashes(modelHash, loraHashes);
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

    private static string? FindModelFile(string? modelsRoot, string stem)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot) || !Directory.Exists(modelsRoot)) return null;
        foreach (var sub in CheckpointSubfolders)
        {
            var dir = Path.Combine(modelsRoot, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!ModelExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                if (Path.GetFileNameWithoutExtension(file).Equals(stem, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
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
