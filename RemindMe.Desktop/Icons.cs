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
    public const string New = "\uE710";
    public const string Edit = "\uE70F";
    public const string Done = "\uE73E";
    public const string ShowAlarm = "\uEA8F";
    public const string Pause = "\uE769";
    public const string Play = "\uE768";
    public const string Stop = "\uE71A";
    public const string Restart = "\uE72C";
    public const string Delete = "\uE74D";
    public const string Devices = "\uE895";
    public const string Calendar = "\uE787";
    public const string Settings = "\uE713";
    public const string Exit = "\uE7E8";
    public const string Previous = "\uE76B";
    public const string Next = "\uE76C";
    public const string More = "\uE712";

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Icons), new PropertyMetadata(""));

    public static string GetGlyph(DependencyObject element) => (string)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string glyph) => element.SetValue(GlyphProperty, glyph);
}
