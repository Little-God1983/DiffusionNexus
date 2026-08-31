using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
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
            nameof(PlaceholderText), "Select base model…");

    /// <summary>
    /// Per-instance engine holding the search/narrowing/selection state. Exposed for the
    /// control's own XAML; consumers bind the styled properties instead.
    /// </summary>
    public SearchableBaseModelPickerViewModel Engine { get; } = new();

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
        PickerButton.DataContext = Engine;

        Engine.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchableBaseModelPickerViewModel.SelectedItem))
            {
                // SetCurrentValue keeps the two-way binding to the consumer VM intact.
                SetCurrentValue(SelectedItemProperty, Engine.SelectedItem);
            }
        };
        Engine.CloseRequested += (_, _) => PickerButton.Flyout?.Hide();

        if (PickerButton.Flyout is { } flyout)
        {
            flyout.Opened += (_, _) =>
            {
                Engine.OnFlyoutOpened();
                // Focus lands after the popup finishes opening.
                Dispatcher.UIThread.Post(() => SearchBox?.Focus());
            };
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsSourceProperty)
        {
            Engine.ItemsSource = change.GetNewValue<IEnumerable<string>?>();
        }
        else if (change.Property == SelectedItemProperty)
        {
            Engine.SelectedItem = change.GetNewValue<string?>();
        }
        else if (change.Property == PlaceholderTextProperty)
        {
            Engine.PlaceholderText = change.GetNewValue<string>();
        }
    }
}
