using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class PromptEditView : UserControl
{
    public PromptEditView()
    {
        InitializeComponent();
        this.AttachedToVisualTree += OnAttached;
        var border = this.FindControl<Border>("ImageDropBorder");
        if (border != null)
        {
            border.AddHandler(DragDrop.DragEnterEvent, (_, e) => ViewModel?.OnDragEnter(e));
            border.AddHandler(DragDrop.DragLeaveEvent, (_, e) => ViewModel?.OnDragLeave(e));
            border.AddHandler(DragDrop.DragOverEvent, (_, e) => ViewModel?.OnDragOver(e));
            border.AddHandler(DragDrop.DropEvent, (_, e) => ViewModel?.OnDrop(e));
        }
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is PromptEditViewModel vm && VisualRoot is Window window)
        {
            vm.DialogService = new DialogService(window);
        }
    }

    private PromptEditViewModel? ViewModel => DataContext as PromptEditViewModel;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
