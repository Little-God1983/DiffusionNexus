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
    private HtmlPanel? _descriptionHtmlPanel;
    private ScrollViewer? _descriptionFallbackScroll;
    private TextBlock? _descriptionFallbackText;
    private TextBlock? _descriptionPlaceholder;
    private LoraDetailViewModel? _viewModel;

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

        _descriptionHtmlPanel = this.FindControl<HtmlPanel>("DescriptionHtmlPanel");
        _descriptionFallbackScroll = this.FindControl<ScrollViewer>("DescriptionFallbackScroll");
        _descriptionFallbackText = this.FindControl<TextBlock>("DescriptionFallbackText");
        _descriptionPlaceholder = this.FindControl<TextBlock>("DescriptionPlaceholder");

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
            if (_descriptionHtmlPanel != null)
            {
                _descriptionHtmlPanel.BaseStylesheet = HtmlDescriptionFormatter.GetStylesheet(theme);
                _descriptionHtmlPanel.Text = HtmlDescriptionFormatter.WrapContent(sanitizedHtml);
                SetPresentation(htmlPanel: true, plainText: false, placeholder: false);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(description) && sanitizedHtml != null)
        {
            if (_descriptionFallbackText != null)
            {
                _descriptionFallbackText.Text = description;
            }

            SetPresentation(htmlPanel: false, plainText: true, placeholder: false);
        }
        else
        {
            SetPresentation(htmlPanel: false, plainText: false, placeholder: true);
        }
    }

    private void SetPresentation(bool htmlPanel, bool plainText, bool placeholder)
    {
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
        if (_descriptionHtmlPanel != null)
        {
            _descriptionHtmlPanel.LinkClicked -= OnHtmlPanelLinkClicked;
        }

        CleanupViewModel();

        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        DataContextChanged -= OnDataContextChanged;
        Closed -= OnClosed;
    }

    private void CleanupViewModel()
    {
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
    }
}
