using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace RemindMe.Desktop;

/// <summary>
/// Keeps a window that sizes itself to its contents on the screen it is on. Its contents
/// must scroll (a ScrollViewer above the buttons): the window grows no taller than the
/// screen's work area (the screen less the taskbar), and when it opens, or its contents
/// grow it, it moves back inside the work area if an edge is outside.
/// </summary>
internal static class ScreenFit
{
    public static void Apply(Window window)
    {
        // Before the first layout, the screen the window opens on: its owner's, or the one
        // with the mouse for a window without one, as WPF centres them.
        window.SourceInitialized += (_, _) =>
        {
            var screen = window.Owner is { } owner
                ? Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle)
                : Forms.Screen.FromPoint(Forms.Cursor.Position);
            window.MaxHeight = WorkArea(window, screen).Height;
        };
        window.Loaded += (_, _) => Fit(window);
        window.SizeChanged += (_, _) => { if (window.IsLoaded) Fit(window); };
        // Moved to another screen: its height there, but where the user put it.
        window.LocationChanged += (_, _) =>
        {
            if (window.IsLoaded) window.MaxHeight = WorkArea(window, ScreenOf(window)).Height;
        };
    }

    private static void Fit(Window window)
    {
        var area = WorkArea(window, ScreenOf(window));
        window.MaxHeight = area.Height;
        window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.ActualHeight));
        window.Left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.ActualWidth));
    }

    /// <summary>The screen most of <paramref name="window"/> is on.</summary>
    private static Forms.Screen ScreenOf(Window window) =>
        Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle);

    /// <summary>The work area of <paramref name="screen"/>, in <paramref name="window"/>'s units.</summary>
    private static Rect WorkArea(Window window, Forms.Screen screen)
    {
        var pixels = screen.WorkingArea;
        var toUnits = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(toUnits.Transform(new Point(pixels.Left, pixels.Top)),
                        toUnits.Transform(new Point(pixels.Right, pixels.Bottom)));
    }
}
