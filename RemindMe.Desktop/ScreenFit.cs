using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace RemindMe.Desktop;

/// <summary>
/// Keeps a window that sizes itself to its contents on the screen it is on. Its contents
/// must scroll (a ScrollViewer above the buttons): the window grows no taller than the
/// screen's work area (the screen less the taskbar), and when it opens, or its contents
/// grow it, it moves back inside the work area if an edge is outside. Where the user puts
/// or sizes it is left alone.
/// </summary>
internal static class ScreenFit
{
    private const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;

    public static void Apply(Window window)
    {
        bool dragging = false;   // in Windows' move or size loop
        window.SourceInitialized += (_, _) =>
        {
            // Before the first layout, the screen the window opens on: its owner's, or the
            // one with the mouse for a window without one, as WPF centres them.
            var screen = window.Owner is { } owner
                ? Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle)
                : Forms.Screen.FromPoint(Forms.Cursor.Position);
            window.MaxHeight = WorkArea(window, screen).Height;
            // Dragged to another screen: that screen's height, once the drag ends; changed
            // halfway, the window would shrink and jump under the mouse.
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(
                (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == WM_ENTERSIZEMOVE) dragging = true;
                    else if (msg == WM_EXITSIZEMOVE) { dragging = false; CapHeight(window); }
                    return IntPtr.Zero;
                });
        };
        window.Loaded += (_, _) => Fit(window);
        // Only growth from its contents: once the user sizes it, SizeToContent is Manual.
        window.SizeChanged += (_, _) =>
        {
            if (window.IsLoaded && !dragging && window.SizeToContent != SizeToContent.Manual) Fit(window);
        };
    }

    /// <summary>Caps the window's height to its screen, and moves it inside the work area.</summary>
    private static void Fit(Window window)
    {
        // Maximized or minimized, Top and Left are the restore position, not to be moved.
        if (window.WindowState != WindowState.Normal) return;
        var area = CapHeight(window);
        window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.ActualHeight));
        window.Left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.ActualWidth));
    }

    /// <summary>Caps the window's height to the work area of the screen most of it is on,
    /// and returns that work area.</summary>
    private static Rect CapHeight(Window window)
    {
        var area = WorkArea(window, Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle));
        window.MaxHeight = area.Height;
        return area;
    }

    /// <summary>The work area of <paramref name="screen"/>, in <paramref name="window"/>'s units.</summary>
    private static Rect WorkArea(Window window, Forms.Screen screen)
    {
        var pixels = screen.WorkingArea;
        var toUnits = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(toUnits.Transform(new Point(pixels.Left, pixels.Top)),
                        toUnits.Transform(new Point(pixels.Right, pixels.Bottom)));
    }
}
