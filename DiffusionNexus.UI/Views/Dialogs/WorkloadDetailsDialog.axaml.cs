using System.Collections.ObjectModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Detail dialog showing which models and custom nodes are installed or missing.
/// Allows the user to select missing items and trigger installation with live progress.
/// </summary>
public partial class WorkloadDetailsDialog : Window
{
    private readonly ObservableCollection<string> _logLines = [];
    private bool _isInstalling;

    public WorkloadDetailsDialog()
    {
        AvaloniaXamlLoader.Load(this);

        var logList = this.FindControl<ItemsControl>("ProgressLogList");
        if (logList is not null)
        {
            logList.ItemsSource = _logLines;
        }
    }

    /// <summary>
    /// The detail items to display in the grid, grouped by <see cref="WorkloadDetailItemViewModel.Category"/>.
    /// Set before calling ShowDialog.
    /// </summary>
    public ObservableCollection<WorkloadDetailItemViewModel> DetailItems
    {
        get => _detailItems;
        set
        {
            _detailItems = value;
            var grid = this.FindControl<DataGrid>("DetailsGrid");
            if (grid is not null)
            {
                var view = new DataGridCollectionView(value);
                view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(WorkloadDetailItemViewModel.Category)));
                grid.ItemsSource = view;
            }

            UpdateInstallButtonState();
        }
    }
    private ObservableCollection<WorkloadDetailItemViewModel> _detailItems = [];

    /// <summary>
    /// Summary text shown below the title.
    /// </summary>
    public string Summary
    {
        get => _summary;
        set
        {
            _summary = value;
            var text = this.FindControl<TextBlock>("SummaryText");
            if (text is not null)
            {
                text.Text = value;
            }
        }
    }
    private string _summary = string.Empty;

    /// <summary>
    /// After the dialog closes, contains the items the user selected for installation,
    /// or <c>null</c> if the user closed without clicking Install.
    /// </summary>
    public IReadOnlyList<WorkloadDetailItemViewModel>? SelectedForInstall { get; private set; }

    /// <summary>
    /// The VRAM profile (in GB) chosen by the user when VRAM-profiled models were selected,
    /// or <c>null</c> when not applicable or cancelled.
    /// </summary>
    public int? SelectedVramProfileGb { get; private set; }

    /// <summary>
    /// VRAM profile values (in GB) configured for this workload.
    /// When empty, VRAM selection is skipped entirely.
    /// Set before calling ShowDialog.
    /// </summary>
    public int[] ConfiguredVramProfiles { get; set; } = [];

    /// <summary>
    /// Callback that performs the actual installation. Set by the caller before ShowDialog.
    /// Receives the selected items, VRAM selection, and a progress reporter.
    /// Returns a summary string.
    /// </summary>
    public Func<IReadOnlyList<WorkloadDetailItemViewModel>, int, IProgress<WorkloadInstallProgress>, CancellationToken, Task<string>>?
        InstallCallback { get; set; }

    /// <summary>
    /// True after the install completed (regardless of success/failure).
    /// The caller uses this to decide whether to re-check the workload.
    /// </summary>
    public bool DidInstall { get; private set; }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        var selected = _detailItems
            .Where(i => i.IsSelected && i.IsMissing)
            .ToList();

        if (selected.Count == 0)
        {
            return;
        }

        // Show VRAM selection only when the configuration defines profiles
        // AND at least one selected model has VRAM-profiled download links
        var hasVramModels = ConfiguredVramProfiles.Length > 0
            && selected.Any(i => i.Category == "Model" && i.HasVramProfiles);

        if (hasVramModels)
        {
            var vramDialog = new VramSelectionDialog(ConfiguredVramProfiles);
            await vramDialog.ShowDialog(this);

            if (vramDialog.SelectedVramGb is null)
            {
                return;
            }

            SelectedVramProfileGb = vramDialog.SelectedVramGb;
        }

        SelectedForInstall = selected;

        // If no callback, just close like before (backward-compatible)
        if (InstallCallback is null)
        {
            Close();
            return;
        }

        // Run install in-dialog with live progress
        await RunInstallWithProgressAsync(selected, SelectedVramProfileGb ?? 0);
    }

    /// <summary>
    /// Executes the install callback while showing live progress in the dialog.
    /// </summary>
    private async Task RunInstallWithProgressAsync(
        IReadOnlyList<WorkloadDetailItemViewModel> selected, int vramGb)
    {
        _isInstalling = true;
        SetInstallingUiState(true);

        var progress = new Progress<WorkloadInstallProgress>(OnProgressReport);

        try
        {
            var summary = await InstallCallback!(selected, vramGb, progress, CancellationToken.None);
            DidInstall = true;

            Dispatcher.UIThread.Post(() =>
            {
                AppendLog($"--- {summary} ---");
                var statusText = this.FindControl<TextBlock>("ProgressStatusText");
                if (statusText is not null)
                {
                    statusText.Text = "Installation complete";
                    statusText.Foreground = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse("#4CAF50"));
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                AppendLog($"ERROR: {ex.Message}");
                var statusText = this.FindControl<TextBlock>("ProgressStatusText");
                if (statusText is not null)
                {
                    statusText.Text = "Installation failed";
                    statusText.Foreground = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse("#F44336"));
                }
            });
        }
        finally
        {
            _isInstalling = false;
            Dispatcher.UIThread.Post(() => SetInstallingUiState(false));
        }
    }

    /// <summary>
    /// Called on the UI thread for each progress report from the install service.
    /// </summary>
    private void OnProgressReport(WorkloadInstallProgress p)
    {
        Dispatcher.UIThread.Post(() => AppendLog(p.Message));
    }

    /// <summary>
    /// Appends a line to the progress log and scrolls to the bottom.
    /// </summary>
    private void AppendLog(string message)
    {
        _logLines.Add(message);

        var scroller = this.FindControl<ScrollViewer>("ProgressLogScroller");
        scroller?.ScrollToEnd();
    }

    /// <summary>
    /// Toggles the UI between browsing and installing states.
    /// </summary>
    private void SetInstallingUiState(bool installing)
    {
        var progressPanel = this.FindControl<Border>("ProgressPanel");
        var installButton = this.FindControl<Button>("InstallButton");
        var closeButton = this.FindControl<Button>("CloseButton");
        var grid = this.FindControl<DataGrid>("DetailsGrid");

        if (progressPanel is not null) progressPanel.IsVisible = installing || DidInstall;
        if (installButton is not null) installButton.IsEnabled = !installing && !DidInstall;
        if (closeButton is not null) closeButton.Content = DidInstall ? "Done" : "Close";
        if (grid is not null) grid.IsEnabled = !installing;

        if (installing)
        {
            if (installButton is not null) installButton.Content = "Installing...";
        }
    }

    private void UpdateInstallButtonState()
    {
        var btn = this.FindControl<Button>("InstallButton");
        if (btn is not null)
        {
            btn.IsEnabled = _detailItems.Any(i => i.IsMissing);
        }
    }
}
