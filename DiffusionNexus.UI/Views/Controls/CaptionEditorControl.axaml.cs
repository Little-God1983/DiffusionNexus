using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Reusable caption editor control with SpellCheckTextBox and Undo/Redo/Reset/Save buttons.
/// Supports both horizontal (buttons below text) and vertical (buttons beside text) layouts
/// via the <see cref="IsVerticalLayout"/> property.
/// </summary>
public partial class CaptionEditorControl : UserControl
{
    /// <summary>
    /// Defines the <see cref="Text"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CaptionEditorControl, string?>(
            nameof(Text),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Defines the <see cref="HasUnsavedChanges"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> HasUnsavedChangesProperty =
        AvaloniaProperty.Register<CaptionEditorControl, bool>(nameof(HasUnsavedChanges));

    /// <summary>
    /// Defines the <see cref="UndoCommand"/> property.
    /// </summary>
    public static readonly StyledProperty<ICommand?> UndoCommandProperty =
        AvaloniaProperty.Register<CaptionEditorControl, ICommand?>(nameof(UndoCommand));

    /// <summary>
    /// Defines the <see cref="RedoCommand"/> property.
    /// </summary>
    public static readonly StyledProperty<ICommand?> RedoCommandProperty =
        AvaloniaProperty.Register<CaptionEditorControl, ICommand?>(nameof(RedoCommand));

    /// <summary>
    /// Defines the <see cref="ResetCommand"/> property.
    /// </summary>
    public static readonly StyledProperty<ICommand?> ResetCommandProperty =
        AvaloniaProperty.Register<CaptionEditorControl, ICommand?>(nameof(ResetCommand));

    /// <summary>
    /// Defines the <see cref="SaveCommand"/> property.
    /// </summary>
    public static readonly StyledProperty<ICommand?> SaveCommandProperty =
        AvaloniaProperty.Register<CaptionEditorControl, ICommand?>(nameof(SaveCommand));

    /// <summary>
    /// Defines the <see cref="Watermark"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<CaptionEditorControl, string?>(nameof(Watermark), "Enter caption...");

    /// <summary>
    /// Defines the <see cref="TextMinHeight"/> property.
    /// </summary>
    public static readonly StyledProperty<double> TextMinHeightProperty =
        AvaloniaProperty.Register<CaptionEditorControl, double>(nameof(TextMinHeight), 60);

    /// <summary>
    /// Defines the <see cref="TextMaxHeight"/> property.
    /// </summary>
    public static readonly StyledProperty<double> TextMaxHeightProperty =
        AvaloniaProperty.Register<CaptionEditorControl, double>(nameof(TextMaxHeight), 120);

    /// <summary>
    /// Defines the <see cref="TextFontSize"/> property.
    /// </summary>
    public static readonly StyledProperty<double> TextFontSizeProperty =
        AvaloniaProperty.Register<CaptionEditorControl, double>(nameof(TextFontSize), 13);

    /// <summary>
    /// Defines the <see cref="IsVerticalLayout"/> property.
    /// When true, buttons are stacked vertically to the right of the text box.
    /// When false (default), buttons appear in a horizontal row below the text box.
    /// </summary>
    public static readonly StyledProperty<bool> IsVerticalLayoutProperty =
        AvaloniaProperty.Register<CaptionEditorControl, bool>(nameof(IsVerticalLayout));

    /// <summary>
    /// Gets or sets the caption text. Two-way bindable.
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the caption has unsaved modifications.
    /// Controls button visibility and the "Modified" indicator.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get => GetValue(HasUnsavedChangesProperty);
        set => SetValue(HasUnsavedChangesProperty, value);
    }

    /// <summary>
    /// Gets or sets the command to undo the last caption edit.
    /// </summary>
    public ICommand? UndoCommand
    {
        get => GetValue(UndoCommandProperty);
        set => SetValue(UndoCommandProperty, value);
    }

    /// <summary>
    /// Gets or sets the command to redo a previously undone edit.
    /// </summary>
    public ICommand? RedoCommand
    {
        get => GetValue(RedoCommandProperty);
        set => SetValue(RedoCommandProperty, value);
    }

    /// <summary>
    /// Gets or sets the command to reset the caption to its original value.
    /// </summary>
    public ICommand? ResetCommand
    {
        get => GetValue(ResetCommandProperty);
        set => SetValue(ResetCommandProperty, value);
    }

    /// <summary>
    /// Gets or sets the command to save the caption to disk.
    /// </summary>
    public ICommand? SaveCommand
    {
        get => GetValue(SaveCommandProperty);
        set => SetValue(SaveCommandProperty, value);
    }

    /// <summary>
    /// Gets or sets the watermark text displayed when the text box is empty.
    /// </summary>
    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <summary>
    /// Gets or sets the minimum height of the text box.
    /// </summary>
    public double TextMinHeight
    {
        get => GetValue(TextMinHeightProperty);
        set => SetValue(TextMinHeightProperty, value);
    }

    /// <summary>
    /// Gets or sets the maximum height of the text box.
    /// </summary>
    public double TextMaxHeight
    {
        get => GetValue(TextMaxHeightProperty);
        set => SetValue(TextMaxHeightProperty, value);
    }

    /// <summary>
    /// Gets or sets the font size for the text box.
    /// </summary>
    public double TextFontSize
    {
        get => GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether to use vertical layout (buttons beside text) instead of
    /// horizontal layout (buttons below text).
    /// </summary>
    public bool IsVerticalLayout
    {
        get => GetValue(IsVerticalLayoutProperty);
        set => SetValue(IsVerticalLayoutProperty, value);
    }

    public CaptionEditorControl()
    {
        InitializeComponent();
    }
}
