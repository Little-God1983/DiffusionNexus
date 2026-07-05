using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for composing and submitting an in-app feedback/bug report.
/// </summary>
public partial class FeedbackDialog : Window
{
    private readonly IFeedbackReportingService _feedbackService;
    private readonly FeedbackProduct _product;
    private readonly string _appVersion;
    private readonly string _logTail;
    private byte[]? _screenshotBytes;
    private string? _lastIssueUrl;

    public FeedbackDialog() : this(
        new FeedbackReportingService(new FeedbackReportingServiceOptions { RelayUrl = "https://example.com" }),
        FeedbackProduct.MainApp,
        "0.0.0",
        string.Empty,
        null)
    {
    }

    public FeedbackDialog(
        IFeedbackReportingService feedbackService,
        FeedbackProduct product,
        string appVersion,
        string logTail,
        byte[]? initialScreenshot)
    {
        InitializeComponent();

        _feedbackService = feedbackService;
        _product = product;
        _appVersion = appVersion;
        _logTail = logTail;
        _screenshotBytes = initialScreenshot;

        UpdateScreenshotPreview();

        SubmitButton.Click += OnSubmitClick;
        CancelButton.Click += (_, _) => Close();
        ReplaceScreenshotButton.Click += OnReplaceScreenshotClick;
        RemoveScreenshotButton.Click += (_, _) =>
        {
            _screenshotBytes = null;
            UpdateScreenshotPreview();
        };
        CloseAfterSuccessButton.Click += (_, _) => Close();
        IssueUrlButton.Click += (_, _) =>
        {
            if (_lastIssueUrl is not null) OpenUrl(_lastIssueUrl);
        };
    }

    private void UpdateScreenshotPreview()
    {
        if (_screenshotBytes is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(_screenshotBytes);
                ScreenshotPreviewImage.Source = new Bitmap(stream);
                ScreenshotPreviewImage.IsVisible = true;
                RemoveScreenshotButton.IsVisible = true;
                StatusText.IsVisible = false;
            }
            catch
            {
                // Image file is corrupted or invalid
                _screenshotBytes = null;
                ScreenshotPreviewImage.Source = null;
                ScreenshotPreviewImage.IsVisible = false;
                RemoveScreenshotButton.IsVisible = false;
                StatusText.Text = "Couldn't load that image file — it may be corrupted or not a valid image.";
                StatusText.IsVisible = true;
            }
        }
        else
        {
            ScreenshotPreviewImage.Source = null;
            ScreenshotPreviewImage.IsVisible = false;
            RemoveScreenshotButton.IsVisible = false;
        }
    }

    private async void OnReplaceScreenshotClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a screenshot",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }]
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        await using var stream = await file.OpenReadAsync();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);

        try
        {
            _screenshotBytes = ScreenshotCapture.DownscaleIfNeeded(memoryStream.ToArray());
        }
        catch
        {
            // Picked file is corrupted or not a valid image.
            StatusText.Text = "Couldn't load that image file — it may be corrupted or not a valid image.";
            StatusText.IsVisible = true;
            return;
        }

        UpdateScreenshotPreview();
    }

    private async void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim() ?? string.Empty;
        var description = DescriptionBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
        {
            StatusText.Text = "Title and description are required.";
            StatusText.IsVisible = true;
            return;
        }

        SetSubmitting(true);

        var report = new FeedbackReport
        {
            Product = _product,
            Title = title,
            Description = description,
            WhatHappened = string.IsNullOrWhiteSpace(WhatHappenedBox.Text) ? null : WhatHappenedBox.Text!.Trim(),
            WhatShouldHaveHappened = string.IsNullOrWhiteSpace(WhatShouldHaveHappenedBox.Text) ? null : WhatShouldHaveHappenedBox.Text!.Trim(),
            ScreenshotPng = _screenshotBytes,
            LogTail = _logTail,
            AppVersion = _appVersion,
            Os = RuntimeInformation.OSDescription,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        var result = await _feedbackService.SubmitAsync(report);

        SetSubmitting(false);

        if (result.Success)
        {
            ShowSuccess(result.IssueUrl!);
        }
        else
        {
            StatusText.Text = $"Couldn't submit feedback: {result.ErrorMessage}. Your text hasn't been lost — try again.";
            StatusText.IsVisible = true;
        }
    }

    private void SetSubmitting(bool submitting)
    {
        SubmitButton.IsEnabled = !submitting;
        CancelButton.IsEnabled = !submitting;
        SubmittingIndicator.IsVisible = submitting;
    }

    private void ShowSuccess(string issueUrl)
    {
        _lastIssueUrl = issueUrl;
        FormPanel.IsVisible = false;
        SuccessPanel.IsVisible = true;
        IssueUrlText.Text = issueUrl;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", url);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
        }
        catch
        {
            // Ignore URL open failures
        }
    }
}
