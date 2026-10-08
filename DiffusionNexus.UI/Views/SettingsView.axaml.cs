using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Settings view for configuring application settings.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DataContextChanged += OnDataContextChanged;
    }

    private SettingsViewModel? _observedViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _observedViewModel = DataContext as SettingsViewModel;

        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.IsComfyUiServerExpanded)
            && _observedViewModel?.IsComfyUiServerExpanded == true)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => this.FindControl<Expander>("ComfyUiServerExpander")?.BringIntoView(),
                Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Inject DialogService into the ViewModel
        if (VisualRoot is Window window && DataContext is IDialogServiceAware aware)
        {
            aware.DialogService = new DialogService(window);
        }

        // Re-check the ⚠ folder-not-found badges every time the page is shown,
        // so folders deleted (or restored) on disk mid-session are reflected.
        // Fire-and-forget: the probes run off-thread and must not delay attach.
        if (DataContext is SettingsViewModel settingsViewModel)
        {
            _ = settingsViewModel.RefreshFolderPresenceAsync();
        }
    }
}
