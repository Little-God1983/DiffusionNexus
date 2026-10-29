using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Views.CivitaiBrowser;

/// <summary>
/// The Recipes tab (#667). Pointer rules live here because they need the modifier keys: a plain click
/// opens the recipe, Ctrl toggles the card, Shift selects the range from the last click. Thumbnails come
/// from <see cref="RecipeThumbnails"/> (decoded once at card size, off the UI thread) when a card is realised.
/// </summary>
public partial class RecipesTabView : UserControl
{
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

    private static RecipeThumbnails? s_thumbnails;

    // The app's shared thumbnail LRU; a private one only where there is no app (the designer).
    private static RecipeThumbnails Thumbnails => s_thumbnails ??=
        new RecipeThumbnails(App.Services?.GetService<IThumbnailService>() ?? new ThumbnailService(maxCacheSize: 300));

    public RecipesTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private RecipesTabViewModel? ViewModel => DataContext as RecipesTabViewModel;

    private bool _attached;

    // As in the browser view: the DataContext binding can resolve before or after the attach, so both
    // hooks try; ActivateAsync shares one library read however often the tab is shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        TryActivate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        TryActivate();
    }

    private void TryActivate()
    {
        if (_attached && ViewModel is { } vm) _ = vm.ActivateAsync();
    }

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
        _ = ShowThumbnailAsync(image, path, cts.Token);
    }

    private static async Task ShowThumbnailAsync(Image image, string path, CancellationToken token)
    {
        try
        {
            var bitmap = await Thumbnails.LoadAsync(path, token);
            if (bitmap is null) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested) image.Source = bitmap;
            });
        }
        catch (OperationCanceledException) { /* the card left the screen */ }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // A file Avalonia cannot decode keeps the placeholder.
        }
    }

    /// <summary>The bitmap belongs to the thumbnail cache, which other cards may share it with: only let go of it.</summary>
    private void OnThumbDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Image image) return;
        if (s_loads.TryGetValue(image, out var cts))
        {
            cts.Cancel();
            s_loads.Remove(image);
        }
        image.Source = null;
    }
}
