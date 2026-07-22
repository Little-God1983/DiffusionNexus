using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Service.Services.DatasetQuality;

/// <summary>
/// Persists and retrieves <see cref="AnalysisRunRecord"/> instances as JSON files
/// inside a <c>.quality-runs</c> subfolder of each dataset version directory.
/// <para>
/// File naming convention:
/// <c>dd-MM-yyyy-HH-mm-ss-Version-V{N}-run.json</c>
/// </para>
/// </summary>
public class AnalysisRunStore
{
    /// <summary>
    /// Name of the subfolder where run files are stored.
    /// </summary>
    private const string RunsFolderName = ".quality-runs";

    /// <summary>
    /// Maximum number of run files retained per dataset. Oldest runs beyond this limit
    /// are automatically deleted after each save.
    /// </summary>
    private const int MaxRunsPerDataset = 50;

    /// <summary>
    /// The leading timestamp segment of a run file name (see <see cref="BuildFileName"/>). Shared by
    /// the name-builder and the prune's chronological parser so the two never drift apart.
    /// </summary>
    private const string TimestampFormat = "dd-MM-yyyy-HH-mm-ss";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Saves an analysis run record to the dataset version's run folder.
    /// </summary>
    /// <param name="datasetFolderPath">Absolute path to the dataset version folder.</param>
    /// <param name="record">The run record to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The absolute path of the saved file.</returns>
    public async Task<string> SaveAsync(
        string datasetFolderPath,
        AnalysisRunRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(datasetFolderPath))
            throw new ArgumentException("Dataset folder path must not be empty.", nameof(datasetFolderPath));

        var runsDir = GetRunsDirectory(datasetFolderPath);
        Directory.CreateDirectory(runsDir);

        var fileName = BuildFileName(record.AnalyzedAtUtc, record.Version);
        var filePath = Path.Combine(runsDir, fileName);

        var json = JsonSerializer.Serialize(record, SerializerOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);

        PruneOldRuns(runsDir);

        return filePath;
    }

    /// <summary>
    /// Loads all run records from the dataset version's run folder, ordered by timestamp descending (newest first).
    /// </summary>
    /// <param name="datasetFolderPath">Absolute path to the dataset version folder.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All run records found, newest first.</returns>
    public async Task<IReadOnlyList<AnalysisRunRecord>> LoadAllAsync(
        string datasetFolderPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(datasetFolderPath))
            return [];

        var runsDir = GetRunsDirectory(datasetFolderPath);
        if (!Directory.Exists(runsDir))
            return [];

        var files = Directory.GetFiles(runsDir, "*-run.json")
            .OrderByDescending(f => f)
            .ToArray();

        var records = new List<AnalysisRunRecord>(files.Length);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var record = JsonSerializer.Deserialize<AnalysisRunRecord>(json, SerializerOptions);
                if (record is not null)
                    records.Add(record);
            }
            catch (JsonException)
            {
                // Skip corrupted files
            }
        }

        return records
            .OrderByDescending(r => r.AnalyzedAtUtc)
            .ToList();
    }

    /// <summary>
    /// Deletes a specific run file by matching its timestamp and version.
    /// </summary>
    /// <param name="datasetFolderPath">Absolute path to the dataset version folder.</param>
    /// <param name="record">The record to delete.</param>
    public void Delete(string datasetFolderPath, AnalysisRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(datasetFolderPath))
            return;

        var runsDir = GetRunsDirectory(datasetFolderPath);
        var fileName = BuildFileName(record.AnalyzedAtUtc, record.Version);
        var filePath = Path.Combine(runsDir, fileName);

        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    /// <summary>
    /// Builds the file name for a run record.
    /// Format: <c>dd-MM-yyyy-HH-mm-ss-Version-V{N}-run.json</c>
    /// </summary>
    private static string BuildFileName(DateTimeOffset timestamp, int version)
    {
        // Use local time for human-readable file names. Formatted with InvariantCulture: this app also
        // runs under German locale, and culture-sensitive date formatting/parsing is a known trap here.
        var local = timestamp.ToLocalTime();
        return $"{local.ToString(TimestampFormat, CultureInfo.InvariantCulture)}-Version-V{version}-run.json";
    }

    private static string GetRunsDirectory(string datasetFolderPath) =>
        Path.Combine(datasetFolderPath, RunsFolderName);

    /// <summary>
    /// Deletes the oldest run files when the total exceeds <see cref="MaxRunsPerDataset"/>.
    /// </summary>
    /// <remarks>
    /// Candidates are ordered by the timestamp PARSED from the file name (see
    /// <see cref="GetSortableTimestamp"/>), not by a plain string sort of the path (issue #468). The
    /// file name's <c>dd-MM-yyyy</c> prefix is day-first, so a lexicographic sort is NOT chronological
    /// across month/year boundaries — e.g. "01-08-2026…" sorts before "28-07-2026…" as a string despite
    /// being later in time — which could delete recently-saved runs while keeping older ones. Ties
    /// (e.g. two runs saved within the same second) fall back to an ordinal compare of the full path,
    /// purely to make the outcome deterministic — this secondary key is NOT chronological and must
    /// never be promoted above the timestamp.
    /// </remarks>
    private static void PruneOldRuns(string runsDir)
    {
        var files = Directory.GetFiles(runsDir, "*-run.json");

        if (files.Length <= MaxRunsPerDataset)
            return;

        var ordered = files
            .OrderByDescending(GetSortableTimestamp)
            .ThenByDescending(f => f, StringComparer.Ordinal)
            .ToArray();

        foreach (var file in ordered.Skip(MaxRunsPerDataset))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Best-effort cleanup; skip locked files
            }
        }
    }

    /// <summary>
    /// Resolves a comparable "age" for a run file so prune candidates can be ordered chronologically.
    /// </summary>
    /// <remarks>
    /// Parses the leading <c>dd-MM-yyyy-HH-mm-ss</c> segment of the file NAME (not the full path) with
    /// <see cref="CultureInfo.InvariantCulture"/> — this app also runs under German locale, where
    /// culture-sensitive parsing is a known trap. The timestamps are local wall-clock values written
    /// with <see cref="DateTimeStyles.None"/> (unspecified kind); only relative ordering matters here,
    /// so a DST-ambiguous local time may mis-order within the repeated hour, which is accepted.
    /// <para>
    /// The <c>*-run.json</c> glob can also match foreign files that don't fit the naming convention
    /// (e.g. a stray file dropped into the runs folder). Those fall back to
    /// <see cref="File.GetLastWriteTimeUtc(string)"/>, converted to LOCAL time via
    /// <see cref="DateTime.ToLocalTime"/> so it lands in the same frame as the parsed branch above.
    /// This conversion is required, not cosmetic: <see cref="DateTime"/> comparison ignores
    /// <see cref="DateTime.Kind"/> and compares raw ticks, so leaving the fallback as raw UTC ticks
    /// would silently misorder it against the parsed branch's local wall-clock ticks on any machine not
    /// at UTC+0 (e.g. on UTC+2, a file genuinely written at local 15:30 has raw UTC ticks of 13:30,
    /// which would incorrectly compare as OLDER than a parsed run stamped local 14:00). With the
    /// conversion applied, an unparsable name can neither crash the prune nor be silently preferred (or
    /// deprioritized) over real, parseable runs — it's ordered by its actual local last-write-time like
    /// any other file would be absent a name to parse.
    /// </para>
    /// </remarks>
    private static DateTime GetSortableTimestamp(string filePath)
    {
        var name = Path.GetFileName(filePath);
        if (name.Length >= TimestampFormat.Length)
        {
            var candidate = name[..TimestampFormat.Length];
            if (DateTime.TryParseExact(
                    candidate,
                    TimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                return parsed;
            }
        }

        try
        {
            // GetLastWriteTimeUtc returns UTC; convert to local so its ticks are comparable to the
            // parsed branch's local wall-clock ticks above (see the Kind-mismatch note in <remarks>).
            return File.GetLastWriteTimeUtc(filePath).ToLocalTime();
        }
        catch (IOException)
        {
            // A genuine I/O failure reading the last-write-time (e.g. a sharing violation) - NOT a
            // missing file: GetLastWriteTimeUtc does not throw for a nonexistent path, it returns the
            // 1601-01-01 UTC sentinel, which already sorts oldest on its own. Treat this failure case
            // the same way (oldest) so it's a prune candidate first rather than risk squatting on a
            // slot ahead of a real run.
            return DateTime.MinValue;
        }
    }
}
