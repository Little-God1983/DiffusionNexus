using System;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class LoraDetailWindow : Window
{
    private readonly NativeWebView? _descriptionWebView;
    private readonly ScrollViewer? _fallbackScrollViewer;
    private readonly TextBlock? _fallbackTextBlock;
    private readonly TextBlock? _runtimeNotice;
    private LoraDetailViewModel? _currentViewModel;

    public LoraDetailWindow()
    {
        InitializeComponent();
        _descriptionWebView = this.FindControl<NativeWebView>("DescriptionWebView");
        _fallbackScrollViewer = this.FindControl<ScrollViewer>("DescriptionFallbackScroll");
        _fallbackTextBlock = this.FindControl<TextBlock>("DescriptionFallback");
        _runtimeNotice = this.FindControl<TextBlock>("WebViewFallbackNotice");

        if (_descriptionWebView != null)
        {
            _descriptionWebView.NavigationStarted += OnNavigationStarting;
            _descriptionWebView.NewWindowRequested += OnNewWindowRequested;
        }

        PropertyChanged += OnWindowPropertyChanged;
        UpdateDescriptionHtml();
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_descriptionWebView != null)
        {
            _descriptionWebView.NavigationStarted -= OnNavigationStarting;
            _descriptionWebView.NewWindowRequested -= OnNewWindowRequested;
        }

        if (_currentViewModel != null)
        {
            _currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _currentViewModel = null;
        }

        PropertyChanged -= OnWindowPropertyChanged;

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        Closed -= OnClosed;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_currentViewModel != null)
        {
            _currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _currentViewModel = DataContext as LoraDetailViewModel;

        if (_currentViewModel != null)
        {
            _currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        base.OnDataContextChanged(e);
        UpdateDescriptionHtml();
    }

    private void UpdateDescriptionHtml()
    {
        if (!IsInitialized)
        {
            return;
        }

        if (DataContext is not LoraDetailViewModel viewModel)
        {
            ShowFallback("No description provided.", false);
            return;
        }

        var theme = ActualThemeVariant ?? RequestedThemeVariant ?? ThemeVariant.Dark;
        var isDark = theme == ThemeVariant.Dark;

        if (_descriptionWebView == null)
        {
            ShowFallback(viewModel.Description, true);
            return;
        }

        try
        {
            var html = viewModel.GetDescriptionDocument(isDark);
            _descriptionWebView.NavigateToString(html);
            _descriptionWebView.IsVisible = true;

            if (_fallbackScrollViewer != null)
            {
                _fallbackScrollViewer.IsVisible = false;
            }

            if (_runtimeNotice != null)
            {
                _runtimeNotice.IsVisible = false;
            }
        }
        catch
        {
            ShowFallback(viewModel.Description, true);
        }
    }

    private void ShowFallback(string text, bool showNotice)
    {
        if (_descriptionWebView != null)
        {
            _descriptionWebView.IsVisible = false;
        }

        if (_fallbackScrollViewer != null)
        {
            _fallbackScrollViewer.IsVisible = true;
        }

        if (_fallbackTextBlock != null)
        {
            _fallbackTextBlock.Text = text;
        }

        if (_runtimeNotice != null)
        {
            _runtimeNotice.IsVisible = showNotice;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LoraDetailViewModel.DescriptionHtml) or nameof(LoraDetailViewModel.Description))
        {
            UpdateDescriptionHtml();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ActualThemeVariantProperty)
        {
            UpdateDescriptionHtml();
        }
    }

    private void OnNavigationStarting(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request == null)
        {
            return;
        }

        if (e.Request.Scheme is "http" or "https")
        {
            e.Cancel = true;
            OpenExternalLink(e.Request);
        }
        else if (!string.Equals(e.Request.Scheme, "about", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        if (e.Request != null)
        {
            OpenExternalLink(e.Request);
        }

        e.Handled = true;
    }

    private static void OpenExternalLink(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignored
        }
    }
}
