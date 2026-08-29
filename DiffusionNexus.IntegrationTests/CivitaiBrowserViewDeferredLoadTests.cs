using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DiffusionNexus.Civitai;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using DiffusionNexus.UI.Views.CivitaiBrowser;
using Moq;

namespace DiffusionNexus.IntegrationTests;

/// <summary>
/// Regression coverage for the "Browse Civitai opens empty until you type" bug (PR #547
/// follow-up). <see cref="CivitaiBrowserView"/> is hosted as
/// <c>&lt;browser:CivitaiBrowserView DataContext="{Binding BrowserViewModel}"/&gt;</c> inside a
/// <c>TabItem</c> (see <c>LoraViewerView.axaml</c>) — a binding on the control itself, which must
/// resolve the inherited parent DataContext before it produces a value. When a TabControl
/// realises a not-yet-selected tab's content, the view's visual-tree attach can fire before that
/// binding has resolved, so a trigger written only in <c>OnAttachedToVisualTree</c> can see a null
/// DataContext and never call <see cref="CivitaiBrowserViewModel.EnsureLoadedAsync"/> — leaving the
/// grid empty and the installed-badge/filter permanently backed by an empty index.
/// <para>
/// This test isolates the essential mechanism — a runtime <see cref="Binding"/> on
/// <c>DataContext</c> whose activation is tied to the target's visual-tree attachment, versus the
/// target's logical parenting, which can happen independently and in either order — using a plain
/// <see cref="Panel"/> as the host rather than an actual <see cref="TabControl"/>. A real
/// <c>TabControl</c> only resolves a control template under a loaded theme (FluentTheme/
/// SimpleTheme), and this project's <c>[AvaloniaFact]</c> headless session runs a themeless
/// <c>Avalonia.Application</c> (confirmed empirically: <c>Application.Current</c> is a bare
/// <c>Avalonia.Application</c>, not <c>DiffusionNexus.UI.App</c>, with zero merged styles) — under
/// which a <c>TabControl</c> never produces a container for its content at all, so its selection
/// can never be observed to attach anything. <see cref="Panel"/> needs no template: children become
/// direct visual children the moment the panel itself is attached, which lets the test control
/// attach-vs-binding order explicitly without depending on theme resources this harness doesn't
/// load. It still exercises the real production mechanism — Avalonia's own binding-activation-on-
/// attach behavior — just without the TabControl chrome around it.
/// </para>
/// <para>
/// This cannot live in <c>DiffusionNexus.Tests</c>: that project deliberately never initializes an
/// Avalonia platform (see the note on <c>LoraViewerLibraryNotifierTests</c>, "no Avalonia platform
/// is initialised (that deadlocks the suite)"), and an attach/DataContext-binding race is only
/// observable with a real visual tree. This project already carries a headless Avalonia session for
/// every <c>[AvaloniaFact]</c> test, so this adds no new global test initialization.
/// </para>
/// </summary>
public class CivitaiBrowserViewDeferredLoadTests
{
    private sealed class PanelHost
    {
        public required CivitaiBrowserViewModel BrowserViewModel { get; init; }
    }

    private static CivitaiBrowserViewModel CreateBrowserViewModel(Mock<IUnifiedLogger> logger) =>
        new(civitaiClient: null,
            settingsService: null,
            logger: logger.Object,
            queue: new CivitaiDownloadQueue(null),
            waitlist: new CivitaiWaitlist(null, null,
                persistPathOverride: Path.Combine(Path.GetTempPath(), $"dn-waitlist-{Guid.NewGuid():N}.json")),
            sharedBaseModelSource: null);

    private static CivitaiBrowserView CreateBoundView() =>
        new CivitaiBrowserView().With(v => v.Bind(Control.DataContextProperty, new Binding(nameof(PanelHost.BrowserViewModel))));

    private static void AssertDeferredLoadStarted(Mock<IUnifiedLogger> logger, Times times) =>
        logger.Verify(l => l.Debug(LogCategory.Network, "CivitaiBrowser",
            It.Is<string>(m => m.Contains("Deferred initial load", StringComparison.Ordinal)),
            null),
            times);

    /// <summary>
    /// Mirrors the actual regression: the view's logical parent (and hence the binding's source)
    /// is only assigned after the view is already visually attached — the exact order the bug
    /// report diagnosed for a TabControl realising a just-selected tab.
    /// </summary>
    [AvaloniaFact]
    public void AttachBeforeDataContext_StillTriggersDeferredLoad()
    {
        var logger = new Mock<IUnifiedLogger>();
        var browserVm = CreateBrowserViewModel(logger);
        var browserView = CreateBoundView();

        var host = new Panel();
        host.Children.Add(browserView);

        var window = new Window { Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The binding's source is only assigned now, after the view is already attached.
        host.DataContext = new PanelHost { BrowserViewModel = browserVm };
        Dispatcher.UIThread.RunJobs();

        AssertDeferredLoadStarted(logger, Times.Once());

        window.Close();
    }

    /// <summary>
    /// The opposite order: the DataContext binding resolves while the view is still logically
    /// parented but not yet visually attached (not part of any shown window), and attachment
    /// happens afterwards.
    /// </summary>
    [AvaloniaFact]
    public void DataContextBeforeAttach_StillTriggersDeferredLoad()
    {
        var logger = new Mock<IUnifiedLogger>();
        var browserVm = CreateBrowserViewModel(logger);
        var browserView = CreateBoundView();

        var host = new Panel { DataContext = new PanelHost { BrowserViewModel = browserVm } };
        host.Children.Add(browserView);

        // Never attached (never shown) yet: the whole point of the lazy trigger is that content
        // the user hasn't looked at must not search Civitai.
        AssertDeferredLoadStarted(logger, Times.Never());

        var window = new Window { Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        AssertDeferredLoadStarted(logger, Times.Once());

        window.Close();
    }

    /// <summary>
    /// Detaching and reattaching (the "switch tabs away and back" case) must not re-run the initial
    /// search: <see cref="CivitaiBrowserViewModel.EnsureLoadedAsync"/> is idempotent
    /// (<c>Interlocked.Exchange</c>), and the view must keep relying on that rather than firing a
    /// second deferred load of its own.
    /// </summary>
    [AvaloniaFact]
    public void DetachAndReattach_DoesNotReRunDeferredLoad()
    {
        var logger = new Mock<IUnifiedLogger>();
        var browserVm = CreateBrowserViewModel(logger);
        var browserView = CreateBoundView();

        var host = new Panel { DataContext = new PanelHost { BrowserViewModel = browserVm } };
        host.Children.Add(browserView);

        var window = new Window { Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        AssertDeferredLoadStarted(logger, Times.Once());

        host.Children.Remove(browserView);
        Dispatcher.UIThread.RunJobs();
        host.Children.Add(browserView);
        Dispatcher.UIThread.RunJobs();

        AssertDeferredLoadStarted(logger, Times.Once());

        window.Close();
    }
}

file static class ObjectExtensions
{
    /// <summary>Small fluent helper so <see cref="Control.Bind"/> (void-returning) can be chained
    /// inside an expression-bodied factory method.</summary>
    public static T With<T>(this T obj, Action<T> configure)
    {
        configure(obj);
        return obj;
    }
}
