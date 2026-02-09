using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Handles exporting and importing application settings to/from JSON files.
/// </summary>
public interface ISettingsExportService
{
    /// <summary>
    /// Exports the current application settings to a JSON file.
    /// </summary>
    /// <param name="filePath">Destination file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExportAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and validates settings from a JSON file without applying them.
    /// </summary>
    /// <param name="filePath">Source file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed export data.</returns>
    Task<SettingsExportData> ReadAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports settings from a JSON file and persists them to the database,
    /// fully replacing the current settings.
    /// </summary>
    /// <param name="filePath">Source file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ImportAsync(string filePath, CancellationToken cancellationToken = default);
}
