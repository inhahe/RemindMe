using System.ComponentModel;
using System.Windows;
using RemindMe.Sync;

namespace RemindMe.Desktop;

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
        Question.Text = PairText.Question(prompt);
        CodeText.Text = $"{prompt.Code[..3]} {prompt.Code[3..]}";
        // The other device said no or went away: nothing left to answer.
        prompt.Withdrawn += OnWithdrawn;
        Closed += (_, _) => prompt.Withdrawn -= OnWithdrawn;
        // Withdrawn before this window existed: close as soon as it is shown.
        if (prompt.IsWithdrawn) Loaded += (_, _) => OnWithdrawn();
    }

    private void OnWithdrawn() => AppHost.OnUi(() =>
    {
        _answered = true;
        Close();
    });

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
