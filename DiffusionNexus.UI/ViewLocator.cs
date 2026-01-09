using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI;

/// <summary>
/// Locates views for ViewModels using naming convention.
/// Maps *ViewModel to *View automatically.
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
