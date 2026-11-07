using System.Windows;

namespace RemindMe.Desktop;

/// <summary>
/// The icons, characters of Windows' icon font (Segoe Fluent Icons), named for what they
/// stand for here; XAML takes them as <c>{x:Static local:Icons.Edit}</c>. A button shows
/// one before its text through <see cref="GlyphProperty"/> and App.xaml's <c>WithIcon</c>
/// template; its content stays its text, so screen readers still name it and its access key
/// still works.
/// </summary>
public static class Icons
{
    public const string New = "";
    public const string Edit = "";
    public const string Done = "";
    public const string ShowAlarm = "";
    public const string Pause = "";
    public const string Play = "";
    public const string Stop = "";
    public const string Restart = "";
    public const string Delete = "";
    public const string Devices = "";
    public const string Calendar = "";
    public const string Settings = "";
    public const string Exit = "";
    public const string Previous = "";
    public const string Next = "";
    public const string More = "";

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Icons), new PropertyMetadata(""));

    public static string GetGlyph(DependencyObject element) => (string)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string glyph) => element.SetValue(GlyphProperty, glyph);
}
