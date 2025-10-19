using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using DiffusionNexus.UI.Utilities;
using TheArtOfDev.HtmlRenderer.Avalonia;
using TheArtOfDev.HtmlRenderer.Core.Entities;
using SystemUri = System.Uri;

namespace DiffusionNexus.UI.Views.Controls;

public partial class HtmlDescriptionView : UserControl
{
    public static readonly StyledProperty<string?> HtmlProperty =
        AvaloniaProperty.Register<HtmlDescriptionView, string?>(nameof(Html));

    public static readonly StyledProperty<string?> PlainTextProperty =
        AvaloniaProperty.Register<HtmlDescriptionView, string?>(nameof(PlainText));

    public static readonly StyledProperty<bool> EnableWebViewProperty =
        AvaloniaProperty.Register<HtmlDescriptionView, bool>(nameof(EnableWebView), true);

    private NativeWebView? _webView;
    private ScrollViewer? _htmlScroll;
    private HtmlPanel? _htmlPanel;
    private ScrollViewer? _plainTextScroll;
    private TextBlock? _plainTextBlock;
    private TextBlock? _placeholder;
    private Border? _fallbackBanner;
    private bool _webViewFailed;
    private bool _suppressNavigation;

    public HtmlDescriptionView()
    {
        InitializeComponent();
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        Loaded += OnLoaded;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public string? Html
    {
        get => GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    public string? PlainText
    {
        get => GetValue(PlainTextProperty);
        set => SetValue(PlainTextProperty, value);
    }

    public bool EnableWebView
    {
        get => GetValue(EnableWebViewProperty);
        set => SetValue(EnableWebViewProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == EnableWebViewProperty && change.GetNewValue<bool>())
        {
            _webViewFailed = false;
        }

        if (change.Property == HtmlProperty ||
            change.Property == PlainTextProperty ||
            change.Property == EnableWebViewProperty)
        {
            UpdatePresentation();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        _webView = this.FindControl<NativeWebView>("WebView");
        _htmlScroll = this.FindControl<ScrollViewer>("HtmlScroll");
        _htmlPanel = this.FindControl<HtmlPanel>("HtmlPanel");
        _plainTextScroll = this.FindControl<ScrollViewer>("PlainTextScroll");
        _plainTextBlock = this.FindControl<TextBlock>("PlainTextBlock");
        _placeholder = this.FindControl<TextBlock>("Placeholder");
        _fallbackBanner = this.FindControl<Border>("FallbackBanner");

        if (_webView != null)
        {
            _webView.NavigationStarted += OnWebViewNavigationStarted;
            _webView.NavigationCompleted += OnWebViewNavigationCompleted;
            _webView.NewWindowRequested += OnWebViewNewWindowRequested;
        }

        if (_htmlPanel != null)
        {
            _htmlPanel.LinkClicked += OnHtmlPanelLinkClicked;
        }

        UpdatePresentation();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_webView != null)
        {
            _webView.NavigationStarted -= OnWebViewNavigationStarted;
            _webView.NavigationCompleted -= OnWebViewNavigationCompleted;
            _webView.NewWindowRequested -= OnWebViewNewWindowRequested;
        }

        if (_htmlPanel != null)
        {
            _htmlPanel.LinkClicked -= OnHtmlPanelLinkClicked;
        }

        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => UpdatePresentation();

    private void UpdatePresentation()
    {
        if (_webView == null &&
            _htmlPanel == null &&
            _htmlScroll == null &&
            _plainTextScroll == null &&
            _placeholder == null &&
            _fallbackBanner == null)
        {
            return;
        }

        var html = Html;
        var plainText = PlainText;
        var theme = ActualThemeVariant ?? ThemeVariant.Dark;

        if (!string.IsNullOrWhiteSpace(html))
        {
            var document = HtmlDescriptionFormatter.BuildDocument(html, theme);

            if (_webView != null && EnableWebView && !_webViewFailed)
            {
                try
                {
                    _suppressNavigation = true;
                    _webView.NavigateToString(document);
                    SetMode(HtmlDescriptionMode.WebView);
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
                        _suppressNavigation = false;
                    }
                }
            }

            if (_htmlPanel != null && _htmlScroll != null)
            {
                _htmlPanel.BaseStylesheet = HtmlDescriptionFormatter.GetStylesheet(theme);
                _htmlPanel.Text = html;
                SetMode(HtmlDescriptionMode.HtmlPanel);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(plainText))
        {
            if (_plainTextBlock != null)
            {
                _plainTextBlock.Text = plainText;
            }

            SetMode(HtmlDescriptionMode.PlainText);
        }
        else
        {
            SetMode(HtmlDescriptionMode.Placeholder);
        }
    }

    private void SetMode(HtmlDescriptionMode mode)
    {
        if (_webView != null)
        {
            _webView.IsVisible = mode == HtmlDescriptionMode.WebView;
        }

        if (_htmlScroll != null)
        {
            _htmlScroll.IsVisible = mode == HtmlDescriptionMode.HtmlPanel;
        }

        if (_plainTextScroll != null)
        {
            _plainTextScroll.IsVisible = mode == HtmlDescriptionMode.PlainText;
        }

        if (_placeholder != null)
        {
            _placeholder.IsVisible = mode == HtmlDescriptionMode.Placeholder;
        }

        if (_fallbackBanner != null)
        {
            var shouldShowBanner = EnableWebView && _webViewFailed && !string.IsNullOrWhiteSpace(Html);
            _fallbackBanner.IsVisible = shouldShowBanner;
        }
    }

    private void OnWebViewNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (_suppressNavigation)
        {
            return;
        }

        var requestUri = e.Request?.ToString();
        if (requestUri != null &&
            SystemUri.TryCreate(requestUri, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            e.Cancel = true;
            OpenExternalLink(uri);
        }
    }

    private void OnWebViewNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        _suppressNavigation = false;
    }

    private void OnWebViewNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        var uriString = e.Request?.ToString();
        if (uriString != null && SystemUri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            OpenExternalLink(uri);
        }

        e.Handled = true;
    }

    private void OnHtmlPanelLinkClicked(object? sender, HtmlRendererRoutedEventArgs<HtmlLinkClickedEventArgs> e)
    {
        if (e.Event?.Link is { Length: > 0 } link && SystemUri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            OpenExternalLink(uri);
            e.Event.Handled = true;
        }
    }

    private static void OpenExternalLink(SystemUri uri)
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

    private enum HtmlDescriptionMode
    {
        WebView,
        HtmlPanel,
        PlainText,
        Placeholder
    }
}
