using System.ComponentModel;
using System.Windows;
using AlarmCalendar.Sync;

namespace AlarmCalendar.Desktop;

/// <summary>Asks whether another device may join this one's sync group (D3). Closing it
/// any other way than Link refuses.</summary>
public partial class PairPromptWindow : Window
{
    private readonly PairPrompt _prompt;
    private bool _answered;

    public PairPromptWindow(PairPrompt prompt)
    {
        InitializeComponent();
        _prompt = prompt;
        Question.Text = $"\"{prompt.PeerName}\" wants to link with this device and sync tasks with it.";
        CodeText.Text = $"{prompt.Code[..3]} {prompt.Code[3..]}";
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        _answered = true;
        _ = _prompt.AcceptAsync();
        Close();
    }

    private void Reject_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_answered) { _answered = true; _ = _prompt.RejectAsync(); }
    }
}
