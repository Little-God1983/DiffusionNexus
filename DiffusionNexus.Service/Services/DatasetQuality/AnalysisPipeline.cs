using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services.DatasetQuality.Scoring;

namespace DiffusionNexus.Service.Services.DatasetQuality;

/// <summary>
/// Orchestrates dataset quality analysis by discovering registered
/// <see cref="IDatasetCheck"/> and <see cref="IImageQualityCheck"/>
/// implementations, filtering by applicability, and running them in order
/// against the loaded dataset. Computes a composite quality score from all results.
/// </summary>
public class AnalysisPipeline
{
    private readonly IEnumerable<IDatasetCheck> _checks;
    private readonly IEnumerable<IImageQualityCheck> _imageChecks;
    private readonly CaptionLoader _captionLoader;
    private readonly BucketAnalyzer _bucketAnalyzer;

    /// <summary>
    /// Creates a new <see cref="AnalysisPipeline"/>.
    /// </summary>
    /// <param name="checks">All registered caption check implementations (injected via DI).</param>
    /// <param name="imageChecks">All registered image check implementations (injected via DI).</param>
    /// <param name="captionLoader">Loader that reads caption files from disk.</param>
    /// <param name="bucketAnalyzer">Bucket analyzer for resolution distribution scoring.</param>
    public AnalysisPipeline(
        IEnumerable<IDatasetCheck> checks,
        IEnumerable<IImageQualityCheck> imageChecks,
        CaptionLoader captionLoader,
        BucketAnalyzer bucketAnalyzer)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(imageChecks);
        ArgumentNullException.ThrowIfNull(captionLoader);
        ArgumentNullException.ThrowIfNull(bucketAnalyzer);

        _checks = checks;
        _imageChecks = imageChecks;
        _captionLoader = captionLoader;
        _bucketAnalyzer = bucketAnalyzer;
    }

    /// <summary>
    /// Runs all applicable caption checks against the dataset described by <paramref name="config"/>.
    /// This is the original synchronous entry point for caption-only analysis.
    /// </summary>
    /// <param name="config">Dataset configuration (folder, trigger word, LoRA type).</param>
    /// <returns>An <see cref="AnalysisReport"/> containing all discovered issues.</returns>
    public AnalysisReport Analyze(DatasetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Load caption files
        var (captions, imageCount) = _captionLoader.Load(config.FolderPath);

        // Filter and order checks
        var applicableChecks = _checks
            .Where(c => c.IsApplicable(config.LoraType))
            .OrderBy(c => c.Order)
            .ToList();

        // Run each check and collect issues
        var allIssues = new List<Issue>();
        var allCheckScores = new List<CheckScore>();

        foreach (var check in applicableChecks)
        {
            var issues = check.Run(captions, config);
            allIssues.AddRange(issues);

            // Convert caption check issues to a numeric score
            var checkIssues = issues.Where(i => i.CheckName == check.Name).ToList();
            var score = CheckScoreAdapter.ScoreFromIssues(check.Name, checkIssues, captions.Count);
            if (score != null)
                allCheckScores.Add(score);
        }

        // Completeness score (captions vs images)
        allCheckScores.Add(CheckScoreAdapter.ScoreFromCompleteness(captions.Count, imageCount));

        // Sort: Critical first, then Warning, then Info
        var sortedIssues = allIssues
            .OrderByDescending(i => i.Severity)
            .ToList();

        var summary = AnalysisReport.BuildSummary(
            sortedIssues,
            captions.Count,
            imageCount,
            applicableChecks.Count);

        var composite = CompositeScoreCalculator.Calculate(allCheckScores);

        return new AnalysisReport
        {
            Config = config,
            Issues = sortedIssues,
            Summary = summary,
            CheckScores = allCheckScores,
            CompositeScore = composite
        };
    }

    /// <summary>
    /// Runs the full analysis pipeline: caption checks, image quality checks,
    /// and bucket analysis. Computes a composite score from all results.
    /// All I/O-bound and CPU-bound synchronous work is offloaded to the thread
    /// pool so the caller's context (typically the UI thread) stays responsive.
    /// </summary>
    /// <param name="config">Dataset configuration.</param>
    /// <param name="bucketConfig">Bucket analysis configuration, or null to skip bucket analysis.</param>
    /// <param name="progress">Optional progress reporter (0.0 – 1.0).</param>
    /// <param name="statusProgress">Optional status text reporter for UI feedback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A comprehensive analysis report with composite scoring.</returns>
    public async Task<AnalysisReport> AnalyzeFullAsync(
        DatasetConfig config,
        BucketConfig? bucketConfig = null,
        IProgress<double>? progress = null,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        // --- Phase 1: Load captions and scan images (I/O-bound) ---
        statusProgress?.Report("Loading caption files…");
        progress?.Report(0.0);

        var (captions, imageCount, images) = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captionResult = _captionLoader.Load(config.FolderPath);
            var imageResult = _bucketAnalyzer.ScanFolder(config.FolderPath);
            return (captionResult.Captions, captionResult.ImageFileCount, imageResult);
        }, cancellationToken);

        var allIssues = new List<Issue>();
        var allCheckScores = new List<CheckScore>();
        var imageCheckResults = new List<ImageCheckResult>();

        // --- Phase 2: Caption checks (CPU-bound, offloaded) ---
        var applicableChecks = _checks
            .Where(c => c.IsApplicable(config.LoraType))
            .OrderBy(c => c.Order)
            .ToList();

        var applicableImageChecks = _imageChecks
            .Where(c => c.IsApplicable(config.LoraType))
            .OrderBy(c => c.Order)
            .ToList();

        // Total phases for overall progress: caption checks + bucket + image checks
        int totalPhases = applicableChecks.Count
            + (bucketConfig != null && images.Count > 0 ? 1 : 0)
            + applicableImageChecks.Count;
        int completedPhases = 0;

        foreach (var check in applicableChecks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statusProgress?.Report($"Running {check.Name}…");

            var issues = await Task.Run(() => check.Run(captions, config), cancellationToken);
            allIssues.AddRange(issues);

            var checkIssues = issues.Where(i => i.CheckName == check.Name).ToList();
            var score = CheckScoreAdapter.ScoreFromIssues(check.Name, checkIssues, captions.Count);
            if (score != null)
                allCheckScores.Add(score);

            completedPhases++;
            progress?.Report((double)completedPhases / Math.Max(totalPhases, 1));
        }

        // Completeness score
        allCheckScores.Add(CheckScoreAdapter.ScoreFromCompleteness(captions.Count, imageCount));

        // --- Phase 3: Bucket analysis (optional, CPU-bound) ---
        if (bucketConfig != null && images.Count > 0)
        {
            statusProgress?.Report("Analyzing bucket distribution…");

            var bucketResult = await Task.Run(
                () => BucketAnalyzer.Analyze(images, bucketConfig), cancellationToken);
            allIssues.AddRange(bucketResult.Issues);
            allCheckScores.Add(CheckScoreAdapter.ScoreFromBucketAnalysis(bucketResult.DistributionScore));

            completedPhases++;
            progress?.Report((double)completedPhases / Math.Max(totalPhases, 1));
        }

        // --- Phase 4: Image quality checks (async) ---
        foreach (var imageCheck in applicableImageChecks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statusProgress?.Report($"Running {imageCheck.Name}…");

            var stepProgress = progress != null
                ? new Progress<double>(p =>
                    progress.Report((completedPhases + p) / Math.Max(totalPhases, 1)))
                : null;

            var result = await imageCheck.RunAsync(images, config, stepProgress, cancellationToken);
            imageCheckResults.Add(result);
            allIssues.AddRange(result.Issues);

            allCheckScores.Add(new CheckScore
            {
                Score = result.Score,
                CheckName = result.CheckName,
                Category = imageCheck.Category,
                Weight = 1.0
            });

            completedPhases++;
            progress?.Report((double)completedPhases / Math.Max(totalPhases, 1));
        }

        // --- Build final report ---
        statusProgress?.Report("Computing scores…");

        var sortedIssues = allIssues
            .OrderByDescending(i => i.Severity)
            .ToList();

        var summary = AnalysisReport.BuildSummary(
            sortedIssues,
            captions.Count,
            imageCount,
            applicableChecks.Count + applicableImageChecks.Count);

        var composite = CompositeScoreCalculator.Calculate(allCheckScores);

        progress?.Report(1.0);
        statusProgress?.Report("Analysis complete");

        return new AnalysisReport
        {
            Config = config,
            Issues = sortedIssues,
            Summary = summary,
            CheckScores = allCheckScores,
            CompositeScore = composite,
            ImageCheckResults = imageCheckResults
        };
    }
}
