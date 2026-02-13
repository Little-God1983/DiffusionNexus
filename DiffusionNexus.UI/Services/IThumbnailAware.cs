namespace DiffusionNexus.UI.Services;

/// <summary>
/// Interface for ViewModels that participate in priority-based thumbnail loading.
/// <para>
/// When a view becomes active (visible to the user), the host calls <see cref="OnThumbnailActivated"/>,
/// which promotes the ViewModel's thumbnail requests to <see cref="ThumbnailPriority.Critical"/>.
/// When the view loses focus, <see cref="OnThumbnailDeactivated"/> cancels pending requests
/// so the newly active view gets priority.
/// </para>
/// <para>
/// <b>Host responsibilities:</b>
/// <list type="bullet">
/// <item><see cref="DiffusionNexus.UI.ViewModels.DiffusionNexusMainWindowViewModel"/> calls these
/// methods when modules are switched via the navigation sidebar.</item>
/// <item><see cref="DiffusionNexus.UI.ViewModels.LoraDatasetHelperViewModel"/> calls these
/// methods when tabs are switched within the LoRA Dataset Helper.</item>
/// </list>
/// </para>
/// </summary>
public interface IThumbnailAware
{
    /// <summary>
    /// The unique owner token for this ViewModel's thumbnail requests.
    /// Created once and reused for all requests.
    /// </summary>
    ThumbnailOwnerToken OwnerToken { get; }

    /// <summary>
    /// Called when this ViewModel's view becomes the active (visible) view.
    /// Implementations should call <see cref="IThumbnailOrchestrator.SetActiveOwner"/>
    /// with their <see cref="OwnerToken"/>.
    /// </summary>
    void OnThumbnailActivated();

    /// <summary>
    /// Called when this ViewModel's view is no longer the active view.
    /// Implementations should call <see cref="IThumbnailOrchestrator.CancelRequests"/>
    /// to free up resources for the newly active view.
    /// </summary>
    void OnThumbnailDeactivated();
}
