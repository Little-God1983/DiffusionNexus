using System;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;
using TheArtOfDev.HtmlRenderer.Avalonia;
using TheArtOfDev.HtmlRenderer.Core.Entities;

namespace DiffusionNexus.UI.Views;

public partial class LoraDetailWindow : Window
{
    private NativeWebView? _descriptionWebView;
    private HtmlPanel? _descriptionHtmlPanel;
    private Border? _webViewUnavailableBanner;
    private ScrollViewer? _descriptionFallbackScroll;
    private TextBlock? _descriptionFallbackText;
    private TextBlock? _descriptionPlaceholder;
    private LoraDetailViewModel? _viewModel;
    private bool _webViewFailed;
    private bool _suppressNavigationHandling;

    public LoraDetailWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _descriptionWebView = this.FindControl<NativeWebView>("DescriptionWebView");
        _descriptionHtmlPanel = this.FindControl<HtmlPanel>("DescriptionHtmlPanel");
        _webViewUnavailableBanner = this.FindControl<Border>("WebViewUnavailableBanner");
        _descriptionFallbackScroll = this.FindControl<ScrollViewer>("DescriptionFallbackScroll");
        _descriptionFallbackText = this.FindControl<TextBlock>("DescriptionFallbackText");
        _descriptionPlaceholder = this.FindControl<TextBlock>("DescriptionPlaceholder");

        if (_descriptionWebView != null)
        {
            _descriptionWebView.NavigationStarted += OnWebViewNavigationStarted;
            _descriptionWebView.NavigationCompleted += OnWebViewNavigationCompleted;
            _descriptionWebView.NewWindowRequested += OnWebViewNewWindowRequested;
        }

        if (_descriptionHtmlPanel != null)
        {
            _descriptionHtmlPanel.LinkClicked += OnHtmlPanelLinkClicked;
        }

        UpdateDescriptionContent();
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        UpdateDescriptionContent();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as LoraDetailViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateDescriptionContent();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LoraDetailViewModel.DescriptionHtml) or nameof(LoraDetailViewModel.Description))
        {
            Dispatcher.UIThread.Post(UpdateDescriptionContent);
        }
    }

    private void UpdateDescriptionContent()
    {
        var vm = _viewModel ?? DataContext as LoraDetailViewModel;
        var sanitizedHtml = vm?.DescriptionHtml;
        var description = vm?.Description;
        var theme = ActualThemeVariant ?? ThemeVariant.Dark;

        if (!string.IsNullOrWhiteSpace(sanitizedHtml))
        {
            var document = HtmlDescriptionFormatter.BuildDocument(sanitizedHtml, theme);

            if (!_webViewFailed && _descriptionWebView != null)
            {
                try
                {
                    _suppressNavigationHandling = true;
                    _descriptionWebView.NavigateToString(document);
                    SetPresentation(webView: true, htmlPanel: false, plainText: false, placeholder: false, showBanner: false);
                    return;
                }
                catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
                {
                    _webViewFailed = true;
                }
                finally
                {
                    if (_webViewFailed)
                    {
                        _suppressNavigationHandling = false;
                    }
                }
            }

            if (_descriptionHtmlPanel != null)
            {
                _descriptionHtmlPanel.BaseStylesheet = HtmlDescriptionFormatter.GetStylesheet(theme);
                _descriptionHtmlPanel.Text = sanitizedHtml;
                SetPresentation(webView: false, htmlPanel: true, plainText: false, placeholder: false, showBanner: _webViewFailed);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            if (_descriptionFallbackText != null)
            {
                _descriptionFallbackText.Text = description;
            }

            SetPresentation(webView: false, htmlPanel: false, plainText: true, placeholder: false, showBanner: _webViewFailed && !string.IsNullOrWhiteSpace(sanitizedHtml));
        }
        else
        {
            SetPresentation(webView: false, htmlPanel: false, plainText: false, placeholder: true, showBanner: false);
        }
    }

    private void SetPresentation(bool webView, bool htmlPanel, bool plainText, bool placeholder, bool showBanner)
    {
        if (_descriptionWebView != null)
        {
            _descriptionWebView.IsVisible = webView;
        }

        if (_descriptionHtmlPanel != null)
        {
            _descriptionHtmlPanel.IsVisible = htmlPanel;
        }

        if (_descriptionFallbackScroll != null)
        {
            _descriptionFallbackScroll.IsVisible = plainText;
        }

        if (_descriptionPlaceholder != null)
        {
            _descriptionPlaceholder.IsVisible = placeholder;
        }

        if (_webViewUnavailableBanner != null)
        {
            _webViewUnavailableBanner.IsVisible = showBanner;
        }
    }

    private void OnWebViewNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (_suppressNavigationHandling)
        {
            return;
        }

        var request = e.Request;
        if (request == null)
        {
            return;
        }

        if (request.Scheme is "http" or "https")
        {
            e.Cancel = true;
            OpenExternalLink(request);
        }
    }

    private void OnWebViewNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        _suppressNavigationHandling = false;
    }

    private void OnWebViewNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        if (e.Request != null)
        {
            OpenExternalLink(e.Request);
        }

        e.Handled = true;
    }

    private void OnHtmlPanelLinkClicked(object? sender, HtmlRendererRoutedEventArgs<HtmlLinkClickedEventArgs> e)
    {
        var link = e.Event?.Link;
        if (!string.IsNullOrWhiteSpace(link) && Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            OpenExternalLink(uri);
            if (e.Event != null)
            {
                e.Event.Handled = true;
            }
        }
    }

    private static void OpenExternalLink(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_descriptionWebView != null)
        {
            _descriptionWebView.NavigationStarted -= OnWebViewNavigationStarted;
            _descriptionWebView.NavigationCompleted -= OnWebViewNavigationCompleted;
            _descriptionWebView.NewWindowRequested -= OnWebViewNewWindowRequested;
        }

        if (_descriptionHtmlPanel != null)
        {
            _descriptionHtmlPanel.LinkClicked -= OnHtmlPanelLinkClicked;
        }

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
            _viewModel = null;
        }
        else if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        DataContextChanged -= OnDataContextChanged;
        Closed -= OnClosed;
    }
}
