using System.Collections.ObjectModel;

namespace RemindMe;

/// <summary>Keeps a list's rows in step with what it shows, changing as few rows as it can.</summary>
public static class ListSync
{
    /// <summary>
    /// Makes <paramref name="rows"/> show <paramref name="items"/>, in their order. The row of
    /// an item whose key was shown before is kept and updated in place; rows whose key is
    /// gone are removed; other items get new rows, filled before they are inserted. Rows are
    /// moved only as needed: the longest run of rows already in order stays, and each other
    /// row moves once (a move, unlike removing and inserting, keeps a row selected).
    /// </summary>
    /// <param name="keyOfItem">What identifies an item's row from one update to the next.
    /// Keys must be unique among <paramref name="items"/>, and among the rows.</param>
    /// <param name="keyOfRow">The key a row was made for.</param>
    /// <param name="create">A new row for an item; <paramref name="update"/> then fills it.</param>
    /// <param name="update">Sets a row's properties from its item.</param>
    /// <exception cref="ArgumentException">Two items, or two rows, have one key.</exception>
    public static void Reconcile<TItem, TRow, TKey>(ObservableCollection<TRow> rows, IEnumerable<TItem> items,
        Func<TItem, TKey> keyOfItem, Func<TRow, TKey> keyOfRow, Func<TItem, TRow> create, Action<TRow, TItem> update)
        where TKey : notnull
    {
        var wanted = items.ToList();
        var targetOf = new Dictionary<TKey, int>();
        for (int t = 0; t < wanted.Count; t++)
            if (!targetOf.TryAdd(keyOfItem(wanted[t]), t))
                throw new ArgumentException($"Two items have the key {keyOfItem(wanted[t])}.", nameof(items));

        // Rows whose key is gone go first, from the end so the indices hold.
        var rowOf = new Dictionary<TKey, TRow>();
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            var key = keyOfRow(rows[i]);
            if (!targetOf.ContainsKey(key)) rows.RemoveAt(i);
            else if (!rowOf.TryAdd(key, rows[i])) throw new ArgumentException($"Two rows have the key {key}.", nameof(rows));
        }

        // Of the rows left, the longest run already in the wanted order stays where it is.
        var staying = LongestRunInOrder(rows.Select(r => targetOf[keyOfRow(r)]).ToList());

        // Then, in the wanted order, each other row (moved or new) goes right after the row
        // wanted before it, which is already in place relative to the rest.
        for (int t = 0; t < wanted.Count; t++)
        {
            var item = wanted[t];
            var key = keyOfItem(item);
            if (rowOf.TryGetValue(key, out var row))
            {
                if (!staying.Contains(t))
                {
                    int from = rows.IndexOf(row);
                    int after = t == 0 ? -1 : rows.IndexOf(rowOf[keyOfItem(wanted[t - 1])]);
                    // Move takes the row out before putting it back: what follows it shifts.
                    int to = from <= after ? after : after + 1;
                    if (to != from) rows.Move(from, to);
                }
                update(row, item);
            }
            else
            {
                row = create(item);
                update(row, item);
                int after = t == 0 ? -1 : rows.IndexOf(rowOf[keyOfItem(wanted[t - 1])]);
                rows.Insert(after + 1, row);
                rowOf[key] = row;
            }
        }
    }

    /// <summary>The values of the longest strictly increasing subsequence of
    /// <paramref name="values"/> (all distinct).</summary>
    private static HashSet<int> LongestRunInOrder(IReadOnlyList<int> values)
    {
        // tails[k]: the index in values of the smallest last value of a run of length k + 1;
        // back[i]: the index of the value before values[i] in the best run ending there.
        var tails = new List<int>();
        var back = new int[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (values[tails[mid]] < values[i]) lo = mid + 1; else hi = mid;
            }
            back[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
        }
        var run = new HashSet<int>();
        for (int i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = back[i]) run.Add(values[i]);
        return run;
    }
}
