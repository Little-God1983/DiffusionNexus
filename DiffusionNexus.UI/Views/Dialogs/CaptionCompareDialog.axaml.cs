using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog that shows current and newly generated captions side by side,
/// letting the user choose which one to keep.
/// </summary>
public partial class CaptionCompareDialog : Window
{
    private CaptionCompareDialogViewModel? _viewModel;

    public CaptionCompareDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets the dialog result after it closes.
    /// </summary>
    public CaptionCompareResult? Result => _viewModel?.Result;

    /// <summary>
    /// Initializes the dialog with image path and caption texts.
    /// </summary>
    public CaptionCompareDialog WithData(string imagePath, string currentCaption, string newCaption)
    {
        _viewModel = new CaptionCompareDialogViewModel();
        _viewModel.Initialize(imagePath, currentCaption, newCaption);
        _viewModel.CloseRequested += (_, _) => Close();
        DataContext = _viewModel;
        return this;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _viewModel?.Dispose();
    }
}
