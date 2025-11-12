using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Classes;

namespace DiffusionNexus.UI.ViewModels;

public partial class DownloadLoraViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly LoraDownloadService _downloadService;

    private Window? _window;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty]
    private string? civitaiLink;

    [ObservableProperty]
    private string? selectedFolder;

    [ObservableProperty]
    private bool isDownloading;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string? speedDisplay;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private bool isResolveInProgress;

    public ObservableCollection<string> SourceFolders { get; } = new();

    public IAsyncRelayCommand DownloadCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public event EventHandler<bool>? CloseRequested;

    public DownloadLoraViewModel()
        : this(new SettingsService(), new LoraDownloadService(new CivitaiApiClient(new HttpClient())))
    {
    }

    public DownloadLoraViewModel(ISettingsService settingsService, LoraDownloadService downloadService)
    {
        _settingsService = settingsService;
        _downloadService = downloadService;
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, CanDownload);
        CancelCommand = new RelayCommand(Cancel);
    }

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.LoadAsync();
        SourceFolders.Clear();
        foreach (var source in settings.LoraHelperSources)
        {
            if (!source.IsEnabled || string.IsNullOrWhiteSpace(source.FolderPath))
            {
                continue;
            }

            if (!SourceFolders.Contains(source.FolderPath))
            {
                SourceFolders.Add(source.FolderPath);
            }
        }

        if (SourceFolders.Count > 0 && SelectedFolder == null)
        {
            SelectedFolder = SourceFolders[0];
        }
        else if (SourceFolders.Count == 0)
        {
            StatusMessage = "No enabled LoRA source folders configured.";
        }
    }

    public void SetWindow(Window window)
    {
        _window = window;
    }

    private bool CanDownload()
    {
        return !IsDownloading
            && !IsResolveInProgress
            && !string.IsNullOrWhiteSpace(CivitaiLink)
            && !string.IsNullOrWhiteSpace(SelectedFolder);
    }

    partial void OnCivitaiLinkChanged(string? value)
    {
        DownloadCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedFolderChanged(string? value)
    {
        DownloadCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsResolveInProgressChanged(bool value)
    {
        DownloadCommand.NotifyCanExecuteChanged();
    }

    private async Task DownloadAsync()
    {
        if (!CanDownload())
        {
            return;
        }

        IsResolveInProgress = true;
        StatusMessage = "Resolving Civitai link...";
        Progress = 0;
        SpeedDisplay = null;

        CivitaiModelInfo modelInfo;
        try
        {
            var settings = await _settingsService.LoadAsync();
            var apiKey = settings.CivitaiApiKey ?? string.Empty;
            modelInfo = await _downloadService.ResolveAsync(CivitaiLink!, apiKey);
        }
        catch (Exception ex)
        {
            Log($"Failed to resolve Civitai link: {ex.Message}", LogSeverity.Error);
            StatusMessage = $"Failed to resolve link: {ex.Message}";
            IsResolveInProgress = false;
            DownloadCommand.NotifyCanExecuteChanged();
            return;
        }
        finally
        {
            IsResolveInProgress = false;
        }

        if (SelectedFolder is null)
        {
            StatusMessage = "Please choose a target folder.";
            DownloadCommand.NotifyCanExecuteChanged();
            return;
        }

        var destinationPath = Path.Combine(SelectedFolder, modelInfo.FileName);
        if (File.Exists(destinationPath))
        {
            StatusMessage = $"File '{modelInfo.FileName}' already exists.";
            DownloadCommand.NotifyCanExecuteChanged();
            return;
        }

        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        StatusMessage = "Downloading LoRA...";
        DownloadCommand.NotifyCanExecuteChanged();

        var progress = new Progress<LoraDownloadProgress>(p =>
        {
            if (p.Percentage.HasValue)
            {
                Progress = Math.Round(p.Percentage.Value, 2);
            }

            if (p.BytesPerSecond.HasValue && p.BytesPerSecond.Value > 0)
            {
                SpeedDisplay = FormatSpeed(p.BytesPerSecond.Value);
            }
        });

        try
        {
            var result = await _downloadService.DownloadAsync(modelInfo, SelectedFolder, progress, _downloadCts.Token);
            StatusMessage = "Download completed.";
            Log($"Downloaded LoRA model {result.ModelId} (version {result.ModelVersionId})", LogSeverity.Success);
            CloseRequested?.Invoke(this, true);
            _window?.Close(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Download cancelled.";
            Log("LoRA download cancelled", LogSeverity.Warning);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
            Log($"LoRA download failed: {ex.Message}", LogSeverity.Error);
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            IsDownloading = false;
            Progress = 0;
            SpeedDisplay = null;
            DownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private void Cancel()
    {
        if (IsDownloading)
        {
            _downloadCts?.Cancel();
            return;
        }

        _window?.Close(false);
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        return bytesPerSecond switch
        {
            >= gb => string.Format(CultureInfo.InvariantCulture, "{0:F2} GB/s", bytesPerSecond / gb),
            >= mb => string.Format(CultureInfo.InvariantCulture, "{0:F2} MB/s", bytesPerSecond / mb),
            >= kb => string.Format(CultureInfo.InvariantCulture, "{0:F2} KB/s", bytesPerSecond / kb),
            _ => string.Format(CultureInfo.InvariantCulture, "{0:F0} B/s", bytesPerSecond)
        };
    }
}
