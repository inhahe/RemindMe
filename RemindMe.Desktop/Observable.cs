using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    /// <summary>
    /// Updates <paramref name="list"/>'s rows in place (<see cref="ListSync.Reconcile"/>, whose
    /// arguments the others are). If the row with the window's focus goes (its task deleted,
    /// say), the focus, and the selection if the row had it, pass to the row now in its
    /// place, or to <paramref name="whenEmpty"/>'s element (else the list) if none is left.
    /// </summary>
    public static void Update<TItem, TRow, TKey>(Selector list, ObservableCollection<TRow> rows, IEnumerable<TItem> items,
        Func<TItem, TKey> keyOfItem, Func<TRow, TKey> keyOfRow, Func<TItem, TRow> create, Action<TRow, TItem> update,
        Func<UIElement?>? whenEmpty = null)
        where TKey : notnull
    {
        var window = Window.GetWindow(list);
        object? focused = FocusedRow(list, window);
        int focusedAt = focused is null ? -1 : list.Items.IndexOf(focused);
        bool selected = focused is not null && IsSelected(list, focused);
        ListSync.Reconcile(rows, items, keyOfItem, keyOfRow, create, update);
        if (focused is null || window is null || list.Items.Contains(focused)) return;

        object? next = list.Items.Count > 0 ? list.Items[Math.Min(focusedAt, list.Items.Count - 1)] : null;
        if (next is not null && selected) list.SelectedItem = next;
        // After the next layout, which makes the row's container.
        list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var target = next is null ? whenEmpty?.Invoke() ?? list : list.ItemContainerGenerator.ContainerFromItem(next) as UIElement;
            if (target is null) return;
            // The keyboard focus only in the active window: elsewhere taking it would bring
            // the window to the front. Its logical focus comes back when it is activated.
            if (window.IsActive) target.Focus();
            else FocusManager.SetFocusedElement(window, target);
        });
    }

    /// <summary>The row whose container has <paramref name="window"/>'s focus (its logical
    /// focus, so also while another window is active), or null.</summary>
    private static object? FocusedRow(Selector list, Window? window)
    {
        if (window is null || FocusManager.GetFocusedElement(window) is not DependencyObject focused) return null;
        if (ItemsControl.ContainerFromElement(list, focused) is not { } container) return null;
        var row = list.ItemContainerGenerator.ItemFromContainer(container);
        return row == DependencyProperty.UnsetValue ? null : row;
    }

    private static bool IsSelected(Selector list, object row) =>
        list is ListBox box ? box.SelectedItems.Contains(row) : Equals(list.SelectedItem, row);
}
