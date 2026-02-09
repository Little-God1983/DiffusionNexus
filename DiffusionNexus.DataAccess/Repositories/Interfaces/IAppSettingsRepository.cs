using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Repository for <see cref="AppSettings"/> with domain-specific queries
/// for managing the singleton settings entity and its child collections.
/// </summary>
public interface IAppSettingsRepository : IRepository<AppSettings>
{
    /// <summary>
    /// Gets the singleton settings entity with all child collections loaded and ordered.
    /// Creates default settings if none exist.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The application settings.</returns>
    Task<AppSettings> GetSettingsWithIncludesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the singleton settings entity without loading child collections.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The application settings, or null if not yet created.</returns>
    Task<AppSettings?> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of dataset categories.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The count.</returns>
    Task<int> GetDatasetCategoryCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds dataset categories to the context.
    /// </summary>
    /// <param name="categories">The categories to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddDatasetCategoriesAsync(IEnumerable<DatasetCategory> categories, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a dataset category.
    /// </summary>
    /// <param name="category">The category to remove.</param>
    void RemoveDatasetCategory(DatasetCategory category);

    /// <summary>
    /// Adds a LoRA source to the context.
    /// </summary>
    /// <param name="source">The source to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddLoraSourceAsync(LoraSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a LoRA source.
    /// </summary>
    /// <param name="source">The source to remove.</param>
    void RemoveLoraSource(LoraSource source);

    /// <summary>
    /// Finds a LoRA source by its ID.
    /// </summary>
    /// <param name="id">The source ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The LoRA source, or null.</returns>
    Task<LoraSource?> FindLoraSourceByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an image gallery to the context.
    /// </summary>
    /// <param name="gallery">The gallery to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddImageGalleryAsync(ImageGallery gallery, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an image gallery.
    /// </summary>
    /// <param name="gallery">The gallery to remove.</param>
    void RemoveImageGallery(ImageGallery gallery);
}
