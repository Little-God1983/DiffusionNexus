using System.Text.Json;
using System.Text.Json.Serialization;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// Writes the per-run sort history (spec §7 step 5) as two files: the full plan,
/// persisted once before the first file is touched, and an append-only completion
/// log next to it. This is the data source for the future "Restore previous
/// structure" feature; v1 only writes it.
/// </summary>
/// <remarks>
/// Completion used to be a flag inside the plan file, re-written in full per
/// transferred file: on a 3000-LoRA sort that is ~9M entry (de)serializations and
/// 6000 whole-file I/O operations on a multi-MB JSON document, on the transfer hot
/// path — O(n²) in library size. Worse for the artifact's actual purpose:
/// File.WriteAllText truncates before writing, so a kill or power loss during any of
/// those n writes left truncated JSON — destroying the crash-recovery file and making
/// the next run's deserialize throw. Appending one line per completed file is O(1),
/// and a torn final line costs one record instead of the whole history.
/// </remarks>
public sealed class SortHistoryWriter
{
    private const string LogSource = "LoraSorter";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>One record per line — indentation would break the JSONL format.</summary>
    private static readonly JsonSerializerOptions LineJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _historyDirectory;
    private readonly IUnifiedLogger? _logger;

    public SortHistoryWriter(string historyDirectory, IUnifiedLogger? logger = null)
    {
        _historyDirectory = historyDirectory;
        _logger = logger;
    }

    public static string DefaultHistoryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "SortHistory");

    /// <summary>
    /// Persists the plan and returns its path, or <c>null</c> when it could not be
    /// written. History is a convenience: a read-only or full LocalAppData must not stop
    /// the user from sorting their library, so this never throws at the caller.
    /// </summary>
    public string? WritePlan(LoraSortPlan plan, DateTimeOffset startedAt)
    {
        var path = Path.Combine(_historyDirectory, $"{startedAt:yyyyMMdd-HHmmss}.json");
        try
        {
            Directory.CreateDirectory(_historyDirectory);
            var manifest = new Manifest(
                startedAt, plan.SourceRoot, plan.TargetRoot, plan.IsMove,
                plan.Moves.Select(ToEntry).ToList());
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger?.Error(LogCategory.FileSystem, LogSource,
                $"Could not write the sort history manifest to {path}.", ex);
            return null;
        }
    }

    /// <summary>
    /// Appends one completion record for <paramref name="sourceFilePath"/>. No-ops when
    /// there is no manifest. Throws on I/O trouble — the executor decides what a manifest
    /// problem costs (never a transfer that already happened).
    /// </summary>
    public void MarkCompleted(string? manifestPath, string sourceFilePath)
    {
        if (string.IsNullOrEmpty(manifestPath)) return;

        var record = JsonSerializer.Serialize(
            new CompletionRecord(sourceFilePath, DateTimeOffset.Now), LineJsonOptions);
        File.AppendAllText(CompletionLogPath(manifestPath), record + Environment.NewLine);
    }

    /// <summary>The append-only completion log that belongs to a plan file. Internal for the same
    /// reason as <see cref="ReadCompleted"/>: only the writer and the tests know about it today.</summary>
    internal static string CompletionLogPath(string manifestPath)
        => Path.ChangeExtension(manifestPath, null) + ".completed.jsonl";

    /// <summary>
    /// Source paths recorded as completed, for Restore (and for asserting on a run). A
    /// torn last line from a killed run is skipped rather than failing the whole read.
    /// </summary>
    /// <remarks>
    /// Internal until Restore ships: no production code reads the journal yet, and a public API
    /// whose only callers are its own tests advertises a contract nothing depends on. The tests
    /// reach it through <c>InternalsVisibleTo("DiffusionNexus.Tests")</c>. The journal itself is
    /// written from day one on purpose — a Restore added later needs the data to already exist.
    /// </remarks>
    internal static IReadOnlyCollection<string> ReadCompleted(string manifestPath)
    {
        var path = CompletionLogPath(manifestPath);
        if (!File.Exists(path)) return [];

        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<CompletionRecord>(line, LineJsonOptions);
                if (!string.IsNullOrEmpty(record?.Source)) completed.Add(record.Source);
            }
            catch (JsonException)
            {
                // Truncated final line from a killed run — every earlier line is intact.
            }
        }
        return completed;
    }

    /// <summary>
    /// Sidecar targets are recorded with the entry: a Restore built on weights alone would
    /// move the model back and strand every companion file next to the sorted copy.
    /// </summary>
    private static Entry ToEntry(PlannedMove move) => new(
        move.Candidate.FilePath, move.TargetFilePath, move.Action, move.WasRenamed,
        move.Candidate.FileSizeBytes,
        move.Action == PlannedAction.Transfer ? SidecarEntries(move) : []);

    private static List<SidecarEntry> SidecarEntries(PlannedMove move)
    {
        var entries = new List<SidecarEntry>(move.Candidate.SidecarPaths.Count);
        foreach (var sidecar in move.Candidate.SidecarPaths)
        {
            try
            {
                entries.Add(new SidecarEntry(sidecar, SidecarLocator.DeriveSidecarTargetPath(
                    sidecar, move.Candidate.FilePath, move.TargetFilePath)));
            }
            catch (ArgumentException)
            {
                // Undeterminable target: the executor will not move it either.
            }
        }
        return entries;
    }

    private sealed record Manifest(
        DateTimeOffset StartedAt, string SourceRoot, string TargetRoot, bool IsMove,
        List<Entry> Entries);

    private sealed record Entry(
        string Source, string Target, PlannedAction Action, bool Renamed,
        long SizeBytes, List<SidecarEntry> Sidecars);

    private sealed record SidecarEntry(string Source, string Target);

    private sealed record CompletionRecord(string Source, DateTimeOffset CompletedAt);
}
