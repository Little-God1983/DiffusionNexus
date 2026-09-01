using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Single-select base-model dropdown with a search box, replicating the flyout UX of the
/// LoRA Viewer / Civitai Browser base-model filters (which stay untouched — they are
/// multi-select filters, this is a picker). Bind <see cref="ItemsSource"/> to the label
/// list a view model already exposes and <see cref="SelectedItem"/> two-way to its
/// selected value; the search state lives per instance inside the control.
/// </summary>
public partial class SearchableBaseModelPicker : UserControl
{
    /// <summary>
    /// Defines the <see cref="ItemsSource"/> property.
    /// </summary>
    public static readonly StyledProperty<IEnumerable<string>?> ItemsSourceProperty =
        AvaloniaProperty.Register<SearchableBaseModelPicker, IEnumerable<string>?>(nameof(ItemsSource));

    /// <summary>
    /// Defines the <see cref="SelectedItem"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> SelectedItemProperty =
        AvaloniaProperty.Register<SearchableBaseModelPicker, string?>(
            nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Defines the <see cref="PlaceholderText"/> property.
    /// </summary>
    public static readonly StyledProperty<string> PlaceholderTextProperty =
        AvaloniaProperty.Register<SearchableBaseModelPicker, string>(
            nameof(PlaceholderText), SearchableBaseModelPickerViewModel.DefaultPlaceholder);

    // Per-instance engine holding the search/narrowing/selection state. It becomes the
    // DataContext of the button subtree in the ctor; consumers bind the styled
    // properties, never the engine.
    private readonly SearchableBaseModelPickerViewModel _engine = new();

    /// <summary>The full label list offered by the picker.</summary>
    public IEnumerable<string>? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>The picked label; <c>null</c> while nothing is selected.</summary>
    public string? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>Shown on the button while nothing is selected.</summary>
    public string PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public SearchableBaseModelPicker()
    {
        InitializeComponent();

        // The engine becomes the DataContext of the button subtree only — the
        // UserControl's own DataContext stays inherited so call-site bindings
        // ({Binding AvailableBaseModels} etc.) still resolve against the page VM.
        PickerButton.DataContext = _engine;

        _engine.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchableBaseModelPickerViewModel.SelectedItem))
            {
                // SetCurrentValue keeps the two-way binding to the consumer VM intact.
                SetCurrentValue(SelectedItemProperty, _engine.SelectedItem);
            }
        };
        _engine.CloseRequested += (_, _) => PickerButton.Flyout?.Hide();

        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _engine.TryCommitSingleMatch())
            {
                e.Handled = true;
            }
        };

        if (PickerButton.Flyout is { } flyout)
        {
            flyout.Opened += (_, _) =>
            {
                _engine.OnFlyoutOpened();
                // The popup is content-sized; opening at least as wide as the button
                // matches how the replaced ComboBox dropdown behaved.
                FlyoutRoot.MinWidth = Math.Max(240, PickerButton.Bounds.Width);
                // Focus and scroll land after the popup finishes opening and laying out.
                Dispatcher.UIThread.Post(() =>
                {
                    SearchBox?.Focus();
                    ScrollSelectionIntoView();
                });
            };
        }
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        _engine.ItemsSource = ItemsSource;
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromLogicalTree(e);
        // Drop the engine's CollectionChanged subscription on the source so a discarded
        // picker never keeps its subtree alive (or rebuilding) through a long-lived
        // collection. Reattach restores it above.
        _engine.ItemsSource = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsSourceProperty)
        {
            _engine.ItemsSource = change.GetNewValue<IEnumerable<string>?>();
        }
        else if (change.Property == SelectedItemProperty)
        {
            _engine.SelectedItem = change.GetNewValue<string?>();
        }
        else if (change.Property == PlaceholderTextProperty)
        {
            _engine.PlaceholderText = change.GetNewValue<string>();
        }
    }

    private void ScrollSelectionIntoView()
    {
        var items = _engine.VisibleItems;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].IsSelected)
            {
                ItemsList.ContainerFromIndex(i)?.BringIntoView();
                return;
            }
        }
    }
}
