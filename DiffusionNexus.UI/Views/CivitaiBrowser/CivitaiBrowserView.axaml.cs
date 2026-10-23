using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;

namespace DiffusionNexus.UI.Views.CivitaiBrowser;

public partial class CivitaiBrowserView : UserControl
{
    // Tracked ourselves rather than relying on IsAttachedToVisualTree/IsEffectivelyVisible: this
    // view's own attach/detach events are the only signal we need, and they are unambiguous.
    private bool _attachedToVisualTree;

    public CivitaiBrowserView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Pointer-pressed handler on each result card. Mirrors the Generation Gallery
    /// behavior: Shift+LMB extends the selection from the last clicked card to the
    /// current one; Ctrl+LMB toggles the current card; plain LMB clears and selects
    /// only the current card. Clicks on inner buttons/checkboxes are ignored so they
    /// keep their own behavior.
    /// </summary>
    private void OnResultCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.DataContext is not CivitaiResultViewModel item) return;
        if (DataContext is not CivitaiBrowserViewModel vm) return;

        // Let nested interactive controls handle the click themselves (checkbox on the
        // card, version-picker button + its flyout, etc.).
        if (e.Source is Visual visual)
        {
            if (visual.FindAncestorOfType<Button>() is not null) return;
            if (visual.FindAncestorOfType<CheckBox>() is not null) return;
        }

        var props = e.GetCurrentPoint(control).Properties;
        if (!props.IsLeftButtonPressed) return;

        var isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        vm.SelectWithModifiers(item, isShift, isCtrl);
        e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachedToVisualTree = true;
        TryEnsureLoaded();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attachedToVisualTree = false;
    }

    /// <summary>
    /// <see cref="DataContext"/> on this view is bound via <c>DataContext="{Binding BrowserViewModel}"</c>
    /// (see <c>LoraViewerView.axaml</c>), which resolves against the inherited parent DataContext. When
    /// the host TabControl realises this tab's content, <see cref="OnAttachedToVisualTree"/> can fire
    /// before that binding has produced a value, so a DataContext check made only there sometimes never
    /// sees the view model — the tab then shows an empty grid until the user types or changes a filter,
    /// and the Installed badge/filter stay dead because <c>RefreshInstalledSetAsync</c> never ran either.
    /// <see cref="OnDataContextChanged"/> covers the case where the binding resolves after attachment;
    /// this method requires both conditions so a DataContext assigned before the view is ever shown
    /// (e.g. eagerly by a parent) does not kick off a Civitai search for a tab the user hasn't opened —
    /// that laziness is deliberate. <see cref="CivitaiBrowserViewModel.EnsureLoadedAsync"/>
    /// is idempotent (<c>Interlocked.Exchange</c>), so calling it from both hooks — including repeatedly
    /// across tab switches — is safe and only ever runs the deferred load once.
    /// </summary>
    private void TryEnsureLoaded()
    {
        if (!_attachedToVisualTree) return;
        if (DataContext is not CivitaiBrowserViewModel vm) return;

        _ = vm.EnsureLoadedAsync();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        TryEnsureLoaded();
    }
}
