using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Three-option confirmation dialog shown when the user tries to enqueue Civitai
/// versions flagged as Early Access. Lists the affected titles and lets the user
/// pick between dropping the EA items, downloading them anyway, or cancelling.
/// </summary>
public enum EarlyAccessConfirmResult
{
    Cancel,
    SkipEarlyAccess,
    AddAnyway
}

public partial class EarlyAccessConfirmDialog : Window
{
    public EarlyAccessConfirmResult Result { get; private set; } = EarlyAccessConfirmResult.Cancel;

    public IReadOnlyList<string> EarlyAccessTitles { get; }
    public int EarlyAccessCount => EarlyAccessTitles.Count;

    /// <summary>Design-time / XAML loader constructor.</summary>
    public EarlyAccessConfirmDialog() : this([]) { }

    public EarlyAccessConfirmDialog(IReadOnlyList<string> earlyAccessTitles)
    {
        EarlyAccessTitles = earlyAccessTitles;
        DataContext = this;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = EarlyAccessConfirmResult.Cancel;
        Close();
    }

    private void OnSkipClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = EarlyAccessConfirmResult.SkipEarlyAccess;
        Close();
    }

    private void OnAddAnywayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = EarlyAccessConfirmResult.AddAnyway;
        Close();
    }
}
