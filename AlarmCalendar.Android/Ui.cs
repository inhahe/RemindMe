using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Google.Android.Material.Card;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace AlarmCalendar.Droid;

/// <summary>Small helpers the screens share.</summary>
public static class Ui
{
    /// <summary>What FindViewById needs kept of a view type under trimming.</summary>
    public const System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes ViewCtors =
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors
        | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicConstructors;

    public static void SetupToolbar(AppCompatActivity a, string title, bool back)
    {
        var bar = a.FindViewById<Google.Android.Material.AppBar.MaterialToolbar>(Resource.Id.toolbar)!;
        a.SetSupportActionBar(bar);
        a.SupportActionBar!.Title = title;
        a.SupportActionBar.SetDisplayHomeAsUpEnabled(back);
    }

    /// <summary>Greys out a pane whose radio button isn't chosen, and makes its controls
    /// unusable, as the desktop editor does. Apply it to outer panes before inner ones: it
    /// sets every control inside, so the inner panes' own state must come after.</summary>
    public static void SetPaneEnabled(View pane, bool enabled)
    {
        pane.Alpha = enabled ? 1f : 0.45f;
        SetEnabledDeep(pane, enabled);
    }

    private static void SetEnabledDeep(View v, bool enabled)
    {
        v.Enabled = enabled;
        if (v is ViewGroup g)
            for (int i = 0; i < g.ChildCount; i++) SetEnabledDeep(g.GetChildAt(i)!, enabled);
    }

    public static bool Is24Hour(Context c) => Android.Text.Format.DateFormat.Is24HourFormat(c);

    public static string FormatTime(Context c, TimeOnly t) =>
        t.ToString(Is24Hour(c) ? (t.Second == 0 ? "HH:mm" : "HH:mm:ss") : (t.Second == 0 ? "h:mm tt" : "h:mm:ss tt"),
            System.Globalization.CultureInfo.CurrentCulture);

    public static string FormatDate(DateTime d) =>
        d.ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);

    public static void PickTime(Context c, TimeOnly current, Action<TimeOnly> picked) =>
        new TimePickerDialog(c, (_, e) => picked(new TimeOnly(e.HourOfDay, e.Minute)), current.Hour, current.Minute, Is24Hour(c)).Show();

    public static void PickDate(Context c, DateTime current, Action<DateTime> picked) =>
        new DatePickerDialog(c, (_, e) => picked(new DateTime(e.Year, e.Month + 1, e.DayOfMonth)), current.Year, current.Month - 1, current.Day).Show();

    public static int IntOf(EditText e) => int.TryParse(e.Text, out var v) ? v : 0;

    public static void Toast(Context c, string text) => Android.Widget.Toast.MakeText(c, text, ToastLength.Short)!.Show();

    public static void Confirm(Context c, string title, string message, string ok, Action onOk) =>
        new AlertDialog.Builder(c).SetTitle(title)!.SetMessage(message)!
            .SetPositiveButton(ok, (_, _) => onOk())!.SetNegativeButton("Cancel", (_, _) => { })!.Show();

    public static ArrayAdapter<string> SpinnerAdapter(Context c, IList<string> items)
    {
        var a = new ArrayAdapter<string>(c, Android.Resource.Layout.SimpleSpinnerItem, items);
        a.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        return a;
    }
}
