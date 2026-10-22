using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DiffusionNexus.UI.Behaviors;

/// <summary>
/// Keeps auto-height dialogs on the owner's screen (issue #630). A dialog with
/// <c>SizeToContent="Height"</c> and <c>WindowStartupLocation="CenterOwner"</c> is centred once,
/// at its opening height; when a collapsed section expands afterwards the window grows downward
/// only and its bottom — with the buttons — drops off the screen. On a 1350 px screen at 125 %
/// a tall dialog never fit at all.
/// <para>
/// <see cref="Register"/> runs once at app start and covers every owned dialog that sizes its own
/// height, so none of them needs to opt in. On open the client height is capped to the screen's
/// working area (a dialog with a <c>ScrollViewer</c> then scrolls instead of overflowing) and the
/// window is pulled inside it; whenever the content changes the height, a <c>CenterOwner</c> dialog
/// is re-centred on its owner, and any other one is only pulled back inside the working area.
/// A dialog the user has dragged somewhere stays where they put it, short of leaving the screen,
/// and a user resize (which turns <c>SizeToContent</c> off) is never fought.
/// </para>
/// </summary>
public static class DialogPlacement
{
    private static bool s_registered;

    /// <summary>Hooks every window's open event. Idempotent; call once from app start-up.</summary>
    public static void Register()
    {
        if (s_registered) return;
        s_registered = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Attach(window));
    }

    private static void Attach(Window window)
    {
        if (window.Owner is not Window owner) return;
        if (!window.SizeToContent.HasFlag(SizeToContent.Height)) return;

        var recentre = window.WindowStartupLocation == WindowStartupLocation.CenterOwner;
        var memory = new PlacementMemory();

        void Place()
        {
            if (!window.IsVisible || !owner.IsVisible) return;
            // The screen the dialog is on, not the owner's: a dialog dragged to another monitor is
            // kept there, and its working area and DesktopScaling then belong to the same screen.
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.ScreenFromWindow(owner);
            if (screen is null) return;

            var scale = window.DesktopScaling;
            var frame = window.FrameSize ?? window.ClientSize;
            var chrome = Math.Max(0, frame.Height - window.ClientSize.Height);

            var maxClient = MaxClientHeight(screen.WorkingArea, scale, chrome);
            // When the cap shrinks the window, the resize that follows places it again.
            if (window.MaxHeight > maxClient) window.MaxHeight = maxClient;

            var ownerScale = owner.DesktopScaling;
            var ownerFrame = owner.FrameSize ?? owner.ClientSize;
            var ownerBounds = new PixelRect(owner.Position, PixelSize.FromSize(ownerFrame, ownerScale));

            var target = ComputePosition(
                ownerBounds,
                PixelSize.FromSize(frame, scale),
                screen.WorkingArea,
                centreOnOwner: recentre && memory.ShouldCentre(window.Position),
                current: window.Position);

            if (target != window.Position) window.Position = target;
            memory.Placed(target);
        }

        void OnResized(object? sender, WindowResizedEventArgs e)
        {
            if (e.Reason == WindowResizeReason.User) return;
            if (!window.SizeToContent.HasFlag(SizeToContent.Height)) return;
            // FrameSize catches up with the new client size after the resize notification.
            Dispatcher.UIThread.Post(Place, DispatcherPriority.Background);
        }

        void OnClosed(object? sender, EventArgs e)
        {
            window.Resized -= OnResized;
            window.Closed -= OnClosed;
        }

        window.Resized += OnResized;
        window.Closed += OnClosed;
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Background);
    }

    /// <summary>
    /// Whether a dialog may still be re-centred: only while it sits where this behaviour last put
    /// it. Once the user has dragged it away it is never re-centred again for the rest of its life —
    /// remembering only the last spot would forget the drag after the next placement.
    /// </summary>
    internal sealed class PlacementMemory
    {
        private PixelPoint? _lastPlaced;
        private bool _userMoved;

        public bool ShouldCentre(PixelPoint current)
        {
            if (_lastPlaced is { } placed && placed != current) _userMoved = true;
            return !_userMoved;
        }

        public void Placed(PixelPoint position) => _lastPlaced = position;
    }

    /// <summary>
    /// Tallest client area that fits the working area once the title bar and borders
    /// (<paramref name="chromeDip"/>) are added. Never below 100 DIP.
    /// </summary>
    internal static double MaxClientHeight(PixelRect workingArea, double scale, double chromeDip) =>
        Math.Max(100, workingArea.Height / scale - chromeDip);

    /// <summary>
    /// Where the window's top-left corner goes: centred on the owner when asked, otherwise where
    /// it is, and in both cases pulled inside the working area — top edge first, so a window
    /// taller than the screen keeps its title bar reachable.
    /// </summary>
    internal static PixelPoint ComputePosition(
        PixelRect ownerBounds, PixelSize frame, PixelRect workingArea, bool centreOnOwner, PixelPoint current)
    {
        var x = centreOnOwner ? ownerBounds.X + (ownerBounds.Width - frame.Width) / 2 : current.X;
        var y = centreOnOwner ? ownerBounds.Y + (ownerBounds.Height - frame.Height) / 2 : current.Y;

        x = Math.Max(workingArea.X, Math.Min(x, workingArea.Right - frame.Width));
        y = Math.Max(workingArea.Y, Math.Min(y, workingArea.Bottom - frame.Height));
        return new PixelPoint(x, y);
    }
}
