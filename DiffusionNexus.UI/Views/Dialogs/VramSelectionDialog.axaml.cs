using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Simple dialog that lets the user pick their GPU VRAM size
/// so the correct model variants can be selected for download.
/// Only shows the profiles that are actually configured for the workload,
/// matching the behaviour of the standalone installer.
/// </summary>
public partial class VramSelectionDialog : Window
{
    private readonly int[] _profiles;

    /// <summary>
    /// The VRAM size (in GB) chosen by the user, or <c>null</c> if cancelled.
    /// </summary>
    public int? SelectedVramGb { get; private set; }

    /// <param name="configuredProfiles">
    /// Available VRAM sizes in GB, parsed from the configuration
    /// (e.g. <c>[8, 16, 24]</c>). Must contain at least one value.
    /// </param>
    public VramSelectionDialog(int[] configuredProfiles)
    {
        ArgumentNullException.ThrowIfNull(configuredProfiles);

        _profiles = configuredProfiles;
        AvaloniaXamlLoader.Load(this);

        var combo = this.FindControl<ComboBox>("VramComboBox")!;
        combo.ItemsSource = _profiles.Select(p => $"{p} GB").ToList();
        combo.SelectedIndex = 0;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var combo = this.FindControl<ComboBox>("VramComboBox")!;
        var index = combo.SelectedIndex;

        SelectedVramGb = index >= 0 && index < _profiles.Length
            ? _profiles[index]
            : _profiles[0];

        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        SelectedVramGb = null;
        Close();
    }
}
