using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;

namespace DiffusionNexus.UI.Views.CivitaiBrowser;

/// <summary>
/// The Recipes tab (#667). Pointer rules live here because they need the modifier keys: a plain click
/// opens the recipe, Ctrl toggles the card, Shift selects the range from the last click. Thumbnails are
/// decoded off the UI thread when a card is realised and released when it leaves the tree.
/// </summary>
public partial class RecipesTabView : UserControl
{
    private const int ThumbnailDecodeWidth = 256;

    public static readonly IValueConverter IsAll = new FuncValueConverter<RecipeStatusFilter, bool>(f => f == RecipeStatusFilter.All);
    public static readonly IValueConverter IsReady = new FuncValueConverter<RecipeStatusFilter, bool>(f => f == RecipeStatusFilter.Ready);
    public static readonly IValueConverter IsMissing = new FuncValueConverter<RecipeStatusFilter, bool>(f => f == RecipeStatusFilter.Missing);
    public static readonly IValueConverter IsPositive = new FuncValueConverter<int, bool>(n => n > 0);

    private static readonly IBrush SelectedBorder = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush IdleBorder = new SolidColorBrush(Color.Parse("#2E2E2E"));
    private static readonly IBrush SafeBrush = new SolidColorBrush(Color.Parse("#1F9D55"));
    private static readonly IBrush NsfwBrush = new SolidColorBrush(Color.Parse("#C0392B"));
    private static readonly IBrush KeptStar = new SolidColorBrush(Color.Parse("#F5C542"));
    private static readonly IBrush IdleStar = new SolidColorBrush(Color.Parse("#99FFFFFF"));

    public static readonly IValueConverter SelectionBorder = new FuncValueConverter<bool, IBrush>(s => s ? SelectedBorder : IdleBorder);
    public static readonly IValueConverter RatingBrush = new FuncValueConverter<bool, IBrush>(safe => safe ? SafeBrush : NsfwBrush);
    public static readonly IValueConverter StarText = new FuncValueConverter<bool, string>(kept => kept ? "★" : "☆");
    public static readonly IValueConverter StarBrush = new FuncValueConverter<bool, IBrush>(kept => kept ? KeptStar : IdleStar);

    private static readonly ConditionalWeakTable<Image, CancellationTokenSource> s_loads = new();

    public RecipesTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private RecipesTabViewModel? ViewModel => DataContext as RecipesTabViewModel;

    private void OnStatusAll(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetStatus(RecipeStatusFilter.All);
    private void OnStatusReady(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetStatus(RecipeStatusFilter.Ready);
    private void OnStatusMissing(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetStatus(RecipeStatusFilter.Missing);

    private void SetStatus(RecipeStatusFilter filter)
    {
        if (ViewModel is { } vm) vm.StatusFilter = filter;
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RecipeCardViewModel card } border || ViewModel is not { } vm) return;
        if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;

        // The star and the tick box are buttons of their own; they must not also open or select the card.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (e.Source is Visual box && box.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl || shift) vm.Select(card, additive: ctrl, range: shift);
        else vm.OpenCommand.Execute(card);
        e.Handled = true;
    }

    private void OnThumbAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not RecipeCardViewModel { ThumbnailPath: { Length: > 0 } path }) return;

        var cts = new CancellationTokenSource();
        s_loads.AddOrUpdate(image, cts);
        var token = cts.Token;
        _ = Task.Run(() =>
        {
            try
            {
                if (token.IsCancellationRequested || !File.Exists(path)) return;
                using var stream = File.OpenRead(path);
                var bitmap = Bitmap.DecodeToWidth(stream, ThumbnailDecodeWidth);
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) bitmap.Dispose();
                    else image.Source = bitmap;
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // A file Avalonia cannot decode keeps the placeholder.
            }
        }, token);
    }

    private void OnThumbDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Image image) return;
        if (s_loads.TryGetValue(image, out var cts))
        {
            cts.Cancel();
            s_loads.Remove(image);
        }
        var bitmap = image.Source as Bitmap;
        image.Source = null;
        bitmap?.Dispose();
    }
}
