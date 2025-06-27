using System.Reflection;

namespace DiffusionNexus.UI.ViewModels
{
    public class AboutViewModel : ViewModelBase
    {
        public string AppVersion { get; } = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    }
}
