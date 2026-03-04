using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffusionNexus.UI.Services.SpellCheck;

namespace DiffusionNexus.UI.Controls;

/// <summary>
/// A TextBox with real-time spell checking (red squiggly underlines) and autocomplete dropdown.
/// Designed as a drop-in replacement for Avalonia TextBox in caption editing scenarios.
/// </summary>
public class SpellCheckTextBox : TextBox
{
    private static ISpellCheckService? s_spellCheckService;
    private static IAutoCompleteService? s_autoCompleteService;

    private readonly List<SpellCheckError> _errors = [];
    private readonly Pen _squigglyPen;

    // Autocomplete state
    private Popup? _autoCompletePopup;
    private ListBox? _suggestionListBox;
    private readonly List<string> _suggestions = [];
    private string _currentWordPrefix = string.Empty;
    private int _currentWordStart;
    private bool _isAutoCompleteVisible;

    // Spell check context menu state
    private Popup? _spellCheckPopup;
    private ListBox? _correctionListBox;
    private int _contextMenuWordStart;
    private int _contextMenuWordLength;

    // Debounce timer
    private DispatcherTimer? _debounceTimer;

    // Cached TextPresenter for caret position lookups
    private TextPresenter? _textPresenter;

    // Guard to suppress autocomplete while we're programmatically editing text
    private bool _suppressAutoComplete;

    /// <summary>
    /// Initializes the shared spell check and autocomplete services.
    /// Call once at startup from App.axaml.cs after DI is configured.
    /// </summary>
    public static void Initialize(ISpellCheckService spellCheckService, IAutoCompleteService autoCompleteService)
    {
        s_spellCheckService = spellCheckService;
        s_autoCompleteService = autoCompleteService;
    }

    /// <summary>
    /// Creates a new SpellCheckTextBox.
    /// </summary>
    public SpellCheckTextBox()
    {
        _squigglyPen = new Pen(Brushes.Red, 1.5, dashStyle: null);
    }

    /// <summary>
    /// Tells Avalonia to apply the built-in TextBox control theme to this subclass.
    /// Without this, no template is applied and the control renders blank.
    /// </summary>
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // Create autocomplete popup
        _suggestionListBox = new ListBox
        {
            MaxHeight = 200,
            MinWidth = 150,
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            FontSize = 13,
        };
        _suggestionListBox.PointerPressed += OnSuggestionPointerPressed;

        _autoCompletePopup = new Popup
        {
            Child = _suggestionListBox,
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
        };

        // Create spell check corrections popup
        _correctionListBox = new ListBox
        {
            MaxHeight = 200,
            MinWidth = 120,
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            FontSize = 13,
        };
        _correctionListBox.PointerPressed += OnCorrectionPointerPressed;

        _spellCheckPopup = new Popup
        {
            Child = _correctionListBox,
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
        };

        // Set logical parent for property inheritance (DataContext, theme, etc.).
        // Popups must NOT be added to VisualChildren — they create their own
        // top-level overlay host when opened and adding them breaks the TextBox template.
        ((ISetLogicalParent)_autoCompletePopup).SetParent(this);
        ((ISetLogicalParent)_spellCheckPopup).SetParent(this);

        // Set up debounced spell check
        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            RunSpellCheck();
        };

        // Cache the TextPresenter for caret position lookups
        _textPresenter = this.GetVisualDescendants()
            .OfType<TextPresenter>()
            .FirstOrDefault();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty)
        {
            // Restart debounce timer for spell checking
            _debounceTimer?.Stop();
            _debounceTimer?.Start();

            // Defer autocomplete so CaretIndex has time to update
            if (!_suppressAutoComplete)
            {
                Dispatcher.UIThread.Post(UpdateAutoComplete, DispatcherPriority.Input);
            }
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_isAutoCompleteVisible && _suggestionListBox is not null)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (_suggestionListBox.SelectedIndex < _suggestionListBox.ItemCount - 1)
                        _suggestionListBox.SelectedIndex++;
                    else
                        _suggestionListBox.SelectedIndex = 0;
                    e.Handled = true;
                    return;

                case Key.Up:
                    if (_suggestionListBox.SelectedIndex > 0)
                        _suggestionListBox.SelectedIndex--;
                    else
                        _suggestionListBox.SelectedIndex = _suggestionListBox.ItemCount - 1;
                    e.Handled = true;
                    return;

                case Key.Tab:
                case Key.Enter:
                    AcceptAutoCompleteSuggestion();
                    e.Handled = true;
                    return;

                case Key.Escape:
                    HideAutoComplete();
                    e.Handled = true;
                    return;
            }
        }

        base.OnKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetCurrentPoint(this);

        // Right-click for spell check suggestions
        if (point.Properties.IsRightButtonPressed && s_spellCheckService is not null)
        {
            ShowSpellCheckSuggestions(e);
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_errors.Count == 0 || Text is null) return;

        // Find the TextPresenter inside the TextBox template
        var textPresenter = this.GetVisualDescendants()
            .OfType<TextPresenter>()
            .FirstOrDefault();

        if (textPresenter is null) return;

        var textLayout = textPresenter.TextLayout;
        if (textLayout is null) return;

        // Get the offset of the text presenter within this control
        var presenterOffset = textPresenter.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);

        foreach (var error in _errors)
        {
            if (error.StartIndex + error.Length > (Text?.Length ?? 0))
                continue;

            try
            {
                var startRect = textLayout.HitTestTextPosition(error.StartIndex);
                var endRect = textLayout.HitTestTextPosition(error.StartIndex + error.Length);

                var y = startRect.Bottom + presenterOffset.Y;
                var startX = startRect.Left + presenterOffset.X;
                var endX = endRect.Left + presenterOffset.X;

                // Handle multi-line: if the end is on a different line, clamp to line end
                if (Math.Abs(endRect.Top - startRect.Top) > 1)
                {
                    endX = Bounds.Width - Padding.Right;
                }

                DrawSquigglyLine(context, startX, endX, y);
            }
            catch
            {
                // Layout measurement can fail during rapid editing; skip this error
            }
        }
    }

    private static void DrawSquigglyLine(DrawingContext context, double startX, double endX, double y)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            const double waveHeight = 2.0;
            const double waveLength = 4.0;
            var x = startX;
            bool up = true;

            ctx.BeginFigure(new Point(x, y), false);

            while (x < endX)
            {
                x += waveLength;
                if (x > endX) x = endX;
                ctx.LineTo(new Point(x, up ? y - waveHeight : y + waveHeight));
                up = !up;
            }
        }

        context.DrawGeometry(null, new Pen(Brushes.Red, 1.5), geometry);
    }

    private void RunSpellCheck()
    {
        if (s_spellCheckService is null || !s_spellCheckService.IsReady)
        {
            _errors.Clear();
            InvalidateVisual();
            return;
        }

        var text = Text;
        if (string.IsNullOrEmpty(text))
        {
            _errors.Clear();
            InvalidateVisual();
            return;
        }

        _errors.Clear();
        _errors.AddRange(s_spellCheckService.CheckText(text));
        InvalidateVisual();
    }

    private void UpdateAutoComplete()
    {
        if (s_autoCompleteService is null || Text is null || CaretIndex <= 0)
        {
            HideAutoComplete();
            return;
        }

        // Find the current word being typed
        var text = Text;
        var caretPos = Math.Min(CaretIndex, text.Length);

        // Walk backwards from caret to find word start
        int wordStart = caretPos;
        while (wordStart > 0 && IsWordChar(text[wordStart - 1]))
        {
            wordStart--;
        }

        // Walk forward to see if caret is at end of word
        int wordEnd = caretPos;
        if (wordEnd < text.Length && IsWordChar(text[wordEnd]))
        {
            // Caret is in the middle of a word — don't show autocomplete
            HideAutoComplete();
            return;
        }

        var prefix = text[wordStart..caretPos];

        if (prefix.Length < 2)
        {
            HideAutoComplete();
            return;
        }

        _currentWordPrefix = prefix;
        _currentWordStart = wordStart;

        _suggestions.Clear();
        _suggestions.AddRange(s_autoCompleteService.GetSuggestions(prefix));

        if (_suggestions.Count == 0)
        {
            HideAutoComplete();
            return;
        }

        ShowAutoComplete();
    }

    private void ShowAutoComplete()
    {
        if (_autoCompletePopup is null || _suggestionListBox is null) return;

        _suggestionListBox.ItemsSource = _suggestions.ToList();
        _suggestionListBox.SelectedIndex = 0;

        // Position the popup near the caret
        PositionPopupAtCaret(_autoCompletePopup);

        _autoCompletePopup.IsOpen = true;
        _isAutoCompleteVisible = true;
    }

    /// <summary>
    /// Calculates the caret position via the TextPresenter's TextLayout and
    /// sets the popup's offset so it appears directly below the word being typed.
    /// </summary>
    private void PositionPopupAtCaret(Popup popup)
    {
        var presenter = _textPresenter
            ?? this.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();

        if (presenter?.TextLayout is null) return;

        try
        {
            var caretPos = Math.Min(CaretIndex, Text?.Length ?? 0);
            var caretRect = presenter.TextLayout.HitTestTextPosition(caretPos);
            var presenterOffset = presenter.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);

            popup.HorizontalOffset = caretRect.Left + presenterOffset.X;
            popup.VerticalOffset = caretRect.Bottom + presenterOffset.Y - Bounds.Height;
        }
        catch
        {
            // Fall back to default placement if layout measurement fails
            popup.HorizontalOffset = 0;
            popup.VerticalOffset = 0;
        }
    }

    private void HideAutoComplete()
    {
        if (_autoCompletePopup is not null)
            _autoCompletePopup.IsOpen = false;
        _isAutoCompleteVisible = false;
    }

    private void AcceptAutoCompleteSuggestion()
    {
        if (_suggestionListBox?.SelectedItem is not string selected || Text is null)
        {
            HideAutoComplete();
            return;
        }

        // Suppress autocomplete while we programmatically replace the text
        _suppressAutoComplete = true;
        try
        {
            // Replace the partial word with the selected suggestion
            var text = Text;
            var before = text[.._currentWordStart];
            var caretPos = Math.Min(CaretIndex, text.Length);
            var after = text[caretPos..];

            Text = before + selected + " " + after;
            CaretIndex = before.Length + selected.Length + 1;

            // Record the word for future frequency boost
            s_autoCompleteService?.RecordWord(selected);
        }
        finally
        {
            _suppressAutoComplete = false;
        }

        HideAutoComplete();
    }

    private void OnSuggestionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Allow a short delay for selection to update, then accept
        Dispatcher.UIThread.Post(AcceptAutoCompleteSuggestion, DispatcherPriority.Input);
    }

    private void ShowSpellCheckSuggestions(PointerPressedEventArgs e)
    {
        if (s_spellCheckService is null || Text is null || _spellCheckPopup is null || _correctionListBox is null)
            return;

        // Find which word was right-clicked
        var caretPos = CaretIndex;
        var text = Text;

        if (caretPos < 0 || caretPos > text.Length)
            return;

        // Find the error at or near the caret position
        SpellCheckError? clickedError = null;
        foreach (var error in _errors)
        {
            if (caretPos >= error.StartIndex && caretPos <= error.StartIndex + error.Length)
            {
                clickedError = error;
                break;
            }
        }

        if (clickedError is null) return;

        _contextMenuWordStart = clickedError.StartIndex;
        _contextMenuWordLength = clickedError.Length;

        var suggestions = s_spellCheckService.Suggest(clickedError.Word);

        var items = new List<string>(suggestions);
        items.Add("──────────");
        items.Add($"Add \"{clickedError.Word}\" to dictionary");

        _correctionListBox.ItemsSource = items;
        _correctionListBox.SelectedIndex = -1;

        PositionPopupAtCaret(_spellCheckPopup);
        _spellCheckPopup.IsOpen = true;
        e.Handled = true;
    }

    private void OnCorrectionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_correctionListBox?.SelectedItem is not string selected || Text is null)
            {
                if (_spellCheckPopup is not null)
                    _spellCheckPopup.IsOpen = false;
                return;
            }

            if (selected.StartsWith("Add \"", StringComparison.Ordinal))
            {
                // "Add to dictionary" option
                var word = Text.Substring(_contextMenuWordStart, _contextMenuWordLength);
                s_spellCheckService?.AddToUserDictionary(word);
                _spellCheckPopup!.IsOpen = false;
                RunSpellCheck();
                return;
            }

            if (selected.StartsWith("──", StringComparison.Ordinal))
            {
                // Separator — ignore
                return;
            }

            // Replace the misspelled word with the selected correction
            var text = Text;
            var before = text[.._contextMenuWordStart];
            var after = text[(_contextMenuWordStart + _contextMenuWordLength)..];
            Text = before + selected + after;
            CaretIndex = before.Length + selected.Length;

            _spellCheckPopup!.IsOpen = false;
            RunSpellCheck();
        }, DispatcherPriority.Input);
    }

    /// <inheritdoc />
    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        // Small delay to allow popup click processing
        Dispatcher.UIThread.Post(HideAutoComplete, DispatcherPriority.Background);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '\'';
}
