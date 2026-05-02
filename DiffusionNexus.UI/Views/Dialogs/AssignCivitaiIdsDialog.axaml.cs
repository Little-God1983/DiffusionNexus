using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog that lets the user manually assign Civitai Model/Version IDs to a local
/// LoRA when the API/hash lookup failed to detect them. The user can paste the
/// full Civitai URL or fill in the IDs separately, run a Search to fetch the
/// model, preview it, and confirm before persisting.
/// </summary>
public partial class AssignCivitaiIdsDialog : Window
{
    public static readonly StyledProperty<string> UrlTextProperty =
        AvaloniaProperty.Register<AssignCivitaiIdsDialog, string>(nameof(UrlText), defaultValue: string.Empty);

    public static readonly StyledProperty<string> ModelIdTextProperty =
        AvaloniaProperty.Register<AssignCivitaiIdsDialog, string>(nameof(ModelIdText), defaultValue: string.Empty);

    public static readonly StyledProperty<string> VersionIdTextProperty =
        AvaloniaProperty.Register<AssignCivitaiIdsDialog, string>(nameof(VersionIdText), defaultValue: string.Empty);

    public AssignCivitaiIdsDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public string UrlText
    {
        get => GetValue(UrlTextProperty);
        set => SetValue(UrlTextProperty, value);
    }

    public string ModelIdText
    {
        get => GetValue(ModelIdTextProperty);
        set => SetValue(ModelIdTextProperty, value);
    }

    public string VersionIdText
    {
        get => GetValue(VersionIdTextProperty);
        set => SetValue(VersionIdTextProperty, value);
    }

    /// <summary>True when the user clicked "Confirm &amp; Save" with a valid model loaded.</summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>The Civitai model fetched by the most recent successful search.</summary>
    public CivitaiModel? ResolvedModel { get; private set; }

    /// <summary>The selected version (when a Version ID was provided), otherwise the first version of <see cref="ResolvedModel"/>.</summary>
    public CivitaiModelVersion? ResolvedVersion { get; private set; }

    private async void OnSearchClick(object? sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        ResolvedModel = null;
        ResolvedVersion = null;

        var statusText = this.FindControl<TextBlock>("StatusText")!;
        var preview = this.FindControl<Border>("PreviewBorder")!;
        var progress = this.FindControl<ProgressBar>("SearchProgress")!;
        var searchButton = this.FindControl<Button>("SearchButton")!;
        var confirmButton = this.FindControl<Button>("ConfirmButton")!;

        statusText.IsVisible = false;
        preview.IsVisible = false;
        confirmButton.IsEnabled = false;

        if (!TryResolveIds(out var modelId, out var versionId, out var error))
        {
            statusText.Text = error;
            statusText.Foreground = Avalonia.Media.Brushes.IndianRed;
            statusText.IsVisible = true;
            return;
        }

        try
        {
            progress.IsVisible = true;
            searchButton.IsEnabled = false;

            var civitaiClient = App.Services?.GetService<ICivitaiClient>();
            if (civitaiClient is null)
            {
                statusText.Text = "Civitai client is not available.";
                statusText.Foreground = Avalonia.Media.Brushes.IndianRed;
                statusText.IsVisible = true;
                return;
            }

            string? apiKey = null;
            var settings = App.Services?.GetService<IAppSettingsService>();
            if (settings is not null)
            {
                apiKey = await settings.GetCivitaiApiKeyAsync();
            }

            CivitaiModel? model = null;
            CivitaiModelVersion? version = null;

            if (modelId.HasValue)
            {
                model = await civitaiClient.GetModelAsync(modelId.Value, apiKey);
            }
            else if (versionId.HasValue)
            {
                version = await civitaiClient.GetModelVersionAsync(versionId.Value, apiKey);
                if (version is not null && version.ModelId > 0)
                {
                    model = await civitaiClient.GetModelAsync(version.ModelId, apiKey);
                }
            }

            if (model is null)
            {
                statusText.Text = "No model found for the supplied ID(s). Double-check the values and try again.";
                statusText.Foreground = Avalonia.Media.Brushes.IndianRed;
                statusText.IsVisible = true;
                return;
            }

            // Resolve the matching version, preferring the explicit Version ID.
            if (versionId.HasValue)
            {
                version = model.ModelVersions.FirstOrDefault(v => v.Id == versionId.Value) ?? version;
                if (version is null)
                {
                    statusText.Text = $"Model {model.Id} found, but it has no version with ID {versionId.Value}.";
                    statusText.Foreground = Avalonia.Media.Brushes.IndianRed;
                    statusText.IsVisible = true;
                    return;
                }
            }
            else
            {
                version ??= model.ModelVersions.FirstOrDefault();
            }

            ResolvedModel = model;
            ResolvedVersion = version;

            await PopulatePreviewAsync(model, version);
            preview.IsVisible = true;
            confirmButton.IsEnabled = true;
        }
        catch (HttpRequestException ex)
        {
            statusText.Text = $"Civitai request failed: {ex.StatusCode} {ex.Message}";
            statusText.Foreground = Avalonia.Media.Brushes.IndianRed;
            statusText.IsVisible = true;
        }
        catch (Exception ex)
        {
            App.Services?.GetService<IUnifiedLogger>()?.Warn(LogCategory.Network, "AssignCivitaiIds",
                $"Search failed: {ex.Message}");
            statusText.Text = $"Search failed: {ex.Message}";
            statusText.Foreground = Avalonia.Media.Brushes.IndianRed;
            statusText.IsVisible = true;
        }
        finally
        {
            progress.IsVisible = false;
            searchButton.IsEnabled = true;
        }
    }

    private bool TryResolveIds(out int? modelId, out int? versionId, out string error)
    {
        modelId = null;
        versionId = null;
        error = string.Empty;

        var url = (UrlText ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(url))
        {
            var modelMatch = Regex.Match(url, @"/models/(\d+)", RegexOptions.IgnoreCase);
            if (modelMatch.Success && int.TryParse(modelMatch.Groups[1].Value, out var mId))
                modelId = mId;

            var versionMatch = Regex.Match(url, @"[?&]modelVersionId=(\d+)", RegexOptions.IgnoreCase);
            if (versionMatch.Success && int.TryParse(versionMatch.Groups[1].Value, out var vId))
                versionId = vId;

            if (modelId is null && versionId is null)
            {
                error = "Could not parse a Model ID from the URL.";
                return false;
            }

            return true;
        }

        var modelText = (ModelIdText ?? string.Empty).Trim();
        var versionText = (VersionIdText ?? string.Empty).Trim();

        if (!string.IsNullOrEmpty(modelText))
        {
            if (!int.TryParse(modelText, out var mId) || mId <= 0)
            {
                error = "Model ID must be a positive integer.";
                return false;
            }
            modelId = mId;
        }

        if (!string.IsNullOrEmpty(versionText))
        {
            if (!int.TryParse(versionText, out var vId) || vId <= 0)
            {
                error = "Model Version ID must be a positive integer.";
                return false;
            }
            versionId = vId;
        }

        if (modelId is null && versionId is null)
        {
            error = "Enter a Civitai URL, a Model ID, or a Model Version ID.";
            return false;
        }

        return true;
    }

    private async Task PopulatePreviewAsync(CivitaiModel model, CivitaiModelVersion? version)
    {
        this.FindControl<TextBlock>("PreviewName")!.Text = model.Name;
        this.FindControl<TextBlock>("PreviewType")!.Text = $"Type: {model.Type}";
        this.FindControl<TextBlock>("PreviewVersion")!.Text = version is not null
            ? $"Version: {version.Name}"
            : "Version: (no version selected)";
        this.FindControl<TextBlock>("PreviewBaseModel")!.Text = $"Base Model: {version?.BaseModel ?? "—"}";
        this.FindControl<TextBlock>("PreviewCreator")!.Text = $"Creator: {model.Creator?.Username ?? "Unknown"}";
        this.FindControl<TextBlock>("PreviewIds")!.Text = version is not null
            ? $"Model ID: {model.Id}    Version ID: {version.Id}"
            : $"Model ID: {model.Id}";

        var previewImage = this.FindControl<Image>("PreviewImage")!;
        var noPreview = this.FindControl<TextBlock>("NoPreviewText")!;
        previewImage.Source = null;

        var imageUrl = version?.Images?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Url))?.Url
                       ?? model.ModelVersions
                           .SelectMany(v => v.Images ?? Enumerable.Empty<CivitaiModelImage>())
                           .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Url))?.Url;

        if (string.IsNullOrEmpty(imageUrl))
        {
            noPreview.IsVisible = true;
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var data = await http.GetByteArrayAsync(imageUrl);
            using var ms = new MemoryStream(data);
            previewImage.Source = new Bitmap(ms);
            noPreview.IsVisible = false;
        }
        catch
        {
            noPreview.IsVisible = true;
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (ResolvedModel is null) return;
        IsConfirmed = true;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        Close(false);
    }
}
