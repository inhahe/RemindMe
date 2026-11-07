using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace RemindMe.Desktop;

/// <summary>
/// Keeps a window that sizes itself to its contents on the screen it opens on. Its contents
/// must scroll (a ScrollViewer above the buttons): the window grows no taller than the
/// screen's work area, and if centring it on its owner left an edge off the work area, it
/// moves back onto it.
/// </summary>
internal static class ScreenFit
{
    public static void Apply(Window window)
    {
        // Before the first layout: the screen is the owner's, as the window opens over it.
        window.SourceInitialized += (_, _) => window.MaxHeight = WorkArea(window, window.Owner ?? window).Height;
        window.Loaded += (_, _) =>
        {
            var area = WorkArea(window, window);
            window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.ActualHeight));
            window.Left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.ActualWidth));
        };
    }

    /// <summary>
    /// The work area (the screen less the taskbar) of the screen most of <paramref name="on"/>
    /// is on, in <paramref name="window"/>'s units.
    /// </summary>
    private static Rect WorkArea(Window window, Window on)
    {
        var pixels = Forms.Screen.FromHandle(new WindowInteropHelper(on).Handle).WorkingArea;
        var toUnits = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(toUnits.Transform(new Point(pixels.Left, pixels.Top)),
                        toUnits.Transform(new Point(pixels.Right, pixels.Bottom)));
    }
}
