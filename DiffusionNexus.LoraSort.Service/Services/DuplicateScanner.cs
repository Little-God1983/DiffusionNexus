using DiffusionNexus.LoraSort.Service.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DiffusionNexus.LoraSort.Service.Services;

public record DuplicateSet(FileInfo FileA, FileInfo FileB, long SizeBytes, ModelClass? MetaA, ModelClass? MetaB);

public record ScanProgress(string? CurrentFile, int Processed, int Total, string? Error = null);

public class DuplicateScanner
{
    public async Task<IReadOnlyList<DuplicateSet>> ScanAsync(string folder, IProgress<ScanProgress>? progress = null, CancellationToken token = default)
    {
        var files = GetCandidateFiles(folder);
        var total = files.Count;
        if (total < 2) return Array.Empty<DuplicateSet>();

        var metaLookup = await BuildMetadataLookupAsync(folder, token);

        var results = new List<DuplicateSet>();
        int processed = 0;

        foreach (var group in files.GroupBy(f => f.Length))
        {
            processed = await ProcessSizeGroupAsync(group.ToList(), metaLookup, progress, total, processed, results, token);
        }

        return results;
    }

    private static List<FileInfo> GetCandidateFiles(string folder)
    {
        return Directory.EnumerateFiles(folder, "*.safetensors", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f)).ToList();
    }

    private static async Task<Dictionary<string, ModelClass>> BuildMetadataLookupAsync(string folder, CancellationToken token)
    {
        var reader = new JsonInfoFileReaderService(folder, string.Empty);
        var metas = await reader.GetModelData(null, folder, token, fetchFromApi: false);
        var lookup = new Dictionary<string, ModelClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in metas)
        {
            foreach (var fi in m.AssociatedFilesInfo)
            {
                if (fi.Extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase))
                    lookup[fi.FullName] = m;
            }
        }
        return lookup;
    }

    private static async Task<int> ProcessSizeGroupAsync(List<FileInfo> files, Dictionary<string, ModelClass> metaLookup, IProgress<ScanProgress>? progress, int total, int processed, List<DuplicateSet> results, CancellationToken token)
    {
        if (files.Count < 2)
        {
            foreach (var f in files)
            {
                processed++;
                progress?.Report(new ScanProgress(f.FullName, processed, total));
            }
            return processed;
        }

        var hashes = new Dictionary<string, List<FileInfo>>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress(file.FullName, processed, total));
            try
            {
                var hash = await ComputeHashAsync(file.FullName, token);
                if (!hashes.TryGetValue(hash, out var list))
                    hashes[hash] = list = new();
                list.Add(file);
            }
            catch (IOException ex)
            {
                progress?.Report(new ScanProgress(file.FullName, processed, total, ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                progress?.Report(new ScanProgress(file.FullName, processed, total, ex.Message));
            }
            processed++;
        }

        await CompareCandidateFilesAsync(hashes, metaLookup, results, token);
        return processed;
    }

    private static async Task CompareCandidateFilesAsync(Dictionary<string, List<FileInfo>> hashes, Dictionary<string, ModelClass> metaLookup, List<DuplicateSet> results, CancellationToken token)
    {
        foreach (var kv in hashes)
        {
            var cand = kv.Value;
            if (cand.Count < 2) continue;

            for (int i = 0; i < cand.Count - 1; i++)
            {
                for (int j = i + 1; j < cand.Count; j++)
                {
                    var a = cand[i];
                    var b = cand[j];
                    if (await FilesEqualAsync(a.FullName, b.FullName, token))
                    {
                        metaLookup.TryGetValue(a.FullName, out var metaA);
                        metaLookup.TryGetValue(b.FullName, out var metaB);
                        results.Add(new DuplicateSet(a, b, a.Length, metaA, metaB));
                    }
                }
            }
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var bytes = await sha.ComputeHashAsync(stream, token);
        return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static async Task<bool> FilesEqualAsync(string path1, string path2, CancellationToken token)
    {
        const int buf = 81920;
        await using var fs1 = File.OpenRead(path1);
        await using var fs2 = File.OpenRead(path2);
        if (fs1.Length != fs2.Length) return false;
        var b1 = new byte[buf];
        var b2 = new byte[buf];
        int r1;
        while ((r1 = await fs1.ReadAsync(b1.AsMemory(0, buf), token)) > 0)
        {
            int r2 = await fs2.ReadAsync(b2.AsMemory(0, r1), token);
            if (r1 != r2 || !b1.AsSpan(0, r1).SequenceEqual(b2.AsSpan(0, r1)))
                return false;
        }
        return true;
    }
}
