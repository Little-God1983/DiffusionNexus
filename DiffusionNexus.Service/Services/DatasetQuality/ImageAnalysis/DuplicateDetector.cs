using System.Security.Cryptography;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;

/// <summary>
/// A cluster of images that are either exact or near-duplicates.
/// </summary>
/// <param name="ImagePaths">Absolute paths of the duplicate images in this cluster.</param>
/// <param name="HammingDistance">Hamming distance between the pHash values (0 for exact duplicates).</param>
/// <param name="SimilarityPercent">Similarity percentage (100% for exact matches).</param>
/// <param name="IsExactDuplicate">True when the SHA-256 file hashes are identical.</param>
public record DuplicateCluster(
    IReadOnlyList<string> ImagePaths,
    int HammingDistance,
    double SimilarityPercent,
    bool IsExactDuplicate);

/// <summary>
/// Detects duplicate images in a dataset using a two-tier approach:
/// <list type="bullet">
///   <item><b>Tier 1 — Exact duplicates:</b> SHA-256 file hash. O(n).</item>
///   <item><b>Tier 2 — Near-duplicates:</b> Perceptual hash (pHash) with Hamming distance &lt; 5. O(n²).</item>
/// </list>
/// Duplicate training images waste compute and bias the model toward repeated content.
/// </summary>
public sealed class DuplicateDetector : IImageQualityCheck
{
    /// <summary>Check name used on all generated issues.</summary>
    public const string CheckDisplayName = "Duplicate Detection";

    public string Name => CheckDisplayName;
    public string Description => "Detects exact and near-duplicate images using SHA-256 and perceptual hashing.";
    public int Order => 30;
    public bool RequiresGpu => false;
    public QualityScoreCategory Category => QualityScoreCategory.DatasetConsistency;

    public bool IsApplicable(LoraType loraType) => true;

    /// <summary>
    /// Stores the duplicate clusters found during the last analysis run.
    /// Allows UI consumers to display cluster details.
    /// </summary>
    public IReadOnlyList<DuplicateCluster> LastClusters { get; private set; } = [];

    public async Task<ImageCheckResult> RunAsync(
        IReadOnlyList<ImageFileInfo> images,
        DatasetConfig config,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(config);

        if (images.Count < 2)
        {
            LastClusters = [];
            return new ImageCheckResult
            {
                Score = 100,
                CheckName = Name,
                Issues = [],
                PerImageScores = []
            };
        }

        // --- Tier 1: SHA-256 exact duplicates ---
        var sha256ByPath = new Dictionary<string, string>(images.Count, StringComparer.OrdinalIgnoreCase);
        var sha256Groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        for (int i = 0; i < images.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = images[i].FilePath;

            string hash = await ComputeSha256Async(path, cancellationToken);
            sha256ByPath[path] = hash;

            if (!sha256Groups.TryGetValue(hash, out var list))
                sha256Groups[hash] = list = [];
            list.Add(path);

            progress?.Report((double)(i + 1) / images.Count * 0.4); // 0–40%
        }

        var exactClusters = sha256Groups.Values
            .Where(g => g.Count > 1)
            .Select(g => new DuplicateCluster(g, HammingDistance: 0, SimilarityPercent: 100.0, IsExactDuplicate: true))
            .ToList();

        // Collect paths already in exact-duplicate clusters to skip in Tier 2
        var exactDuplicatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cluster in exactClusters)
        {
            foreach (var p in cluster.ImagePaths)
                exactDuplicatePaths.Add(p);
        }

        // --- Tier 2: pHash near-duplicates ---
        var hashByPath = new Dictionary<string, ulong>(images.Count, StringComparer.OrdinalIgnoreCase);
        var uniquePaths = images
            .Select(img => img.FilePath)
            .Where(p => !exactDuplicatePaths.Contains(p))
            .ToList();

        // Also include one representative from each exact cluster for near-dup comparison
        foreach (var cluster in exactClusters)
        {
            uniquePaths.Add(cluster.ImagePaths[0]);
        }

        for (int i = 0; i < uniquePaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = uniquePaths[i];

            ulong phash = await Task.Run(() => PerceptualHasher.ComputeHash(path), cancellationToken);
            hashByPath[path] = phash;

            progress?.Report(0.4 + (double)(i + 1) / uniquePaths.Count * 0.4); // 40–80%
        }

        // O(n²) pHash comparison for near-duplicates
        var nearClusters = FindNearDuplicateClusters(hashByPath);

        // Combine clusters
        var allClusters = new List<DuplicateCluster>(exactClusters.Count + nearClusters.Count);
        allClusters.AddRange(exactClusters);
        allClusters.AddRange(nearClusters);
        LastClusters = allClusters;

        // --- Build results ---
        var duplicateImagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cluster in allClusters)
        {
            foreach (var p in cluster.ImagePaths)
                duplicateImagePaths.Add(p);
        }

        var perImageScores = new List<PerImageScore>(images.Count);
        foreach (var img in images)
        {
            bool isDuplicate = duplicateImagePaths.Contains(img.FilePath);
            double score = isDuplicate ? 0 : 100;
            string detail = isDuplicate ? "Duplicate detected" : "Unique";
            perImageScores.Add(new PerImageScore(img.FilePath, score, detail));
        }

        var issues = new List<Issue>();

        if (exactClusters.Count > 0)
        {
            int totalExact = exactClusters.Sum(c => c.ImagePaths.Count);
            var affectedFiles = exactClusters.SelectMany(c => c.ImagePaths).ToList();
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{exactClusters.Count} exact duplicate group(s) found ({totalExact} images total).",
                Details = "Identical files (same SHA-256 hash) waste training compute and bias the model. "
                        + "Remove all but one copy from each group.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = affectedFiles
            });
        }

        if (nearClusters.Count > 0)
        {
            int totalNear = nearClusters.Sum(c => c.ImagePaths.Count);
            var affectedFiles = nearClusters.SelectMany(c => c.ImagePaths).ToList();
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{nearClusters.Count} near-duplicate group(s) found ({totalNear} images total).",
                Details = "These images are visually very similar (pHash Hamming distance < "
                        + $"{PerceptualHasher.NearDuplicateThreshold}). Near-duplicates over-represent "
                        + "certain poses or compositions. Review and consider removing extras.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = affectedFiles
            });
        }

        progress?.Report(1.0);

        // Score: penalize based on ratio of duplicate images
        double uniqueRatio = images.Count > 0
            ? (double)(images.Count - duplicateImagePaths.Count) / images.Count
            : 1.0;
        double overallScore = Math.Round(uniqueRatio * 100, 1);

        return new ImageCheckResult
        {
            Score = overallScore,
            CheckName = Name,
            Issues = issues,
            PerImageScores = perImageScores
        };
    }

    /// <summary>
    /// Finds clusters of near-duplicate images via O(n²) pHash comparison.
    /// Uses union-find to group transitive near-duplicates.
    /// </summary>
    internal static List<DuplicateCluster> FindNearDuplicateClusters(Dictionary<string, ulong> hashByPath)
    {
        var paths = hashByPath.Keys.ToList();
        int n = paths.Count;

        // Union-Find
        int[] parent = new int[n];
        int[] rank = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        // Track minimum hamming distance within each eventual cluster
        var pairDistances = new List<(int a, int b, int distance)>();

        for (int i = 0; i < n - 1; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                int dist = PerceptualHasher.HammingDistance(hashByPath[paths[i]], hashByPath[paths[j]]);
                if (dist < PerceptualHasher.NearDuplicateThreshold)
                {
                    Union(parent, rank, i, j);
                    pairDistances.Add((i, j, dist));
                }
            }
        }

        // Group by root
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(parent, i);
            if (!groups.TryGetValue(root, out var list))
                groups[root] = list = [];
            list.Add(i);
        }

        var clusters = new List<DuplicateCluster>();
        foreach (var (_, members) in groups)
        {
            if (members.Count < 2) continue;

            var memberSet = new HashSet<int>(members);
            int maxDist = pairDistances
                .Where(p => memberSet.Contains(p.a) && memberSet.Contains(p.b))
                .Select(p => p.distance)
                .DefaultIfEmpty(0)
                .Max();

            clusters.Add(new DuplicateCluster(
                members.Select(i => paths[i]).ToList(),
                HammingDistance: maxDist,
                SimilarityPercent: PerceptualHasher.SimilarityPercent(maxDist),
                IsExactDuplicate: false));
        }

        return clusters;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hashBytes);
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]]; // path compression
            i = parent[i];
        }
        return i;
    }

    private static void Union(int[] parent, int[] rank, int a, int b)
    {
        int ra = Find(parent, a);
        int rb = Find(parent, b);
        if (ra == rb) return;

        if (rank[ra] < rank[rb]) (ra, rb) = (rb, ra);
        parent[rb] = ra;
        if (rank[ra] == rank[rb]) rank[ra]++;
    }
}
