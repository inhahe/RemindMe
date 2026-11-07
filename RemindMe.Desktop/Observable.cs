using System.Collections.ObjectModel;
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

/// <summary>Keeps a list's rows in step with what it shows.</summary>
public static class Rows
{
    /// <summary>
    /// Makes <paramref name="rows"/> show <paramref name="items"/>, in their order: the row of
    /// an item whose key was shown before is kept (moved if need be, which keeps it selected)
    /// and updated in place; other items get new rows; rows whose key is gone are removed.
    /// </summary>
    /// <param name="keyOfItem">What identifies an item's row from one update to the next.
    /// An item whose key came earlier in <paramref name="items"/> is left out.</param>
    /// <param name="keyOfRow">The key a row was made for.</param>
    /// <param name="create">A new row for an item; <paramref name="update"/> then fills it.</param>
    /// <param name="update">Sets a row's properties from its item.</param>
    public static void Reconcile<TItem, TRow, TKey>(ObservableCollection<TRow> rows, IEnumerable<TItem> items,
        Func<TItem, TKey> keyOfItem, Func<TRow, TKey> keyOfRow, Func<TItem, TRow> create, Action<TRow, TItem> update)
        where TKey : notnull
    {
        var placed = new HashSet<TKey>();
        int i = 0;
        foreach (var item in items)
        {
            var key = keyOfItem(item);
            if (!placed.Add(key)) continue;
            // Rows before i are the items placed so far; the item's row, if any, is after.
            int at = -1;
            for (int j = i; j < rows.Count && at < 0; j++)
                if (EqualityComparer<TKey>.Default.Equals(keyOfRow(rows[j]), key)) at = j;
            TRow row;
            if (at < 0) rows.Insert(i, row = create(item));
            else
            {
                row = rows[at];
                if (at != i) rows.Move(at, i);
            }
            update(row, item);
            i++;
        }
        while (rows.Count > i) rows.RemoveAt(rows.Count - 1);
    }
}
