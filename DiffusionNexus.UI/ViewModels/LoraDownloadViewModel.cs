using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Classes;

namespace DiffusionNexus.UI.ViewModels;

public partial class LoraDownloadViewModel : ViewModelBase
{
    private readonly CivitaiModelDownloadService _downloadService;
    private readonly LoraMetadataDownloadService _metadataDownloadService;
    private readonly string _apiKey;
    private readonly Action<string, LogSeverity>? _log;
    private readonly AsyncRelayCommand _startDownloadCommand;
    private readonly AsyncRelayCommand _pasteFromClipboardCommand;
    private CancellationTokenSource? _cts;

    public ObservableCollection<LoraDownloadTargetOption> Targets { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartDownloadCommand))]
    private string civitaiUrl = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartDownloadCommand))]
    private LoraDownloadTargetOption? selectedTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartDownloadCommand))]
    private bool isDownloading;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private string progressMessage = string.Empty;

    [ObservableProperty]
    private string speedDisplay = string.Empty;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private bool hasError;

    [ObservableProperty]
    private string statusForeground = "#E0E0E0";

    public IAsyncRelayCommand StartDownloadCommand => _startDownloadCommand;
    public IAsyncRelayCommand PasteFromClipboardCommand => _pasteFromClipboardCommand;

    public IRelayCommand CancelCommand { get; }

    public event EventHandler<bool>? RequestClose;

    public CivitaiModelDownloadResult? Result { get; private set; }

    public LoraDownloadViewModel(
        CivitaiModelDownloadService downloadService,
        LoraMetadataDownloadService metadataDownloadService,
        IEnumerable<LoraHelperSourceModel> sources,
        string apiKey,
        Action<string, LogSeverity>? log = null)
    {
        _downloadService = downloadService;
        _metadataDownloadService = metadataDownloadService;
        _apiKey = apiKey ?? string.Empty;
        _log = log;
        _startDownloadCommand = new AsyncRelayCommand(OnDownloadAsync, CanStartDownload);
        _pasteFromClipboardCommand = new AsyncRelayCommand(OnPasteFromClipboardAsync, CanUseClipboard);
        CancelCommand = new RelayCommand(OnCancel);
        PopulateTargets(sources);
    }

    private void PopulateTargets(IEnumerable<LoraHelperSourceModel> sources)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.FolderPath))
            {
                continue;
            }

            if (!unique.Add(source.FolderPath))
            {
                continue;
            }

            Targets.Add(new LoraDownloadTargetOption(source.FolderPath));
        }

        SelectedTarget = Targets.FirstOrDefault();
    }

    private bool CanStartDownload() =>
        !IsDownloading &&
        !string.IsNullOrWhiteSpace(CivitaiUrl) &&
        SelectedTarget != null;

    private bool CanUseClipboard() => !IsDownloading;

    private async Task OnDownloadAsync()
    {
        if (SelectedTarget == null || string.IsNullOrWhiteSpace(SelectedTarget.FolderPath))
        {
            HasError = true;
            StatusMessage = "Select a target folder.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CivitaiUrl))
        {
            HasError = true;
            StatusMessage = "Enter a valid Civitai link.";
            return;
        }

        HasError = false;
        StatusMessage = "Starting download...";
        ProgressValue = 0;
        ProgressMessage = string.Empty;
        SpeedDisplay = string.Empty;

        _cts = new CancellationTokenSource();
        IsDownloading = true;

        try
        {
            var progress = new Progress<ModelDownloadProgress>(UpdateProgress);
            Result = await _downloadService.DownloadModelAsync(CivitaiUrl.Trim(), SelectedTarget.FolderPath, _apiKey, progress, _cts.Token);

            if (Result.ResultType == ModelDownloadResultType.Success && Result.FilePath != null && Result.VersionInfo != null)
            {
                await HandleSuccessfulDownloadAsync(Result);
                return;
            }

            HandleNonSuccess(Result);
        }
        finally
        {
            IsDownloading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
    private async Task HandleSuccessfulDownloadAsync(CivitaiModelDownloadResult result)
    {
        StatusMessage = "Download complete. Fetching metadata...";
        try
        {
            var model = BuildModel(result);
            var metadataResult = await _metadataDownloadService.EnsureMetadataAsync(model, _apiKey);
            switch (metadataResult.ResultType)
            {
                case MetadataDownloadResultType.Downloaded:
                    StatusMessage = "Download and metadata retrieval completed.";
                    HasError = false;
                    break;
                case MetadataDownloadResultType.AlreadyExists:
                    StatusMessage = "Download finished. Metadata already existed.";
                    HasError = false;
                    break;
                case MetadataDownloadResultType.NotFound:
                    StatusMessage = "Download finished, but metadata was not found on Civitai.";
                    HasError = true;
                    break;
                case MetadataDownloadResultType.Error:
                    StatusMessage = string.IsNullOrWhiteSpace(metadataResult.ErrorMessage)
                        ? "Download finished, but metadata download failed."
                        : $"Download finished, but metadata failed: {metadataResult.ErrorMessage}";
                    HasError = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Download finished, but metadata failed: {ex.Message}";
        }

        if (result.VersionInfo != null && result.FilePath != null)
        {
            _log?.Invoke($"Downloaded LoRA '{result.VersionInfo.VersionName}' to '{result.FilePath}'", LogSeverity.Success);
        }

        RequestClose?.Invoke(this, true);
    }

    private void HandleNonSuccess(CivitaiModelDownloadResult result)
    {
        switch (result.ResultType)
        {
            case ModelDownloadResultType.AlreadyExists:
                StatusMessage = "This model already exists in the selected folder.";
                HasError = false;
                if (!string.IsNullOrWhiteSpace(result.FilePath))
                {
                    _log?.Invoke($"LoRA already exists at '{result.FilePath}'", LogSeverity.Warning);
                }
                break;
            case ModelDownloadResultType.Cancelled:
                StatusMessage = "Download cancelled.";
                HasError = false;
                _log?.Invoke("LoRA download cancelled.", LogSeverity.Warning);
                break;
            case ModelDownloadResultType.Error:
                StatusMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "Download failed."
                    : $"Download failed: {result.ErrorMessage}";
                HasError = true;
                _log?.Invoke(StatusMessage, LogSeverity.Error);
                break;
        }
    }

    private void UpdateProgress(ModelDownloadProgress progress)
    {
        if (progress.TotalBytes.HasValue && progress.TotalBytes.Value > 0)
        {
            var percentage = Math.Clamp(progress.BytesReceived / (double)progress.TotalBytes.Value * 100d, 0d, 100d);
            ProgressValue = percentage;
            ProgressMessage = $"{FormatBytes(progress.BytesReceived)} / {FormatBytes(progress.TotalBytes.Value)}";
        }
        else
        {
            ProgressMessage = $"{FormatBytes(progress.BytesReceived)} downloaded";
        }

        if (progress.BytesPerSecond.HasValue)
        {
            SpeedDisplay = $"{FormatBytes(progress.BytesPerSecond.Value)}/s";
        }
    }

    private void OnCancel()
    {
        if (IsDownloading)
        {
            _cts?.Cancel();
        }
        else
        {
            RequestClose?.Invoke(this, false);
        }
    }

    partial void OnHasErrorChanged(bool value)
    {
        StatusForeground = value ? "#FF6F6F" : "#E0E0E0";
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        _pasteFromClipboardCommand.NotifyCanExecuteChanged();
    }

    private static string FormatBytes(double bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private static ModelClass BuildModel(CivitaiModelDownloadResult result)
    {
        var fileInfo = new FileInfo(result.FilePath!);
        var model = new ModelClass
        {
            SafeTensorFileName = fileInfo.Name,
            ModelVersionName = result.VersionInfo!.VersionName,
            ModelId = result.VersionInfo.ModelId.ToString(CultureInfo.InvariantCulture),
            DiffusionBaseModel = result.VersionInfo.BaseModel ?? string.Empty,
            ModelType = ParseModelType(result.VersionInfo.ModelType),
            TrainedWords = result.VersionInfo.TrainedWords.ToList(),
            AssociatedFilesInfo = new List<FileInfo> { fileInfo },
            SHA256Hash = result.FileInfo?.Sha256
        };

        return model;
    }

    private static DiffusionTypes ParseModelType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return DiffusionTypes.UNASSIGNED;
        }

        var normalized = type.Replace(" ", string.Empty);
        return Enum.TryParse<DiffusionTypes>(normalized, true, out var parsed)
            ? parsed
            : DiffusionTypes.UNASSIGNED;
    }

    public sealed record LoraDownloadTargetOption(string FolderPath)
    {
        public string DisplayName => FolderPath;
    }

    private async Task OnPasteFromClipboardAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is not { Clipboard: { } clipboard })
        {
            HasError = true;
            StatusMessage = "Clipboard is not available.";
            return;
        }

        var text = await clipboard.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            HasError = true;
            StatusMessage = "Clipboard does not contain a link.";
            return;
        }

        CivitaiUrl = text.Trim();
        HasError = false;
        StatusMessage = "Link pasted from clipboard.";
    }
}
