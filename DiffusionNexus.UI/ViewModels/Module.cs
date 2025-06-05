using Avalonia.Controls;

namespace DiffusionNexus.UI.ViewModels
{
    public class Module : ViewModelBase
    {
        public string Title { get; }
        public UserControl View { get; }

        public Module(string title, UserControl view)
        {
            Title = title;
            View = view;
        }
    }
}
