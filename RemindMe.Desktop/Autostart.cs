using Microsoft.Win32;

namespace RemindMe.Desktop;

/// <summary>
/// "Start with Windows": a single per-user Run entry, written only by the app, carrying
/// <c>--minimized</c> so a boot start goes straight to the tray (D1). Per-user only, as in
/// LanLink: a machine-wide entry would need elevation to remove, and two entries would
/// start two copies racing for the single-instance mutex.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>An instance with its own <c>--data-dir</c> gets an entry of its own, so it
    /// can never overwrite or delete the normal instance's.</summary>
    private static string ValueName => IsDefaultDataDir ? AppHost.Name : $"{AppHost.Name} ({AppHost.DataDir})";

    private static bool IsDefaultDataDir => string.Equals(AppHost.DataDir, AppHost.DefaultDataDir, StringComparison.OrdinalIgnoreCase);

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Makes the Run entry match <paramref name="enabled"/>, pointing at this exe.</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is null) return;
                var args = IsDefaultDataDir ? "--minimized" : $"--minimized --data-dir \"{AppHost.DataDir}\"";
                key.SetValue(ValueName, $"\"{exe}\" {args}");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            AppHost.Log($"Could not update the autostart entry: {e.Message}");
        }
    }

    /// <summary>On the very first run, applies the default (on): an alarm app that isn't
    /// running after a reboot rings nothing until it's opened. Later runs leave the entry
    /// to the Settings window, so a choice made there sticks.</summary>
    public static void ApplyOnFirstRun()
    {
        if (AppHost.Settings.AutostartInitialized)
        {
            // Keep the entry pointing at this exe if the app was moved or updated.
            if (AppHost.Settings.StartWithWindows && IsEnabled()) Apply(true);
            return;
        }
        // An instance with its own --data-dir (a test, an experiment) never starts with
        // Windows on its own: it could be a throwaway folder ringing test alarms at every login.
        if (!IsDefaultDataDir) AppHost.Settings.StartWithWindows = false;
        Apply(AppHost.Settings.StartWithWindows);
        AppHost.Settings.AutostartInitialized = true;
        AppHost.SaveSettings();
    }
}
