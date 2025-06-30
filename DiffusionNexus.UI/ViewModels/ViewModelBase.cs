using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.ViewModels
{
    public class ViewModelBase : ObservableObject
    {
        protected void Log(string message, LogLevel level = LogLevel.Info)
        {
            LogEventService.Instance.Publish(level, message);
            Debug.WriteLine(message);
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            Debug.WriteLine($"{GetType().Name}.{e.PropertyName} changed");
        }
    }
}
