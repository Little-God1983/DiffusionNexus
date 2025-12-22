using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Service for managing application settings.
/// </summary>
public interface IAppSettingsService
{
    /// <summary>
    /// Gets the current application settings.
    /// Creates default settings if none exist.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The application settings.</returns>
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the application settings.
    /// </summary>
    /// <param name="settings">The settings to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the decrypted Civitai API key.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The API key or null if not set.</returns>
    Task<string?> GetCivitaiApiKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the Civitai API key (will be encrypted before storage).
    /// </summary>
    /// <param name="apiKey">The API key to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetCivitaiApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all enabled LoRA source paths.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of enabled source folder paths.</returns>
    Task<IReadOnlyList<string>> GetEnabledLoraSourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new LoRA source folder.
    /// </summary>
    /// <param name="folderPath">The folder path to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created LoraSource entity.</returns>
    Task<LoraSource> AddLoraSourceAsync(string folderPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a LoRA source folder.
    /// </summary>
    /// <param name="sourceId">The ID of the source to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveLoraSourceAsync(int sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a LoRA source folder.
    /// </summary>
    /// <param name="source">The source to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateLoraSourceAsync(LoraSource source, CancellationToken cancellationToken = default);
}
