using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Classes;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace DiffusionNexus.UI.Views;

public partial class LoraHelperView : UserControl
{
    private ScrollViewer? _scroll;
    private const int MaxActivePreviewCount = 30;
    private readonly LinkedList<LoraCardViewModel> _activePreviewCards = new();
    private LoraHelperViewModel? _viewModel;

    public LoraHelperView()
    {
        InitializeComponent();
        this.AttachedToVisualTree += OnAttached;
        this.DetachedFromVisualTree += OnDetached;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnSuggestionChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ViewModels.LoraHelperViewModel vm && sender is ComboBox cb && cb.SelectedItem is string text)
        {
            vm.ApplySuggestion(text);
            cb.IsDropDownOpen = false;
            cb.SelectedIndex = -1;
        }
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is LoraHelperViewModel vm && VisualRoot is Window window)
        {
            if (!ReferenceEquals(_viewModel, vm))
            {
                if (_viewModel != null)
                {
                    _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                }

                _viewModel = vm;
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }

            vm.DialogService = new DialogService(window);
            vm.SetWindow(window);
        }

        _scroll = this.FindControl<ScrollViewer>("CardScrollViewer");
        if (_scroll != null)
            _scroll.ScrollChanged += OnScrollChanged;
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_scroll != null)
        {
            _scroll.ScrollChanged -= OnScrollChanged;
            _scroll = null;
        }

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        foreach (var card in _activePreviewCards)
        {
            card.DisposeVideoPreview();
        }

        _activePreviewCards.Clear();
    }

    private async void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll == null)
            return;

        if (DataContext is LoraHelperViewModel vm)
        {
            if (_scroll.Offset.Y + _scroll.Viewport.Height > _scroll.Extent.Height - 300)
            {
                await vm.LoadNextPageAsync();
            }
        }
    }

    private void OnCardElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element?.DataContext is not LoraCardViewModel card)
            return;

        if (_viewModel == null && DataContext is LoraHelperViewModel vm)
        {
            _viewModel = vm;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (_viewModel == null)
            return;

        _activePreviewCards.Remove(card);
        _activePreviewCards.AddLast(card);
        UpdateActivePreviewState();
    }

    private void OnCardElementClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
    {
        if (e.Element?.DataContext is not LoraCardViewModel card)
            return;

        _activePreviewCards.Remove(card);
        card.DisposeVideoPreview();
        UpdateActivePreviewState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoraHelperViewModel.IsVideoPreviewEnabled))
        {
            UpdateActivePreviewState();
        }
    }

    private void UpdateActivePreviewState()
    {
        if (_viewModel == null)
            return;

        var enableStartIndex = Math.Max(0, _activePreviewCards.Count - MaxActivePreviewCount);
        var index = 0;
        foreach (var card in _activePreviewCards)
        {
            var shouldEnable = _viewModel.IsVideoPreviewEnabled && index >= enableStartIndex;
            card.ApplyVideoPreviewSetting(shouldEnable);
            index++;
        }
    }

    private async void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;

        if (sender is not Border border)
            return;

        if (e.Source is Button || e.Source is ToggleButton)
            return;

        if (border.DataContext is not LoraCardViewModel card)
            return;

        if (card.OpenDetailsCommand is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }
        else
        {
            card.OpenDetailsCommand.Execute(null);
        }

        e.Handled = true;
    }
}
