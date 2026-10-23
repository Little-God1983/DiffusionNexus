using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// One file line of the LoRA Sorter's duplicate list and run report (#651), with
/// "Open in folder" for the file and for the path it is paired with.
/// </summary>
public partial class SortReportFileRow : UserControl
{
    public SortReportFileRow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
