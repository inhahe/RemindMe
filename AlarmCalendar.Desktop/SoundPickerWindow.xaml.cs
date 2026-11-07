using System.IO;
using System.Windows;

namespace AlarmCalendar.Desktop;

/// <summary>Picks a Windows sound or a sound file (R10), or "default" when choosing for a
/// task. Not modal (see <see cref="TaskEditorWindow"/>): OK reports the choice through
/// <see cref="Chosen"/>.</summary>
public partial class SoundPickerWindow : Window
{
    private readonly object _previewOwner = new();
    private readonly bool _allowDefault;

    /// <summary>Raised on OK with the chosen sound; null means "default".</summary>
    public event Action<SoundRef?>? Chosen;

    /// <summary>Opens a picker owned by <paramref name="owner"/>, or brings forward the one
    /// it already has.</summary>
    public static void Open(Window owner, SoundRef? current, bool allowDefault, Action<SoundRef?> chosen)
    {
        if (owner.OwnedWindows.OfType<SoundPickerWindow>().FirstOrDefault() is { } open) { open.Activate(); return; }
        var picker = new SoundPickerWindow(current, allowDefault) { Owner = owner };
        picker.Chosen += chosen;
        picker.Show();
    }

    /// <param name="current">The sound chosen now; null means "default".</param>
    /// <param name="allowDefault">Offer "default" (for a task). The default itself, in
    /// Settings, offers instead the built-in sound under that name.</param>
    public SoundPickerWindow(SoundRef? current, bool allowDefault)
    {
        InitializeComponent();
        _allowDefault = allowDefault;
        var sounds = SoundService.SystemSounds();
        SystemList.ItemsSource = sounds;

        if (allowDefault)
            DefaultRadio.Content = $"_Default: {(AppHost.Settings.DefaultSound ?? SoundService.BuiltIn).Name}";
        else
        {
            DefaultRadio.Content = $"The _built-in alarm sound ({SoundService.BuiltIn.Name})";
            DefaultHint.Text = "Used when nothing else is chosen, or a chosen file goes missing.";
        }

        switch (current?.Source)
        {
            case null:
                DefaultRadio.IsChecked = true;
                break;
            case SoundSource.System:
                SystemRadio.IsChecked = true;
                break;
            case SoundSource.File:
                FileRadio.IsChecked = true;
                FilePath.Text = current.Value;
                break;
        }
        SystemList.SelectedItem = sounds.FirstOrDefault(s => current?.Source == SoundSource.System && s.Value == current.Value)
            ?? sounds.FirstOrDefault(s => s.Value == SoundService.BuiltIn.Value) ?? sounds.FirstOrDefault();
        SystemList.SelectionChanged += (_, _) => { SystemRadio.IsChecked = true; Preview(); };
        Closed += (_, _) => AppHost.Sounds.Stop(_previewOwner);
    }

    /// <summary>The sound the controls describe; null for "default".</summary>
    private SoundRef? Selected()
    {
        if (SystemRadio.IsChecked == true) return SystemList.SelectedItem as SoundRef;
        if (FileRadio.IsChecked == true && FilePath.Text.Length > 0)
            return new SoundRef(SoundSource.File, FilePath.Text, Path.GetFileName(FilePath.Text));
        return null;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a sound file",
            Filter = "Sound files|*.wav;*.mp3;*.wma;*.m4a;*.aac;*.flac;*.aif;*.aiff|All files|*.*",
        };
        if (FilePath.Text.Length > 0) dlg.InitialDirectory = Path.GetDirectoryName(FilePath.Text);
        if (dlg.ShowDialog(this) != true) return;
        FilePath.Text = dlg.FileName;
        FileRadio.IsChecked = true;
        Preview();
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => Preview();

    private void Preview()
    {
        var s = Selected();
        var path = s is null && !_allowDefault ? SoundService.PathOf(SoundService.BuiltIn) : SoundService.Resolve(s, out _);
        AppHost.Sounds.Play(_previewOwner, path, loop: false);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => AppHost.Sounds.Stop(_previewOwner);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (FileRadio.IsChecked == true && FilePath.Text.Length == 0)
        {
            MessageBox.Show(this, "Choose a file first, or pick another option.", Title);
            return;
        }
        Chosen?.Invoke(Selected());
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
