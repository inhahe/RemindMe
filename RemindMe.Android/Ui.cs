using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Google.Android.Material.Card;
using RemindMe.Sync;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace RemindMe.Droid;

/// <summary>The app's screens: one place for what each would otherwise repeat.</summary>
public abstract class Screen : AppCompatActivity
{
    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        // If the process started while the phone was locked and it has been unlocked since,
        // the data is loaded now, before the screen reads it (AppState.Provisional).
        AppState.LoadOnceUnlocked();
        base.OnCreate(savedInstanceState);
    }

    protected T Find<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(Ui.ViewCtors)] T>(int id) where T : View =>
        FindViewById<T>(id)!;

    // ---- Linking (D3) -------------------------------------------------------------------

    /// <summary>Whether this screen asks another device's request to link while in front, as
    /// every screen does but the alarm's: the question mustn't get in the way of answering
    /// an alarm. Meanwhile, and while no screen is in front, a notification stands for it.</summary>
    protected virtual bool AsksToLink => true;

    /// <summary>The app's screen in front, if any.</summary>
    private static Screen? s_inFront;

    /// <summary>The alarm screen is in front: a request to link waits quietly.</summary>
    public static bool AlarmInFront => s_inFront is { AsksToLink: false };

    private AlertDialog? _linkDialog;
    /// <summary>The question <see cref="_linkDialog"/> asks.</summary>
    private PairPrompt? _asking;

    protected override void OnResume()
    {
        base.OnResume();
        s_inFront = this;
        AppState.FollowOwnName();
        if (!AsksToLink) return;
        AppState.PairRequested += AskToLink;
        AppState.PromptEnded += OnPromptEnded;
        if (AppState.PendingPrompt is { } p) AskToLink(p);
    }

    protected override void OnPause()
    {
        if (s_inFront == this) s_inFront = null;
        if (AsksToLink)
        {
            AppState.PairRequested -= AskToLink;
            AppState.PromptEnded -= OnPromptEnded;
            // The dialog belongs to this window; the question stays until answered, standing
            // as a notification meanwhile (but not across a rotation, which asks again at once).
            bool asking = _asking is not null;
            CloseLinkDialog();
            if (asking && !IsChangingConfigurations) AppState.PromptHidden();
        }
        base.OnPause();
    }

    private void CloseLinkDialog()
    {
        if (_linkDialog is { IsShowing: true } d && !IsDestroyed) d.Dismiss();
        _linkDialog = null;
        if (_asking is not null) AppState.PromptOnScreen = false;
        _asking = null;
    }

    private void OnPromptEnded(PairPrompt p)
    {
        if (_asking == p) CloseLinkDialog();
    }

    /// <summary>Asks whether to link; on either device, both users must accept.</summary>
    private void AskToLink(PairPrompt p)
    {
        if (_asking == p) return;
        CloseLinkDialog();
        _asking = p;
        AppState.PromptOnScreen = true;
        Notifications.Cancel(this, Notifications.PairId);
        // Withdrawn (the other device said no or went away): AppState raises PromptEnded.
        _linkDialog = new AlertDialog.Builder(this)
            .SetTitle("Link a device?")!
            .SetMessage($"{PairText.Question(p)}\n\n"
                + $"Both devices should be showing this code:\n\n{p.Code[..3]} {p.Code[3..]}\n\n"
                + "Accept only if the codes match.")!
            .SetPositiveButton("Link", (_, _) => AppState.AnswerPrompt(p, yes: true))!
            .SetNegativeButton("Don't link", (_, _) => AppState.AnswerPrompt(p, yes: false))!
            .SetCancelable(false)!
            .Show()!;
    }
}

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
