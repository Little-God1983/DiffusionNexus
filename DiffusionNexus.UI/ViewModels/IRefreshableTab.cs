namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Implemented by tab ViewModels that support being refreshed by a module-level
/// refresh button. The module shell dispatches to whichever tab is active;
/// tabs that do not implement this interface leave the button disabled.
/// </summary>
public interface IRefreshableTab
{
    /// <summary>
    /// False while the tab is busy and must not be refreshed.
    /// </summary>
    bool CanRefresh { get; }

    /// <summary>
    /// Reloads the tab's current view from its underlying source.
    /// </summary>
    Task RefreshAsync();
}
