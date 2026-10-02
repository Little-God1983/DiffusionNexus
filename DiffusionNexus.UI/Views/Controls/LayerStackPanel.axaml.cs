using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// A layer list: each row has a show/hide toggle, a thumbnail, the name (double-click to rename), a lock
/// toggle and the opacity, under a ↑/↓/− toolbar. Shared by the Image Editor and the Diffusion Canvas.
/// </summary>
/// <remarks>
/// <para>
/// The host supplies <see cref="Items"/> already in display order (top layer first) and owns every
/// command. The control never reorders anything itself.
/// </para>
/// <para>
/// Rename is UI state local to this control. Only the committed name reaches the row, through
/// <see cref="LayerStackNaming.Resolve"/>, so a blank name never lands.
/// </para>
/// </remarks>
public partial class LayerStackPanel : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<LayerStackPanel, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<LayerStackPanel, object?>(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<ICommand?> MoveUpCommandProperty =
        AvaloniaProperty.Register<LayerStackPanel, ICommand?>(nameof(MoveUpCommand));

    public static readonly StyledProperty<ICommand?> MoveDownCommandProperty =
        AvaloniaProperty.Register<LayerStackPanel, ICommand?>(nameof(MoveDownCommand));

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<LayerStackPanel, ICommand?>(nameof(DeleteCommand));

    public static readonly StyledProperty<object?> LeadingToolsProperty =
        AvaloniaProperty.Register<LayerStackPanel, object?>(nameof(LeadingTools));

    public static readonly StyledProperty<string> LockToolTipProperty =
        AvaloniaProperty.Register<LayerStackPanel, string>(nameof(LockToolTip),
            defaultValue: "Lock the layer so it cannot be deleted");

    public static readonly StyledProperty<double> ListMaxHeightProperty =
        AvaloniaProperty.Register<LayerStackPanel, double>(nameof(ListMaxHeight), defaultValue: double.PositiveInfinity);

    public LayerStackPanel()
    {
        InitializeComponent();

        // Tunnel, so Enter and Escape reach us even if the TextBox would mark them handled.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Rows implementing <see cref="ILayerStackItem"/>, top layer first.</summary>
    public IEnumerable? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    /// <summary>The selected row. Two-way by default.</summary>
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }

    /// <summary>Moves the selected layer toward the top. Owned by the host.</summary>
    public ICommand? MoveUpCommand { get => GetValue(MoveUpCommandProperty); set => SetValue(MoveUpCommandProperty, value); }

    /// <summary>Moves the selected layer toward the bottom. Owned by the host.</summary>
    public ICommand? MoveDownCommand { get => GetValue(MoveDownCommandProperty); set => SetValue(MoveDownCommandProperty, value); }

    /// <summary>Deletes the selected layer. Owned by the host, which refuses a locked layer.</summary>
    public ICommand? DeleteCommand { get => GetValue(DeleteCommandProperty); set => SetValue(DeleteCommandProperty, value); }

    /// <summary>Host-specific buttons shown before ↑/↓/− (the editor's Add and Duplicate).</summary>
    public object? LeadingTools { get => GetValue(LeadingToolsProperty); set => SetValue(LeadingToolsProperty, value); }

    /// <summary>The lock toggle's tooltip: what lock means on this screen.</summary>
    public string LockToolTip { get => GetValue(LockToolTipProperty); set => SetValue(LockToolTipProperty, value); }

    /// <summary>Caps the list's height; unbounded by default so a docked panel can fill its column.</summary>
    public double ListMaxHeight { get => GetValue(ListMaxHeightProperty); set => SetValue(ListMaxHeightProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // While the host's list is away (its view detached on navigation, or its DataContext cleared on a
        // tab switch) the ListBox is empty and drops its selection, and the host's view model rightly
        // refuses that null. When the list comes back nothing would re-select the row, so put the host's
        // selection back once the ListBox has its items again.
        if (change.Property == ItemsProperty && change.NewValue is not null && SelectedItem is { } selected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(SelectedItem, selected))
                    LayerList.SelectedItem = selected;
            });
        }
    }

    private void OnNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Panel cell || cell.DataContext is not ILayerStackItem item)
            return;

        var label = cell.Children.OfType<TextBlock>().FirstOrDefault();
        var editor = cell.Children.OfType<TextBox>().FirstOrDefault();
        if (label is null || editor is null || editor.IsVisible)
            return;

        editor.Text = item.Name;
        label.IsVisible = false;
        editor.IsVisible = true;
        editor.Focus();
        editor.SelectAll();
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { IsVisible: true } editor || !editor.Classes.Contains("layerRename"))
            return;

        if (e.Key == Key.Enter)
        {
            EndRename(editor, commit: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndRename(editor, commit: false);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } editor)
            EndRename(editor, commit: true);
    }

    private void EndRename(TextBox editor, bool commit)
    {
        if (!editor.IsVisible)
            return;

        // Hide first: moving focus away below raises LostFocus, which must find nothing left to commit.
        editor.IsVisible = false;
        if (editor.Parent is Panel cell)
        {
            foreach (var label in cell.Children.OfType<TextBlock>())
                label.IsVisible = true;
        }

        if (commit && editor.DataContext is ILayerStackItem item)
        {
            var name = LayerStackNaming.Resolve(editor.Text, item.Name);
            if (!string.Equals(name, item.Name, StringComparison.Ordinal))
                item.Name = name;
        }

        LayerList.Focus();
    }
}
