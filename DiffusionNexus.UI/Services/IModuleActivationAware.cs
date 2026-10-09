namespace DiffusionNexus.UI.Services;

/// <summary>
/// Implemented by a module's ViewModel that wants to know each time the user navigates to its
/// module (sidebar click or a navigation request), e.g. to refresh state that can change while the
/// module is off screen.
/// </summary>
public interface IModuleActivationAware
{
    /// <summary>Called every time the module becomes the displayed one.</summary>
    void OnModuleActivated();
}
