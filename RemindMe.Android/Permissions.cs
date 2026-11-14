using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;

namespace RemindMe.Droid;

/// <summary>The permissions and special access an alarm needs to reach the user (D4).</summary>
public static class Permissions
{
    /// <param name="Title">What is missing.</param>
    /// <param name="Why">What goes wrong without it.</param>
    /// <param name="Fix">Opens the system screen where the user can grant it.</param>
    public sealed record Need(string Title, string Why, Action<Activity> Fix);

    public static IEnumerable<Need> Missing(Context c)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && c.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            yield return new Need("Notifications are off",
                "Without notifications, alarms can't ring in the background or appear over the lock screen.",
                a => a.StartActivity(new Intent(Settings.ActionAppNotificationSettings)
                    .PutExtra(Settings.ExtraAppPackage, a.PackageName)));

        var am = (AlarmManager)c.GetSystemService(Context.AlarmService)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(31) && !am.CanScheduleExactAlarms())
        {
            var action = Settings.ActionRequestScheduleExactAlarm;   // read here, where the version is known
            yield return new Need("Exact alarms aren't allowed",
                "Without this, Android may deliver alarms minutes late.",
                a => a.StartActivity(new Intent(action, Android.Net.Uri.Parse($"package:{a.PackageName}"))));
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            var nm = (NotificationManager)c.GetSystemService(Context.NotificationService)!;
            if (!nm.CanUseFullScreenIntent())
            {
                var action = Settings.ActionManageAppUseFullScreenIntent;   // read where the version is known
                yield return new Need("Full-screen alarms aren't allowed",
                    "Without this, a ringing alarm shows only as a notification instead of opening over the lock screen.",
                    a => a.StartActivity(new Intent(action, Android.Net.Uri.Parse($"package:{a.PackageName}"))));
            }
        }
    }

    /// <summary>Whether Android's battery optimisation may hold back sync in the background.</summary>
    public static bool BatteryOptimized(Context c)
    {
        var pm = (PowerManager)c.GetSystemService(Context.PowerService)!;
        return !pm.IsIgnoringBatteryOptimizations(c.PackageName);
    }

    /// <summary>Asks, with Android's own one-tap prompt, to let the app run without battery
    /// optimization (Samsung's "Unrestricted"). Where a phone has no such prompt, opens the
    /// list of apps' battery settings instead, and where it has neither, says where to look.</summary>
    public static void AskNoBatteryOptimization(Activity a)
    {
        var tries = new[]
        {
            new Intent(Settings.ActionRequestIgnoreBatteryOptimizations, Android.Net.Uri.Parse($"package:{a.PackageName}")),
            new Intent(Settings.ActionIgnoreBatteryOptimizationSettings),
        };
        foreach (var intent in tries)
        {
            try { a.StartActivity(intent); return; }
            catch (ActivityNotFoundException) { }
        }
        Android.Widget.Toast.MakeText(a, "In the phone's Settings, open Apps → RemindMe → Battery, and choose Unrestricted.",
            Android.Widget.ToastLength.Long)!.Show();
    }
}
