using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Combined VRAM-tier + destination + disk-space dialog shown before a
/// captioning model download starts. The dialog only closes successfully
/// when the selected destination has enough free space for the chosen tier;
/// the Download button is disabled otherwise.
/// </summary>
public partial class CaptioningDownloadOptionsDialog : Window
{
    /// <summary>Selected VRAM tier in GB, or null on cancel.</summary>
    public int? SelectedVramGb { get; private set; }

    /// <summary>Selected destination directory, or null on cancel.</summary>
    public string? SelectedDestination { get; private set; }

    public CaptioningDownloadOptionsDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.CaptioningDownloadOptionsViewModel vm
            && vm.CanConfirm
            && vm.SelectedDestination is not null)
        {
            // For non-tiered models we still surface a SelectedVramGb (0)
            // back so the row download flow can decide based on whether the
            // model has tiers; it doesn't use the value otherwise.
            SelectedVramGb = vm.HasVramTiers ? vm.SelectedVramGb : 0;
            SelectedDestination = vm.SelectedDestination.Path;
        }
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        SelectedVramGb = null;
        SelectedDestination = null;
        Close();
    }
}

