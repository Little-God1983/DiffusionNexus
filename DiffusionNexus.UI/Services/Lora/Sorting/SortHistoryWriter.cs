using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// Writes the per-run sort-history manifest (spec §7 step 5): the full plan is
/// persisted before the first file is touched, then each completed file is
/// flagged. This is the data source for the future "Restore previous structure"
/// feature; v1 only writes it.
/// </summary>
public sealed class SortHistoryWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _historyDirectory;

    public SortHistoryWriter(string historyDirectory) => _historyDirectory = historyDirectory;

    public static string DefaultHistoryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "SortHistory");

    public string WritePlan(LoraSortPlan plan, DateTimeOffset startedAt)
    {
        Directory.CreateDirectory(_historyDirectory);
        var manifest = new Manifest(
            startedAt, plan.SourceRoot, plan.TargetRoot, plan.IsMove,
            plan.Moves.Select(m => new Entry(
                m.Candidate.FilePath, m.TargetFilePath, m.Action, m.WasRenamed,
                m.Candidate.FileSizeBytes, Completed: false)).ToList());
        var path = Path.Combine(_historyDirectory, $"{startedAt:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
        return path;
    }

    public void MarkCompleted(string manifestPath, string sourceFilePath)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions)!;
        var updated = manifest with
        {
            Entries = manifest.Entries
                .Select(e => string.Equals(e.Source, sourceFilePath, StringComparison.OrdinalIgnoreCase)
                    ? e with { Completed = true } : e)
                .ToList()
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(updated, JsonOptions));
    }

    private sealed record Manifest(
        DateTimeOffset StartedAt, string SourceRoot, string TargetRoot, bool IsMove,
        List<Entry> Entries);

    private sealed record Entry(
        string Source, string Target, PlannedAction Action, bool Renamed,
        long SizeBytes, bool Completed);
}
