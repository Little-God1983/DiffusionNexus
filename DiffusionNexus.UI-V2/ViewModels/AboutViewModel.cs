using System.Reflection;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the About view displaying application information.
/// </summary>
public class AboutViewModel : ViewModelBase
{
    /// <summary>
    /// Gets the application version from assembly metadata.
    /// </summary>
    public string AppVersion { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
