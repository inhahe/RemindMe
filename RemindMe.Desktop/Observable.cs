using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace RemindMe.Desktop;

/// <summary>
/// A list row whose properties the list follows as they change, so a row can be updated in
/// place: its container, and the keyboard focus on it, survive the update.
/// </summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets <paramref name="field"/> to <paramref name="value"/>, telling the list
    /// when it changes. Returns whether it changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }

    /// <summary>Tells the list that the property <paramref name="name"/> changed: for a
    /// property worked out from others.</summary>
    protected void Changed(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Keeps a list's rows in step with what it shows, and the focus on its rows.</summary>
public static class Rows
{
    /// <summary>Where a list's focus goes once it is laid out, after an update took away
    /// the row it was on: <see cref="Row"/>, or <see cref="WhenEmpty"/>'s element if null.</summary>
    private sealed class Pending
    {
        public object? Row;
        public Func<UIElement?>? WhenEmpty;
    }

    private static readonly ConditionalWeakTable<ListBox, Pending> s_pending = new();

    /// <summary>
    /// Updates <paramref name="list"/>'s rows, its ItemsSource, in place
    /// (<see cref="ListSync.Reconcile"/>, whose arguments the others are). If the row with
    /// the focus goes (its task deleted, say), the focus passes to the row now in its place,
    /// or to <paramref name="whenEmpty"/>'s element (else the list) if none is left. The
    /// selection doesn't follow: an action on it would then act on a row nobody chose.
    /// </summary>
    public static void Update<TItem, TRow, TKey>(ListBox list, ObservableCollection<TRow> rows, IEnumerable<TItem> items,
        Func<TItem, TKey> keyOfItem, Func<TRow, TKey> keyOfRow, Func<TItem, TRow> create, Action<TRow, TItem> update,
        Func<UIElement?>? whenEmpty = null)
        where TKey : notnull
    {
        if (!ReferenceEquals(list.ItemsSource, rows)) throw new ArgumentException("The rows must be the list's ItemsSource.", nameof(rows));
        var window = Window.GetWindow(list);
        // The row the focus is on, or on its way to after an update just before this one.
        bool waiting = s_pending.TryGetValue(list, out var pending);
        object? focused = waiting ? pending!.Row : FocusedRow(list, window);
        int focusedAt = focused is null ? -1 : list.Items.IndexOf(focused);
        ListSync.Reconcile(rows, items, keyOfItem, keyOfRow, create, update);
        if (window is null || focused is null || list.Items.Contains(focused)) return;

        if (!waiting)
        {
            s_pending.AddOrUpdate(list, pending = new Pending());
            list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusPending(list, window));
        }
        pending!.Row = list.Items.Count > 0 ? list.Items[Math.Min(focusedAt, list.Items.Count - 1)] : null;
        pending.WhenEmpty = whenEmpty;
    }

    /// <summary>Gives the focus to where the last update left it to go.</summary>
    private static void FocusPending(ListBox list, Window window)
    {
        if (!s_pending.TryGetValue(list, out var pending)) return;
        s_pending.Remove(list);
        UIElement? target;
        if (pending.Row is null) target = pending.WhenEmpty?.Invoke() ?? list;
        else
        {
            // A row scrolled out of view has no container until it is brought into view.
            if (list.ItemContainerGenerator.ContainerFromItem(pending.Row) is null)
            {
                list.ScrollIntoView(pending.Row);
                list.UpdateLayout();
            }
            target = list.ItemContainerGenerator.ContainerFromItem(pending.Row) as UIElement ?? list;
        }
        // The keyboard focus only in the active window: elsewhere taking it would bring the
        // window to the front. Its logical focus comes back when it is activated.
        if (window.IsActive) target.Focus();
        else FocusManager.SetFocusedElement(window, target);
    }

    /// <summary>The row with <paramref name="window"/>'s logical focus, or null. That is also
    /// there while another window is active, and while the keyboard is in a toolbar (a focus
    /// scope of its own): the main window's Delete button keeps the keyboard, and the focus
    /// is to come back to the list from there when the row goes.</summary>
    private static object? FocusedRow(ListBox list, Window? window)
    {
        if (window is null || FocusManager.GetFocusedElement(window) is not DependencyObject focused) return null;
        if (ItemsControl.ContainerFromElement(list, focused) is not { } container) return null;
        var row = list.ItemContainerGenerator.ItemFromContainer(container);
        return row == DependencyProperty.UnsetValue ? null : row;
    }
}
