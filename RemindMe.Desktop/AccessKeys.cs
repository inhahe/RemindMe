namespace RemindMe.Desktop;

/// <summary>Text put into a control's Content, where WPF takes "_" as an access key.</summary>
public static class AccessKeys
{
    /// <summary><paramref name="text"/> (a sound's name, say) shown as it is: each "_"
    /// doubled, so that none becomes an access key, taking one from another control.</summary>
    public static string Literal(string text) => text.Replace("_", "__");
}
