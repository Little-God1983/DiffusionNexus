using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels
{
    public class ViewModelBase : ObservableObject
    {
        protected void Log(string message) => Debug.WriteLine(message);

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            Debug.WriteLine($"{GetType().Name}.{e.PropertyName} changed");
        }
    }
}
