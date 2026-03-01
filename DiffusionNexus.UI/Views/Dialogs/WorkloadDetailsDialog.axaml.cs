using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Detail dialog showing which models and custom nodes are installed or missing.
/// </summary>
public partial class WorkloadDetailsDialog : Window
{
    public WorkloadDetailsDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// The detail items to display in the grid.
    /// Set before calling ShowDialog.
    /// </summary>
    public ObservableCollection<WorkloadDetailItemViewModel> DetailItems
    {
        get => _detailItems;
        set
        {
            _detailItems = value;
            var grid = this.FindControl<DataGrid>("DetailsGrid");
            if (grid is not null)
            {
                grid.ItemsSource = value;
            }
        }
    }
    private ObservableCollection<WorkloadDetailItemViewModel> _detailItems = [];

    /// <summary>
    /// Summary text shown below the title.
    /// </summary>
    public string Summary
    {
        get => _summary;
        set
        {
            _summary = value;
            var text = this.FindControl<TextBlock>("SummaryText");
            if (text is not null)
            {
                text.Text = value;
            }
        }
    }
    private string _summary = string.Empty;

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
