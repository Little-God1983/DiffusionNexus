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
        // Use local time for human-readable file names
        var local = timestamp.ToLocalTime();
        return $"{local:dd-MM-yyyy-HH-mm-ss}-Version-V{version}-run.json";
    }

    private static string GetRunsDirectory(string datasetFolderPath) =>
        Path.Combine(datasetFolderPath, RunsFolderName);
}
