using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RemindMe.Desktop;

/// <summary>
/// A list row whose properties the list follows as they change, so a row can be updated in
/// place: its container, and the keyboard focus on it, survive the update.
/// </summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets <paramref name="field"/> to <paramref name="value"/>, telling the list
    /// when it changes.</summary>
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
