using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// View for the LoRA Dataset Helper module.
/// </summary>
public partial class LoraDatasetHelperView : UserControl
{
    private ImageEditorControl? _imageEditorCanvas;

    public LoraDatasetHelperView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _imageEditorCanvas = this.FindControl<ImageEditorControl>("ImageEditorCanvas");
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Inject DialogService into the ViewModel
        if (VisualRoot is Window window && DataContext is IDialogServiceAware aware)
        {
            aware.DialogService = new DialogService(window);
        }

        // Load datasets on first attach
        if (DataContext is LoraDatasetHelperViewModel vm)
        {
            vm.CheckStorageConfigurationCommand.Execute(null);

            // Wire up image editor events
            WireUpImageEditorEvents(vm);
        }
    }

    private void WireUpImageEditorEvents(LoraDatasetHelperViewModel vm)
    {
        if (_imageEditorCanvas is null) return;

        // Update dimensions when image changes
        _imageEditorCanvas.ImageChanged += (_, _) =>
        {
            vm.ImageEditor.UpdateDimensions(
                _imageEditorCanvas.ImageWidth,
                _imageEditorCanvas.ImageHeight);
        };

        // Handle clear/reset requests from ViewModel
        vm.ImageEditor.ClearRequested += (_, _) =>
        {
            _imageEditorCanvas.ClearImage();
        };

        vm.ImageEditor.ResetRequested += (_, _) =>
        {
            _imageEditorCanvas.ResetToOriginal();
        };
    }
}
