namespace RemindMe;

/// <summary>The volume and fade-in choices both apps offer (S10), and the fade-in curve
/// both play by, so a task sounds the same on every device.</summary>
public static class SoundLevels
{
    /// <summary>Percent of the device's own alarm volume.</summary>
    public static readonly IReadOnlyList<int> Volumes = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10];

    /// <summary>Seconds; 0 is no fade-in.</summary>
    public static readonly IReadOnlyList<int> FadeIns = [0, 10, 30, 60, 120, 300];

    public static string VolumeText(int percent) => $"{percent}%";

    /// <summary>The index of the choice in <paramref name="choices"/> closest to <paramref name="value"/>
    /// (a value another version offered, perhaps).</summary>
    public static int Nearest(IReadOnlyList<int> choices, int value)
    {
        int best = 0;
        for (int i = 1; i < choices.Count; i++)
            if (Math.Abs(choices[i] - value) < Math.Abs(choices[best] - value)) best = i;
        return best;
    }

    public static string FadeInText(int seconds) =>
        seconds <= 0 ? "no fade-in" : seconds < 60 ? $"fade in over {seconds} s" : $"fade in over {seconds / 60} min";

    /// <summary>The softest a fade-in starts: quiet, but never silent, so an alarm is heard
    /// from its first moment.</summary>
    public const double FadeFloor = 0.02;

    /// <summary>The volume (0 to 1) to play at <paramref name="elapsed"/> into a fade-in lasting
    /// <paramref name="fadeIn"/> towards <paramref name="target"/>. It rises with the square of
    /// the time: loudness is heard on a logarithmic scale, and a straight line would sound
    /// loud almost at once and then hardly change.</summary>
    public static double FadeLevel(double target, TimeSpan elapsed, TimeSpan fadeIn)
    {
        if (fadeIn <= TimeSpan.Zero || elapsed >= fadeIn) return target;
        double x = Math.Max(0, elapsed / fadeIn);
        return target * Math.Max(FadeFloor, x * x);
    }
}
