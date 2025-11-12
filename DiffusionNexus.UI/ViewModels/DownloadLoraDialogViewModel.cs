using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Classes;

namespace DiffusionNexus.UI.ViewModels;

public partial class DownloadLoraDialogViewModel : ViewModelBase
{
    private readonly ICivitaiUrlParser _urlParser;
    private readonly ILoraDownloader _downloader;
    private readonly ILoraSourcesProvider _sourcesProvider;
    private readonly IUserSettings _userSettings;
    private readonly string _apiKey;
    private readonly Func<DateTimeOffset> _clock;
    private CancellationTokenSource? _downloadCts;
    private CivitaiLinkInfo? _currentLink;
    private Window? _window;
    private DateTimeOffset? _lastProgressTimestamp;
    private long _lastProgressBytes;
    private bool _isClosing;

    public DownloadLoraDialogViewModel(
        ICivitaiUrlParser urlParser,
        ILoraDownloader downloader,
        ILoraSourcesProvider sourcesProvider,
        IUserSettings userSettings,
        string apiKey,
        Func<DateTimeOffset>? clock = null)
    {
        _urlParser = urlParser ?? throw new ArgumentNullException(nameof(urlParser));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _sourcesProvider = sourcesProvider ?? throw new ArgumentNullException(nameof(sourcesProvider));
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _apiKey = apiKey ?? string.Empty;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        OkCommand = new AsyncRelayCommand(StartDownloadAsync, () => !IsDownloading);
        CancelCommand = new RelayCommand(Cancel);
        PasteFromClipboardCommand = new AsyncRelayCommand(PasteFromClipboardAsync);
    }

    public ObservableCollection<LoraDownloadTargetOption> Targets { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private bool isDownloading;

    [ObservableProperty]
    private string? civitaiUrl;

    [ObservableProperty]
    private LoraDownloadTargetOption? selectedTarget;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string progressText = string.Empty;

    [ObservableProperty]
    private string speedText = string.Empty;

    [ObservableProperty]
    private string etaText = string.Empty;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private bool isOkEnabled;

    [ObservableProperty]
    private bool showProgress;

    public IDialogService? DialogService { get; set; }

    public IAsyncRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand PasteFromClipboardCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var sources = await _sourcesProvider.GetSourcesAsync(cancellationToken).ConfigureAwait(false);

        Targets.Clear();
        foreach (var source in sources)
        {
            Targets.Add(new LoraDownloadTargetOption(source.DisplayName, source.Path));
        }

        var last = await _userSettings.GetLastDownloadLoraTargetAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(last))
        {
            SelectedTarget = Targets.FirstOrDefault(t => string.Equals(t.Path, last, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedTarget == null && Targets.Count > 0)
        {
            SelectedTarget = Targets[0];
        }

        UpdateValidation();
    }

    partial void OnCivitaiUrlChanged(string? value) => UpdateValidation();

    partial void OnSelectedTargetChanged(LoraDownloadTargetOption? value) => UpdateValidation();

    partial void OnIsDownloadingChanged(bool value)
    {
        UpdateIsOkEnabled();
        if (!value)
        {
            ShowProgress = false;
        }
    }

    private void UpdateValidation()
    {
        _currentLink = null;
        ErrorMessage = null;

        if (!_urlParser.TryParse(CivitaiUrl, out var info, out var message))
        {
            if (!string.IsNullOrWhiteSpace(CivitaiUrl))
            {
                ErrorMessage = message;
            }
        }
        else
        {
            _currentLink = info;
        }

        if (SelectedTarget == null)
        {
            ErrorMessage ??= "Select a target folder.";
        }

        UpdateIsOkEnabled();
    }

    private void UpdateIsOkEnabled()
    {
        IsOkEnabled = _currentLink != null && SelectedTarget != null && !IsDownloading && string.IsNullOrWhiteSpace(ErrorMessage);
    }

    private async Task StartDownloadAsync()
    {
        if (_currentLink == null || SelectedTarget == null)
        {
            UpdateValidation();
            return;
        }

        var link = _currentLink!;

        if (!Directory.Exists(SelectedTarget.Path))
        {
            ErrorMessage = "Selected folder does not exist.";
            UpdateIsOkEnabled();
            return;
        }

        try
        {
            var testPath = Path.Combine(SelectedTarget.Path, Path.GetRandomFileName());
            await using (File.Create(testPath, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Cannot write to the selected folder: {ex.Message}";
            UpdateIsOkEnabled();
            return;
        }

        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        ShowProgress = true;
        ErrorMessage = null;
        Progress = 0;
        ProgressText = string.Empty;
        SpeedText = string.Empty;
        EtaText = string.Empty;
        _lastProgressTimestamp = null;
        _lastProgressBytes = 0;

        var progress = new Progress<LoraDownloadProgress>(ReportProgress);

        try
        {
            var result = await _downloader.DownloadAsync(new LoraDownloadRequest
            {
                ModelId = link.ModelId,
                ModelVersionId = link.ModelVersionId,
                TargetDirectory = SelectedTarget.Path,
                ApiKey = _apiKey,
                ConflictResolver = HandleConflictAsync
            }, progress, _downloadCts.Token).ConfigureAwait(false);

            if (result.Status == LoraDownloadResultStatus.Skipped)
            {
                ErrorMessage = "Download skipped.";
                IsDownloading = false;
                return;
            }

            await _userSettings.SetLastDownloadLoraTargetAsync(SelectedTarget.Path).ConfigureAwait(false);
            _window?.Close(true);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                _window?.Close(false);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsDownloading = false;
        }
    }

    private void ReportProgress(LoraDownloadProgress update)
    {
        if (update.TotalBytes.HasValue && update.TotalBytes.Value > 0)
        {
            Progress = Math.Clamp(update.BytesReceived / (double)update.TotalBytes.Value * 100d, 0d, 100d);
            ProgressText = $"{FormatSize(update.BytesReceived)} / {FormatSize(update.TotalBytes.Value)}";
        }
        else
        {
            Progress = 0;
            ProgressText = FormatSize(update.BytesReceived);
            EtaText = string.Empty;
        }

        var now = _clock();
        if (_lastProgressTimestamp is { } previous)
        {
            var elapsed = (now - previous).TotalSeconds;
            if (elapsed > 0)
            {
                var deltaBytes = update.BytesReceived - _lastProgressBytes;
                var bytesPerSecond = deltaBytes / elapsed;
                if (bytesPerSecond > 0)
                {
                    SpeedText = $"{bytesPerSecond / (1024 * 1024):F2} MB/s";
                    if (update.TotalBytes.HasValue)
                    {
                        var remaining = update.TotalBytes.Value - update.BytesReceived;
                        if (remaining > 0)
                        {
                            var etaSeconds = remaining / bytesPerSecond;
                            var eta = TimeSpan.FromSeconds(etaSeconds);
                            EtaText = $"ETA ~ {eta:mm\\:ss}";
                        }
                        else
                        {
                            EtaText = string.Empty;
                        }
                    }
                }
                else if (!update.TotalBytes.HasValue)
                {
                    EtaText = string.Empty;
                }
            }
        }

        _lastProgressTimestamp = now;
        _lastProgressBytes = update.BytesReceived;
    }

    private void Cancel()
    {
        if (IsDownloading)
        {
            _downloadCts?.Cancel();
        }
        else
        {
            _window?.Close(false);
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        if (_window?.Clipboard is { } clipboard)
        {
            var text = await clipboard.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                CivitaiUrl = text.Trim();
            }
        }
    }

    private async Task<LoraDownloadConflictResolution> HandleConflictAsync(LoraDownloadConflictContext context)
    {
        if (_window is null)
        {
            return new LoraDownloadConflictResolution(LoraDownloadConflictResolutionType.Skip);
        }

        var tcs = new TaskCompletionSource<LoraDownloadConflictResolution>();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var dialog = new Window
            {
                Width = 420,
                Height = 210,
                Title = "File already exists",
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var overwriteButton = new Button { Content = "Overwrite", Width = 110 };
            var skipButton = new Button { Content = "Skip", Width = 110 };
            var saveAsButton = new Button { Content = "Save As", Width = 110 };

            void Complete(LoraDownloadConflictResolution resolution)
            {
                if (!tcs.TrySetResult(resolution))
                    return;
                dialog.Close();
            }

            overwriteButton.Click += (_, _) => Complete(new LoraDownloadConflictResolution(LoraDownloadConflictResolutionType.Overwrite));
            skipButton.Click += (_, _) => Complete(new LoraDownloadConflictResolution(LoraDownloadConflictResolutionType.Skip));
            saveAsButton.Click += async (_, _) =>
            {
                string? newName = null;
                if (DialogService != null)
                {
                    newName = await DialogService.ShowInputAsync("Choose a new file name", context.SuggestedFileName);
                }
                else
                {
                    newName = await ShowInputDialogAsync(context.SuggestedFileName);
                }

                if (string.IsNullOrWhiteSpace(newName))
                {
                    return;
                }

                Complete(new LoraDownloadConflictResolution(LoraDownloadConflictResolutionType.Rename, newName.Trim()));
            };

            dialog.Closed += (_, _) =>
            {
                if (!tcs.Task.IsCompleted)
                {
                    tcs.TrySetResult(new LoraDownloadConflictResolution(LoraDownloadConflictResolutionType.Skip));
                }
            };

            var text = new TextBlock
            {
                Text = $"'{Path.GetFileName(context.ExistingFilePath)}' already exists in the target folder.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    text,
                    new TextBlock { Text = "Choose what to do:", FontWeight = FontWeight.SemiBold },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { overwriteButton, skipButton, saveAsButton }
                    }
                }
            };

            _ = dialog.ShowDialog(_window);
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<string?> ShowInputDialogAsync(string suggestedFileName)
    {
        var localTcs = new TaskCompletionSource<string?>();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_window is null)
            {
                localTcs.TrySetResult(null);
                return;
            }

            var dialog = new Window
            {
                Width = 360,
                Height = 170,
                Title = "Save As",
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var textBox = new TextBox { Text = suggestedFileName };
            var okButton = new Button { Content = "OK", Width = 90 };
            var cancelButton = new Button { Content = "Cancel", Width = 90 };

            okButton.Click += (_, _) =>
            {
                localTcs.TrySetResult(textBox.Text);
                dialog.Close();
            };

            cancelButton.Click += (_, _) =>
            {
                localTcs.TrySetResult(null);
                dialog.Close();
            };

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Enter a new file name", FontWeight = FontWeight.SemiBold },
                    textBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { okButton, cancelButton }
                    }
                }
            };

            _ = dialog.ShowDialog(_window);
        });

        return await localTcs.Task.ConfigureAwait(false);
    }

    private static string FormatSize(long bytes)
    {
        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:F2} {units[unitIndex]}";
    }

    public void SetWindow(Window window)
    {
        _window = window;
    }

    public void OnWindowClosing()
    {
        _isClosing = true;
        if (IsDownloading)
        {
            _downloadCts?.Cancel();
        }
    }
}

public record LoraDownloadTargetOption(string DisplayName, string Path)
{
    public override string ToString() => DisplayName;
}
