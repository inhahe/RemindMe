using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace RemindMe.Tests;

public class ListSyncTests
{
    private sealed class Row(string key)
    {
        public string Key { get; } = key;
        public int Value { get; set; } = -1;
    }

    /// <summary>Rows for <paramref name="keys"/>, and a log of the changes the list then
    /// reports ("move A", "remove A", "add A=value").</summary>
    private static (ObservableCollection<Row> Rows, List<string> Log) RowsOf(params string[] keys)
    {
        var rows = new ObservableCollection<Row>(keys.Select(k => new Row(k) { Value = 0 }));
        var log = new List<string>();
        rows.CollectionChanged += (_, e) => log.Add(e.Action switch
        {
            NotifyCollectionChangedAction.Move => $"move {((Row)e.OldItems![0]!).Key}",
            NotifyCollectionChangedAction.Remove => $"remove {((Row)e.OldItems![0]!).Key}",
            NotifyCollectionChangedAction.Add => $"add {((Row)e.NewItems![0]!).Key}={((Row)e.NewItems[0]!).Value}",
            _ => e.Action.ToString(),
        });
        return (rows, log);
    }

    /// <summary>Shows <paramref name="keys"/>, each item's value its position.</summary>
    private static void Show(ObservableCollection<Row> rows, params string[] keys) =>
        ListSync.Reconcile(rows, keys.Select((k, i) => (Key: k, Value: i)), x => x.Key, r => r.Key,
            x => new Row(x.Key), (r, x) => r.Value = x.Value);

    private static string[] Keys(ObservableCollection<Row> rows) => rows.Select(r => r.Key).ToArray();

    [Fact]
    public void ARowThatSinksOrRisesMovesOnceAndTheRestStay()
    {
        var (rows, log) = RowsOf("A", "B", "C", "D", "E");
        var kept = rows.ToList();
        Show(rows, "B", "C", "D", "E", "A");
        Assert.Equal(["B", "C", "D", "E", "A"], Keys(rows));
        Assert.Equal(["move A"], log);

        log.Clear();
        Show(rows, "A", "B", "C", "D", "E");
        Assert.Equal(["move A"], log);
        Assert.All(rows, r => Assert.Contains(r, kept));   // the same rows, not new ones
        Assert.Equal([0, 1, 2, 3, 4], rows.Select(r => r.Value));
    }

    [Fact]
    public void ARowGoneIsRemovedAndNothingElseMoves()
    {
        var (rows, log) = RowsOf("A", "B", "C", "D");
        Show(rows, "B", "C", "D");
        Assert.Equal(["remove A"], log);
        Assert.Equal(["B", "C", "D"], Keys(rows));
    }

    [Fact]
    public void ANewRowIsFilledBeforeItIsInsertedWhereItBelongs()
    {
        var (rows, log) = RowsOf("A", "C");
        Show(rows, "A", "B", "C");
        Assert.Equal(["add B=1"], log);
        Assert.Equal(["A", "B", "C"], Keys(rows));
    }

    [Fact]
    public void NothingChangedChangesNothing()
    {
        var (rows, log) = RowsOf("A", "B", "C");
        Show(rows, "A", "B", "C");
        Assert.Empty(log);
        Assert.Equal([0, 1, 2], rows.Select(r => r.Value));
    }

    [Fact]
    public void AKeyGivenTwiceIsAnError()
    {
        var (rows, _) = RowsOf("A");
        Assert.Throws<ArgumentException>(() => Show(rows, "A", "B", "A"));
    }

    [Fact]
    public void TwoRowsOfOneKeyAreAnErrorThatChangesNothing()
    {
        var (rows, log) = RowsOf("A", "A", "X");
        Assert.Throws<ArgumentException>(() => Show(rows, "A"));
        Assert.Equal(["A", "A", "X"], Keys(rows));
        Assert.Empty(log);
    }

    [Fact]
    public void AnyChangeEndsInTheWantedOrderWithTheKeptRowsKept()
    {
        var random = new Random(20261107);
        for (int round = 0; round < 2000; round++)
        {
            var before = Enumerable.Range(0, random.Next(0, 9)).Select(i => $"k{i}").ToList();
            var after = before.Where(_ => random.Next(4) > 0)
                .Concat(Enumerable.Range(0, random.Next(0, 4)).Select(i => $"n{i}"))
                .OrderBy(_ => random.Next()).ToArray();
            var (rows, log) = RowsOf([.. before]);
            var old = rows.ToDictionary(r => r.Key);
            Show(rows, after);
            Assert.Equal(after, Keys(rows));
            Assert.Equal(Enumerable.Range(0, after.Length), rows.Select(r => r.Value));
            foreach (var r in rows) if (old.TryGetValue(r.Key, out var was)) Assert.Same(was, r);
            // Each kept row moves at most once.
            Assert.Equal(log.Count(l => l.StartsWith("move")), log.Where(l => l.StartsWith("move")).Distinct().Count());
        }
    }
}
