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
    private bool _isSubmitting;

    /// <summary>True once a report was successfully submitted.</summary>
    public bool WasSubmitted { get; private set; }

    /// <summary>The e-mail the user submitted with (null/empty allowed). Only meaningful when <see cref="WasSubmitted"/>.</summary>
    public string? SubmittedEmail { get; private set; }

    public FeedbackDialog() : this(
        new FeedbackReportingService(new FeedbackReportingServiceOptions { RelayUrl = "https://example.com" }),
        FeedbackProduct.MainApp,
        "0.0.0",
        string.Empty,
        null,
        null)
    {
    }

    public FeedbackDialog(
        IFeedbackReportingService feedbackService,
        FeedbackProduct product,
        string appVersion,
        string logTail,
        byte[]? initialScreenshot,
        string? initialEmail)
    {
        InitializeComponent();

        _feedbackService = feedbackService;
        _product = product;
        _appVersion = appVersion;
        _logTail = logTail;
        _screenshotBytes = initialScreenshot;
        EmailBox.Text = initialEmail;

        UpdateScreenshotPreview();

        SubmitButton.Click += OnSubmitClick;
        CancelButton.Click += (_, _) => Close();
        ReplaceScreenshotButton.Click += OnReplaceScreenshotClick;
        RemoveScreenshotButton.Click += (_, _) =>
        {
            _screenshotBytes = null;
            UpdateScreenshotPreview();
        };
        DisclaimerCheckBox.IsCheckedChanged += (_, _) => UpdateSubmitEnabled();
        UpdateSubmitEnabled();
        CloseAfterSuccessButton.Click += (_, _) => Close();
        SubmitAnotherButton.Click += OnSubmitAnotherClick;
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

        var email = EmailBox.Text?.Trim();
        if (!string.IsNullOrEmpty(email) && !LooksLikeEmail(email))
        {
            StatusText.Text = "That e-mail address doesn't look valid — fix it or leave the field empty.";
            StatusText.IsVisible = true;
            return;
        }

        var reportType = TypeFeedbackRadio.IsChecked == true ? FeedbackReportType.Feedback
            : TypeFeatureRadio.IsChecked == true ? FeedbackReportType.FeatureRequest
            : FeedbackReportType.Bug;

        SetSubmitting(true);

        var report = new FeedbackReport
        {
            Product = _product,
            ReportType = reportType,
            Title = title,
            Description = description,
            WhatHappened = string.IsNullOrWhiteSpace(WhatHappenedBox.Text) ? null : WhatHappenedBox.Text!.Trim(),
            WhatShouldHaveHappened = string.IsNullOrWhiteSpace(WhatShouldHaveHappenedBox.Text) ? null : WhatShouldHaveHappenedBox.Text!.Trim(),
            Email = string.IsNullOrEmpty(email) ? null : email,
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
            ShowSuccess(email);
        }
        else
        {
            StatusText.Text = $"Couldn't submit feedback: {result.ErrorMessage}. Your text hasn't been lost — try again.";
            StatusText.IsVisible = true;
        }
    }

    private void OnSubmitAnotherClick(object? sender, RoutedEventArgs e)
    {
        TitleBox.Text = string.Empty;
        DescriptionBox.Text = string.Empty;
        WhatHappenedBox.Text = string.Empty;
        WhatShouldHaveHappenedBox.Text = string.Empty;
        _screenshotBytes = null;
        UpdateScreenshotPreview();
        TypeBugRadio.IsChecked = true;
        DisclaimerCheckBox.IsChecked = false;
        StatusText.IsVisible = false;
        UpdateSubmitEnabled();

        SuccessPanel.IsVisible = false;
        FormPanel.IsVisible = true;
    }

    private void UpdateSubmitEnabled()
    {
        SubmitButton.IsEnabled = !_isSubmitting && DisclaimerCheckBox.IsChecked == true;
    }

    private void SetSubmitting(bool submitting)
    {
        _isSubmitting = submitting;
        UpdateSubmitEnabled();
        CancelButton.IsEnabled = !submitting;
        SubmittingIndicator.IsVisible = submitting;
    }

    private void ShowSuccess(string? email)
    {
        WasSubmitted = true;
        SubmittedEmail = string.IsNullOrEmpty(email) ? null : email;

        FormPanel.IsVisible = false;
        SuccessPanel.IsVisible = true;
    }

    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@')) return false;
        var domain = email[(at + 1)..];
        return domain.Length >= 3 && domain.Contains('.');
    }
}
