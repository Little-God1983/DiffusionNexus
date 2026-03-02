using System.Collections.ObjectModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Detail dialog showing which models and custom nodes are installed or missing.
/// Allows the user to select missing items and trigger installation with live progress,
/// including a real-time download progress bar and the ability to skip individual downloads.
/// </summary>
public partial class WorkloadDetailsDialog : Window
{
    private readonly ObservableCollection<string> _logLines = [];
    private bool _isInstalling;

    /// <summary>
    /// Token source used to skip the current model download.
    /// Recreated for each new download so that previously-skipped tokens don't affect later files.
    /// </summary>
    private CancellationTokenSource? _skipDownloadCts;

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
    /// Parameters: selected items, VRAM GB, install progress, download progress, skip token provider, cancellation token.
    /// Returns a summary string.
    /// </summary>
    public Func<IReadOnlyList<WorkloadDetailItemViewModel>, int, IProgress<WorkloadInstallProgress>,
        IProgress<DownloadProgress>, Func<CancellationToken>, CancellationToken, Task<string>>?
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

    private void OnSkipDownload(object sender, RoutedEventArgs e)
    {
        if (_skipDownloadCts is not null && !_skipDownloadCts.IsCancellationRequested)
        {
            AppendLog("Skipping current download...");
            _skipDownloadCts.Cancel();

            // Create a fresh token source for the next download
            var old = _skipDownloadCts;
            _skipDownloadCts = new CancellationTokenSource();
            old.Dispose();
        }
    }

    /// <summary>
    /// Returns the current skip token. Called by the download handler to check for skip requests.
    /// </summary>
    private CancellationToken GetSkipDownloadToken()
    {
        _skipDownloadCts ??= new CancellationTokenSource();
        return _skipDownloadCts.Token;
    }

    /// <summary>
    /// Executes the install callback while showing live progress in the dialog.
    /// </summary>
    private async Task RunInstallWithProgressAsync(
        IReadOnlyList<WorkloadDetailItemViewModel> selected, int vramGb)
    {
        _isInstalling = true;
        _skipDownloadCts = new CancellationTokenSource();
        SetInstallingUiState(true);

        var installProgress = new Progress<WorkloadInstallProgress>(OnInstallProgressReport);
        var downloadProgress = new Progress<DownloadProgress>(OnDownloadProgressReport);

        try
        {
            var summary = await InstallCallback!(
                selected, vramGb, installProgress, downloadProgress, GetSkipDownloadToken, CancellationToken.None);
            DidInstall = true;

            HideDownloadProgress();
            AppendLog($"--- {summary} ---");
            var statusText = this.FindControl<TextBlock>("ProgressStatusText");
            if (statusText is not null)
            {
                statusText.Text = "Installation complete";
                statusText.Foreground = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.Parse("#4CAF50"));
            }
        }
        catch (Exception ex)
        {
            HideDownloadProgress();
            AppendLog($"ERROR: {ex.Message}");
            var statusText = this.FindControl<TextBlock>("ProgressStatusText");
            if (statusText is not null)
            {
                statusText.Text = "Installation failed";
                statusText.Foreground = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.Parse("#F44336"));
            }
        }
        finally
        {
            _isInstalling = false;
            _skipDownloadCts?.Dispose();
            _skipDownloadCts = null;
            SetInstallingUiState(false);
        }
    }

    /// <summary>
    /// Called on the UI thread for each install step progress report.
    /// When an item reports success, marks the matching grid row as installed
    /// so its status turns green immediately.
    /// </summary>
    /// <remarks>
    /// <see cref="Progress{T}"/> already marshals to the captured SynchronizationContext
    /// (the UI thread), so we update controls directly — no extra Dispatcher.Post needed.
    /// </remarks>
    private void OnInstallProgressReport(WorkloadInstallProgress p)
    {
        AppendLog(p.Message);

        if (p.IsSuccess && p.ItemId != Guid.Empty)
        {
            var match = _detailItems.FirstOrDefault(i => i.Id == p.ItemId);
            if (match is not null && !match.IsInstalled)
            {
                match.IsInstalled = true;
                match.IsSelected = false;
            }
        }
    }

    /// <summary>
    /// Called on the UI thread for each download byte-level progress report.
    /// Updates the progress bar, file name, size text, and speed text.
    /// </summary>
    private void OnDownloadProgressReport(DownloadProgress p)
    {
        var panel = this.FindControl<Border>("DownloadProgressPanel");
        var bar = this.FindControl<ProgressBar>("DownloadProgressBar");
        var fileName = this.FindControl<TextBlock>("DownloadFileNameText");
        var sizeText = this.FindControl<TextBlock>("DownloadSizeText");
        var speedText = this.FindControl<TextBlock>("DownloadSpeedText");

        if (p.IsComplete || !p.IsActive)
        {
            // Download finished or inactive — hide the bar
            if (panel is not null) panel.IsVisible = false;
            return;
        }

        // Show the download progress panel
        if (panel is not null) panel.IsVisible = true;
        if (fileName is not null) fileName.Text = p.FileName;
        if (speedText is not null) speedText.Text = p.SpeedText;

        if (p.TotalBytes.HasValue && p.TotalBytes > 0)
        {
            if (bar is not null)
            {
                bar.IsIndeterminate = false;
                bar.Maximum = p.TotalBytes.Value;
                bar.Value = p.BytesDownloaded;
            }
            if (sizeText is not null) sizeText.Text = $"{p.DownloadedSizeText} / {p.TotalSizeText}";
        }
        else
        {
            // Unknown total — show indeterminate-style
            if (bar is not null)
            {
                bar.Maximum = 100;
                bar.Value = 0;
                bar.IsIndeterminate = true;
            }
            if (sizeText is not null) sizeText.Text = p.DownloadedSizeText;
        }
    }

    /// <summary>
    /// Hides the download progress bar panel.
    /// </summary>
    private void HideDownloadProgress()
    {
        var panel = this.FindControl<Border>("DownloadProgressPanel");
        if (panel is not null) panel.IsVisible = false;
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
