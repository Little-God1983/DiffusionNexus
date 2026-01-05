using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Base class for all ViewModels providing common functionality
/// like property change notifications and debug logging.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Debug.WriteLine($"[{GetType().Name}] Property changed: {e.PropertyName}");
    }
}
