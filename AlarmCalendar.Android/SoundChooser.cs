using Android.App;
using Android.Content;
using Android.Media;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace AlarmCalendar.Droid;

/// <summary>
/// Picks a sound (R10): the phone's own alarm, ringtone and notification sounds (through
/// Android's ringtone picker), a sound file (through the document picker, keeping a
/// persistent read grant so it still plays after a reboot), or "default". The owning
/// activity forwards <c>OnActivityResult</c> to <see cref="HandleResult"/>.
/// </summary>
public sealed class SoundChooser(Activity activity)
{
    private const int RingtoneRequest = 7001, FileRequest = 7002;
    private Action<SoundRef?>? _pending;

    /// <param name="allowDefault">Offer "default" (choosing for a task); the default itself
    /// offers the system alarm sound in its place.</param>
    public void Show(SoundRef? current, bool allowDefault, Action<SoundRef?> chosen)
    {
        var defaultName = allowDefault
            ? $"Default ({AppState.Settings.DefaultSound?.Name ?? "the phone's alarm sound"})"
            : "The phone's alarm sound";
        string[] options = [defaultName, "An alarm, ringtone or notification sound…", "A sound file…"];
        new AlertDialog.Builder(activity).SetTitle("Sound")!
            .SetItems(options, (_, e) =>
            {
                switch (e.Which)
                {
                    case 0: chosen(null); break;
                    case 1: PickRingtone(current, chosen); break;
                    case 2: PickFile(chosen); break;
                }
            })!.Show();
    }

    private void PickRingtone(SoundRef? current, Action<SoundRef?> chosen)
    {
        _pending = chosen;
        var i = new Intent(RingtoneManager.ActionRingtonePicker)
            .PutExtra(RingtoneManager.ExtraRingtoneType, (int)RingtoneType.All)
            .PutExtra(RingtoneManager.ExtraRingtoneShowDefault, false)
            .PutExtra(RingtoneManager.ExtraRingtoneShowSilent, false)
            .PutExtra(RingtoneManager.ExtraRingtoneTitle, "Alarm sound");
        if (current?.Source == SoundSource.System)
            i.PutExtra(RingtoneManager.ExtraRingtoneExistingUri, Android.Net.Uri.Parse(current.Value));
        activity.StartActivityForResult(i, RingtoneRequest);
    }

    private void PickFile(Action<SoundRef?> chosen)
    {
        _pending = chosen;
        var i = new Intent(Intent.ActionOpenDocument).AddCategory(Intent.CategoryOpenable).SetType("audio/*");
        activity.StartActivityForResult(i, FileRequest);
    }

    /// <returns>True when the result was this chooser's.</returns>
    public bool HandleResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode is not (RingtoneRequest or FileRequest)) return false;
        var chosen = _pending;
        _pending = null;
        if (resultCode != Result.Ok || data is null || chosen is null) return true;
        if (requestCode == RingtoneRequest)
        {
            var uri = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? (Android.Net.Uri?)data.GetParcelableExtra(RingtoneManager.ExtraRingtonePickedUri, Java.Lang.Class.FromType(typeof(Android.Net.Uri)))
#pragma warning disable CA1422 // the old overload, for Android 12 and below only
                : (Android.Net.Uri?)data.GetParcelableExtra(RingtoneManager.ExtraRingtonePickedUri);
#pragma warning restore CA1422
            if (uri is null) return true;
            var title = RingtoneManager.GetRingtone(activity, uri)?.GetTitle(activity) ?? "Sound";
            chosen(new SoundRef(SoundSource.System, uri.ToString()!, title));
        }
        else if (data.Data is { } uri)
        {
            try
            {
                activity.ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
            }
            catch (Java.Lang.SecurityException e) { AppState.Log($"No lasting access to {uri}: {e.Message}"); }
            chosen(new SoundRef(SoundSource.File, uri.ToString()!, DisplayName(uri)));
        }
        return true;
    }

    private string DisplayName(Android.Net.Uri uri)
    {
        try
        {
            using var c = activity.ContentResolver!.Query(uri, [Android.Provider.IOpenableColumns.DisplayName], null, null, null);
            if (c is not null && c.MoveToFirst()) return c.GetString(0) ?? "Sound file";
        }
        catch (Java.Lang.Exception) { }
        return uri.LastPathSegment ?? "Sound file";
    }

    /// <summary>The name of the sound a task plays, for its button.</summary>
    public static string Describe(SoundRef? taskSound) =>
        taskSound?.Name ?? $"Default ({AppState.Settings.DefaultSound?.Name ?? "phone's alarm"})";
}
