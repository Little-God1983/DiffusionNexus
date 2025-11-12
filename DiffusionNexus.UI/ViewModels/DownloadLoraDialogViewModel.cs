using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Services;

namespace DiffusionNexus.UI.ViewModels;

public partial class DownloadLoraDialogViewModel : ViewModelBase
{
    private readonly ICivitaiUrlParser _urlParser;
    private readonly ILoraDownloader _loraDownloader;
    private readonly string? _apiKey;
    private readonly Func<string, Task>? _saveLastTargetAsync;
    private CancellationTokenSource? _downloadCts;
    private bool _isUrlValid;
    private TaskCompletionSource<CollisionResolution>? _collisionTcs;
    private bool _cancellationRequested;

    public ObservableCollection<DownloadTargetOption> Targets { get; }

    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty]
    private string? civitaiUrl;

    [ObservableProperty]
    private DownloadTargetOption? selectedTarget;

    [ObservableProperty]
    private bool isDownloading;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string progressStatus = string.Empty;

    [ObservableProperty]
    private double speedMbps;

    [ObservableProperty]
    private string? etaText;

    [ObservableProperty]
    private string? urlError;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private bool isCollisionPromptVisible;

    [ObservableProperty]
    private string? collisionMessage;

    [ObservableProperty]
    private string? saveAsFileName;

    public bool WasSuccessful { get; private set; }

    public bool IsOkEnabled => _isUrlValid && !IsDownloading && SelectedTarget != null && !IsCollisionPromptVisible;

    public IAsyncRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand PasteFromClipboardCommand { get; }
    public IRelayCommand OverwriteCommand { get; }
    public IRelayCommand SkipCommand { get; }
    public IRelayCommand SaveAsCommand { get; }

    public DownloadLoraDialogViewModel(
        ICivitaiUrlParser urlParser,
        ILoraDownloader loraDownloader,
        IEnumerable<DownloadTargetOption> targets,
        string? apiKey,
        string? lastTargetPath,
        Func<string, Task>? saveLastTargetAsync)
    {
        _urlParser = urlParser ?? throw new ArgumentNullException(nameof(urlParser));
        _loraDownloader = loraDownloader ?? throw new ArgumentNullException(nameof(loraDownloader));
        _apiKey = apiKey;
        _saveLastTargetAsync = saveLastTargetAsync;

        Targets = new ObservableCollection<DownloadTargetOption>(targets ?? Enumerable.Empty<DownloadTargetOption>());

        if (!string.IsNullOrWhiteSpace(lastTargetPath))
        {
            SelectedTarget = Targets.FirstOrDefault(t => string.Equals(t.FullPath, lastTargetPath, StringComparison.OrdinalIgnoreCase));
        }

        OkCommand = new AsyncRelayCommand(ExecuteOkAsync, () => IsOkEnabled);
        CancelCommand = new RelayCommand(OnCancel);
        PasteFromClipboardCommand = new AsyncRelayCommand(PasteFromClipboardAsync);
        OverwriteCommand = new RelayCommand(() => ResolveCollision(CollisionResolution.Overwrite), () => IsCollisionPromptVisible);
        SkipCommand = new RelayCommand(() => ResolveCollision(CollisionResolution.Skip), () => IsCollisionPromptVisible);
        SaveAsCommand = new RelayCommand(() => ResolveCollision(CollisionResolution.SaveAs), () => IsCollisionPromptVisible);
    }

    partial void OnCivitaiUrlChanged(string? value)
    {
        ValidateUrl(value);
        OkCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsOkEnabled));
    }

    partial void OnSelectedTargetChanged(DownloadTargetOption? value)
    {
        OkCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsOkEnabled));
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        OkCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChangedIfSupported();
        OnPropertyChanged(nameof(IsOkEnabled));
    }

    partial void OnIsCollisionPromptVisibleChanged(bool value)
    {
        OkCommand.NotifyCanExecuteChanged();
        OverwriteCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsOkEnabled));
    }

    public void CancelDownload()
    {
        _cancellationRequested = true;
        _downloadCts?.Cancel();
    }

    public bool HandleWindowClosing()
    {
        if (IsDownloading)
        {
            CancelDownload();
            return true;
        }

        return false;
    }

    private async Task ExecuteOkAsync()
    {
        ErrorMessage = null;

        if (!_urlParser.TryParse(CivitaiUrl, out var info, out var normalizedUrl, out var parseError))
        {
            UrlError = parseError;
            _isUrlValid = false;
            return;
        }

        UrlError = null;
        _isUrlValid = true;
        if (!string.IsNullOrWhiteSpace(normalizedUrl) && !string.Equals(CivitaiUrl, normalizedUrl, StringComparison.Ordinal))
        {
            CivitaiUrl = normalizedUrl;
        }

        if (SelectedTarget is null)
        {
            ErrorMessage = "Select a target folder.";
            return;
        }

        if (!Directory.Exists(SelectedTarget.FullPath))
        {
            ErrorMessage = "Target folder no longer exists.";
            return;
        }

        if (!IsDirectoryWritable(SelectedTarget.FullPath))
        {
            ErrorMessage = "Target folder is not writable.";
            return;
        }

        IsDownloading = true;
        Progress = 0;
        SpeedMbps = 0;
        EtaText = null;
        ProgressStatus = string.Empty;
        _cancellationRequested = false;
        _downloadCts = new CancellationTokenSource();

        try
        {
            var plan = await _loraDownloader.PrepareAsync(info.ModelId, info.ModelVersionId, _apiKey, _downloadCts.Token);
            var targetPath = CombineSafe(SelectedTarget.FullPath, plan.FileName);

            if (File.Exists(targetPath))
            {
                var resolution = await PromptForCollisionAsync(SelectedTarget.FullPath, plan.FileName, _downloadCts.Token);
                if (resolution == CollisionResolution.Skip)
                {
                    ErrorMessage = "Download skipped.";
                    return;
                }

                if (resolution == CollisionResolution.SaveAs)
                {
                    targetPath = CombineSafe(SelectedTarget.FullPath, SaveAsFileName ?? plan.FileName);
                }
                else if (resolution == CollisionResolution.Overwrite && File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
            }

            var progress = new Progress<LoraDownloadProgress>(UpdateProgress);
            await _loraDownloader.DownloadAsync(plan, targetPath, progress, _downloadCts.Token);

            if (_saveLastTargetAsync != null)
            {
                await _saveLastTargetAsync(SelectedTarget.FullPath);
            }

            WasSuccessful = true;
            RequestClose(true);
        }
        catch (OperationCanceledException)
        {
            if (_cancellationRequested)
            {
                RequestClose(false);
            }
            else
            {
                ErrorMessage = "Download cancelled.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            IsDownloading = false;
            CancelCollisionPrompt();
        }
    }

    private async Task<CollisionResolution> PromptForCollisionAsync(string folder, string fileName, CancellationToken ct)
    {
        SaveAsFileName = GenerateSaveAsName(folder, fileName);
        CollisionMessage = $"The file '{fileName}' already exists.";
        IsCollisionPromptVisible = true;
        _collisionTcs = new TaskCompletionSource<CollisionResolution>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (ct.Register(() => _collisionTcs?.TrySetCanceled()))
        {
            try
            {
                return await _collisionTcs.Task.ConfigureAwait(false);
            }
            finally
            {
                IsCollisionPromptVisible = false;
            }
        }
    }

    private void CancelCollisionPrompt()
    {
        IsCollisionPromptVisible = false;
        CollisionMessage = null;
        SaveAsFileName = null;
        _collisionTcs?.TrySetCanceled();
        _collisionTcs = null;
    }

    private void ResolveCollision(CollisionResolution resolution)
    {
        _collisionTcs?.TrySetResult(resolution);
    }

    private void UpdateProgress(LoraDownloadProgress progress)
    {
        RunOnUiThread(() =>
        {
            Progress = progress.Percentage ?? 0;
            SpeedMbps = progress.SpeedMbps;
            EtaText = progress.EstimatedRemaining.HasValue && progress.EstimatedRemaining > TimeSpan.Zero
                ? $"ETA ~ {progress.EstimatedRemaining:hh\\:mm\\:ss}"
                : null;

            ProgressStatus = BuildProgressStatus(progress.BytesReceived, progress.TotalBytes, progress.SpeedMbps);
        });
    }

    private static string BuildProgressStatus(long received, long? total, double speed)
    {
        var builder = new StringBuilder();
        builder.Append($"Downloaded {FormatBytes(received)}");
        if (total.HasValue)
        {
            builder.Append($" / {FormatBytes(total.Value)}");
        }

        builder.Append($" ({speed:F2} MB/s)");
        return builder.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        var sizes = new[] { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        var order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    private static string CombineSafe(string folder, string fileName)
    {
        var fullFolder = Path.GetFullPath(folder);
        var combined = Path.GetFullPath(Path.Combine(fullFolder, fileName));
        if (!combined.StartsWith(fullFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !combined.Equals(fullFolder, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Attempted to write outside of the target folder.");
        }

        return combined;
    }

    private static string GenerateSaveAsName(string folder, string fileName)
    {
        var directory = Path.GetFullPath(folder);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 1;
        string candidate;
        do
        {
            candidate = $"{baseName} ({index}){extension}";
            index++;
        }
        while (File.Exists(Path.Combine(directory, candidate)));

        return candidate;
    }

    private static bool IsDirectoryWritable(string path)
    {
        try
        {
            var testFile = Path.Combine(path, Path.GetRandomFileName());
            using (File.Create(testFile, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
            }
        }
        else
        {
            action();
        }
    }

    private void ValidateUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            UrlError = null;
            _isUrlValid = false;
            return;
        }

        if (_urlParser.TryParse(value, out _, out _, out var error))
        {
            UrlError = null;
            _isUrlValid = true;
        }
        else
        {
            UrlError = error;
            _isUrlValid = false;
        }

        OnPropertyChanged(nameof(IsOkEnabled));
    }

    private async Task PasteFromClipboardAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { Clipboard: { } clipboard })
        {
            var text = await clipboard.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                CivitaiUrl = text.Trim();
            }
        }
    }

    private void OnCancel()
    {
        if (IsDownloading)
        {
            CancelDownload();
        }
        else
        {
            RequestClose(false);
        }
    }

    private void RequestClose(bool result)
    {
        CloseRequested?.Invoke(this, result);
    }

    private enum CollisionResolution
    {
        Overwrite,
        Skip,
        SaveAs
    }
}

internal static class RelayCommandExtensions
{
    public static void NotifyCanExecuteChangedIfSupported(this IRelayCommand command)
    {
        switch (command)
        {
            case RelayCommand relayCommand:
                relayCommand.NotifyCanExecuteChanged();
                break;
            case IAsyncRelayCommand asyncCommand:
                asyncCommand.NotifyCanExecuteChanged();
                break;
        }
    }
}
