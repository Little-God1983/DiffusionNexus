using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Service.Services.DatasetQuality;

/// <summary>
/// Orchestrates dataset quality analysis by discovering registered
/// <see cref="IDatasetCheck"/> implementations, filtering by applicability,
/// and running them in order against the loaded dataset.
/// </summary>
public class AnalysisPipeline
{
    private readonly IEnumerable<IDatasetCheck> _checks;
    private readonly CaptionLoader _captionLoader;

    /// <summary>
    /// Creates a new <see cref="AnalysisPipeline"/>.
    /// </summary>
    /// <param name="checks">All registered check implementations (injected via DI).</param>
    /// <param name="captionLoader">Loader that reads caption files from disk.</param>
    public AnalysisPipeline(IEnumerable<IDatasetCheck> checks, CaptionLoader captionLoader)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(captionLoader);

        _checks = checks;
        _captionLoader = captionLoader;
    }

    /// <summary>
    /// Runs all applicable checks against the dataset described by <paramref name="config"/>.
    /// </summary>
    /// <param name="config">Dataset configuration (folder, trigger word, LoRA type).</param>
    /// <returns>An <see cref="AnalysisReport"/> containing all discovered issues.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
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

        foreach (var check in applicableChecks)
        {
            var issues = check.Run(captions, config);
            allIssues.AddRange(issues);
        }

        // Sort: Critical first, then Warning, then Info
        var sortedIssues = allIssues
            .OrderByDescending(i => i.Severity)
            .ToList();

        var summary = AnalysisReport.BuildSummary(
            sortedIssues,
            captions.Count,
            imageCount,
            applicableChecks.Count);

        return new AnalysisReport
        {
            Config = config,
            Issues = sortedIssues,
            Summary = summary
        };
    }
}
