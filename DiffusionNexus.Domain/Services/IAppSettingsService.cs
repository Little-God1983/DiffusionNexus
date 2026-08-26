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
    /// Updates only the LastBackupAt timestamp without affecting other settings or collections.
    /// Use this instead of SaveSettingsAsync when only updating the backup timestamp.
    /// </summary>
    /// <param name="lastBackupAt">The timestamp of the last backup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateLastBackupAtAsync(DateTimeOffset lastBackupAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps when the last user-started library sync completed, without touching any other setting.
    /// </summary>
    Task UpdateLastLibrarySyncAtAsync(DateTimeOffset lastLibrarySyncAt, CancellationToken cancellationToken = default);

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
    /// Gets the decrypted HuggingFace access token, or null if not set.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token or null if not set.</returns>
    Task<string?> GetHuggingfaceApiKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the HuggingFace access token (will be encrypted before storage).
    /// </summary>
    /// <param name="token">The token to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetHuggingfaceApiKeyAsync(string? token, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Gets the user's favorited LoRA source folder path, or null if none is set.
    /// Used to pre-select the default destination in download dialogs.
    /// </summary>
    Task<string?> GetFavoriteLoraSourceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets (or clears, when <paramref name="folderPath"/> is null) the user's
    /// favorited LoRA source folder.
    /// </summary>
    Task SetFavoriteLoraSourceAsync(string? folderPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all enabled Base Model Folders, ordered by <c>Order</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<BaseModelFolder>> GetEnabledBaseModelFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a Base Model Folder. Idempotent: when a row with the same path already
    /// exists (case-insensitive) no duplicate is created; a non-null
    /// <paramref name="installerPackageId"/> is linked onto the existing row.
    /// Never sets the default flag.
    /// </summary>
    /// <param name="folderPath">Absolute path of the models root.</param>
    /// <param name="installerPackageId">Owning installer package, when auto-registered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a new row was inserted; <c>false</c> when the path already existed.</returns>
    Task<bool> AddBaseModelFolderAsync(string folderPath, int? installerPackageId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets every Base Model Folder row, disabled ones included, ordered by <c>Order</c>.
    /// Unlike <see cref="GetEnabledBaseModelFoldersAsync"/> this is for maintenance work
    /// that has to see the whole registry rather than what is currently scanned.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<BaseModelFolder>> GetAllBaseModelFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes Base Model Folder rows by id. The ⭐ default row is never removed, whatever
    /// the caller asks: losing the default download target silently would be worse than
    /// keeping one redundant row.
    /// </summary>
    /// <param name="ids">Row ids to remove; unknown ids are ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were actually removed.</returns>
    Task<int> RemoveBaseModelFoldersAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default);

    /// <summary>Gets the remembered feedback-reporter e-mail, or null if not set.</summary>
    Task<string?> GetFeedbackReporterEmailAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the feedback-reporter e-mail; whitespace/empty clears it to null.</summary>
    Task SetFeedbackReporterEmailAsync(string? email, CancellationToken cancellationToken = default);

    /// <summary>Gets the LoRA Viewer's saved base-model filter JSON, or null if never saved.</summary>
    Task<string?> GetLoraViewerFilterJsonAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the LoRA Viewer's base-model filter JSON; whitespace/empty clears it to null.</summary>
    Task SetLoraViewerFilterJsonAsync(string? json, CancellationToken cancellationToken = default);
}
