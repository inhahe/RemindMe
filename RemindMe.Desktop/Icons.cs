using System.Windows;

namespace RemindMe.Desktop;

/// <summary>
/// The icon a button shows before its text, through App.xaml's <c>WithIcon</c> template: a
/// character of Windows' icon font (Segoe Fluent Icons). The button's content stays its
/// text, so screen readers still name it and its access key still works.
/// </summary>
public static class Icons
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Icons), new PropertyMetadata(""));

    public static string GetGlyph(DependencyObject element) => (string)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string glyph) => element.SetValue(GlyphProperty, glyph);

    // The glyphs code switches between; XAML writes the others as &#x...; directly.
    public const string Play = "";
    public const string Pause = "";
}
