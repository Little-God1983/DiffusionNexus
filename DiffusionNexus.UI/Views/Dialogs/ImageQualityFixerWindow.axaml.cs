using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Dialogs;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog showing every analyzed image as one row with per-metric scores, rating chip,
/// and quick fix actions (Mark Ready/Trash, Replace, Edit in Editor, Show in Explorer).
/// Mirrors the <see cref="ColorFixerWindow"/> / <see cref="DuplicateFixerWindow"/> pattern.
/// </summary>
public partial class ImageQualityFixerWindow : Window
{
    public ImageQualityFixerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Initializes the window with a pre-configured ViewModel and dialog service.
    /// The host is responsible for wiring <see cref="ImageQualityFixerViewModel.RequestReplace"/>
    /// and <see cref="ImageQualityFixerViewModel.RequestEditInImageEditor"/> before showing.
    /// </summary>
    public ImageQualityFixerWindow(ImageQualityFixerViewModel viewModel, IDialogService dialogService)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        viewModel.DialogService = dialogService;
        DataContext = viewModel;

        // Closing the dialog from "Edit in Image Editor" must propagate up the chain.
        var hostHandler = viewModel.RequestEditInImageEditor;
        viewModel.RequestEditInImageEditor = item =>
        {
            hostHandler?.Invoke(item);
            Close();
        };
    }

    private ImageQualityFixerViewModel? ViewModel => DataContext as ImageQualityFixerViewModel;

    private void OnFilterAll_Click(object? sender, RoutedEventArgs e)
        => SetFilter(ImageQualityRatingFilter.All);

    private void OnFilterUnrated_Click(object? sender, RoutedEventArgs e)
        => SetFilter(ImageQualityRatingFilter.Unrated);

    private void OnFilterApproved_Click(object? sender, RoutedEventArgs e)
        => SetFilter(ImageQualityRatingFilter.Approved);

    private void OnFilterTrash_Click(object? sender, RoutedEventArgs e)
        => SetFilter(ImageQualityRatingFilter.Trash);

    private void SetFilter(ImageQualityRatingFilter filter)
    {
        if (ViewModel is { } vm)
            vm.RatingFilter = filter;
    }

    private void OnSortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || sender is not ComboBox combo)
            return;

        ViewModel.SortMode = combo.SelectedIndex switch
        {
            1 => ImageQualitySortMode.OverallScoreDesc,
            2 => ImageQualitySortMode.FileName,
            _ => ImageQualitySortMode.OverallScoreAsc
        };
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
