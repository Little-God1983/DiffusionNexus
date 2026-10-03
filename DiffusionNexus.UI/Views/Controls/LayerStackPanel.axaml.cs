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
/// <para>
/// A layer list always has a selected layer while it has layers, and the host decides when that changes.
/// The control therefore never writes null into <see cref="SelectedItem"/>: a ListBox drops its selection
/// on a Ctrl+click of the selected row, when its items are cleared and while the view is detached, and
/// none of those is the user choosing "no layer". Hosts keep plain setters.
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
        LayerList.SelectionChanged += OnListSelectionChanged;
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

        if (change.Property == SelectedItemProperty)
        {
            // The host's choice, including null once its last layer has gone, goes straight to the list.
            if (!ReferenceEquals(LayerList.SelectedItem, change.NewValue))
                LayerList.SelectedItem = change.NewValue;
        }
        else if (change.Property == ItemsProperty && change.NewValue is not null)
        {
            // While the host's list is away (its DataContext cleared on a tab switch) the ListBox is empty
            // and drops its selection. When the list comes back nothing would re-select the row.
            ReconcileLater();
        }
    }

    /// <summary>
    /// Relays the ListBox's selection to <see cref="SelectedItem"/>, except a null. When the ListBox
    /// drops a row that is still listed (a Ctrl+click or Ctrl+Space on the selected row) the row is put
    /// back on the next turn. When the row has left <see cref="Items"/> the host is removing it and picks
    /// the next selection itself.
    /// </summary>
    /// <remarks>
    /// A pick reaches the host on the next dispatcher turn, not from inside this handler. The host may
    /// answer a pick by rebuilding its rows (the editor does when the pick commits a pending
    /// Move/Transform), and changing the collection while the ListBox is inside its own selection change
    /// makes Avalonia throw; the binding swallows the exception and the rows stay half rebuilt.
    /// </remarks>
    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LayerList.SelectedItem is { } picked)
        {
            Dispatcher.UIThread.Post(() => RelayPick(picked));
            return;
        }

        // Posted: the ListBox is still inside its own selection change and would undo an immediate set.
        ReconcileLater();
    }

    private void RelayPick(object picked)
    {
        // A later pick, or a deselect, superseded this one.
        if (!ReferenceEquals(LayerList.SelectedItem, picked))
            return;

        SelectedItem = picked;

        // A host that answers with a different row does so inside this binding write-back, where Avalonia
        // ignores the source's change notification, so the panel would keep the picked row. Read the host's
        // value again now that the write-back is over.
        BindingOperations.GetBindingExpressionBase(this, SelectedItemProperty)?.UpdateTarget();
        Reconcile();
    }

    /// <summary>On the next dispatcher turn, <see cref="Reconcile"/>.</summary>
    private void ReconcileLater() => Dispatcher.UIThread.Post(Reconcile);

    /// <summary>
    /// Makes the list show <see cref="SelectedItem"/> again if the two have drifted apart, provided the item
    /// is still listed. The one place the list is put back in step.
    /// </summary>
    private void Reconcile()
    {
        var wanted = SelectedItem;
        if (ReferenceEquals(LayerList.SelectedItem, wanted))
            return;

        if (wanted is null || IsListed(wanted))
            LayerList.SelectedItem = wanted;
    }

    private bool IsListed(object item) => Items is { } items && items.Cast<object>().Contains(item);

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

    /// <summary>
    /// Clicking or tabbing elsewhere commits. Focus is left where the user put it: pulling it back to the
    /// list would send their typing to the layer list instead of the prompt they just clicked.
    /// </summary>
    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } editor)
            EndRename(editor, commit: true, refocusList: false);
    }

    /// <summary>
    /// The row's container was handed to another layer while a rename was open (the ListBox recycles
    /// containers when its items are rebuilt, as the editor's sync does). The typed text belongs to the
    /// old layer, so cancel rather than commit it onto the new one.
    /// </summary>
    private void OnRenameDataContextChanged(object? sender, EventArgs e)
    {
        // Hiding a focused TextBox does not move focus off it, so typing would vanish into the hidden box
        // (and the canvas leaves every key to a focused TextBox). Give the list the keyboard back then.
        if (sender is TextBox { IsVisible: true } editor)
            EndRename(editor, commit: false, refocusList: editor.IsKeyboardFocusWithin);
    }

    private void EndRename(TextBox editor, bool commit, bool refocusList = true)
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

        // Enter and Escape: the user is still in the panel, so keep the keyboard in the list.
        if (refocusList)
            LayerList.Focus();
    }
}
