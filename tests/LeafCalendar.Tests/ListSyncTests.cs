using System.Collections.ObjectModel;
using System.Collections.Specialized;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ListSyncTests
{
    // Items are (Id, Version): the ID is the key, the version stands in for the row's data
    private sealed record Item(string Id, int Version);

    private sealed class Row(Item item)
    {
        public Item Item { get; set; } = item;
    }

    private static (ObservableCollection<Row> Shown, List<NotifyCollectionChangedAction> Changes, List<Row> Original) Shown(params string[] ids)
    {
        var shown = new ObservableCollection<Row>(ids.Select(id => new Row(new Item(id, 0))));
        var changes = new List<NotifyCollectionChangedAction>();
        shown.CollectionChanged += (_, e) => changes.Add(e.Action);
        return (shown, changes, [.. shown]);
    }

    private static List<Row> Fresh(params string[] ids) => [.. ids.Select(id => new Row(new Item(id, 1)))];

    private static void Sync(ObservableCollection<Row> shown, List<Row> fresh) =>
        ListSync.Apply(shown, fresh, r => r.Item.Id, (row, from) => row.Item = from.Item);

    [Fact]
    public void SameIds_KeepsEveryRow_AndOnlyUpdatesThem()
    {
        var (shown, changes, original) = Shown("a", "b", "c");

        Sync(shown, Fresh("a", "b", "c"));

        Assert.Empty(changes);
        Assert.Equal(original, shown);
        Assert.All(shown, r => Assert.Equal(1, r.Item.Version));
    }

    [Fact]
    public void OneRemoved_RemovesOnlyThatRow()
    {
        var (shown, changes, original) = Shown("a", "b", "c");

        Sync(shown, Fresh("a", "c"));

        Assert.Equal([NotifyCollectionChangedAction.Remove], changes);
        Assert.Equal([original[0], original[2]], shown);
    }

    [Fact]
    public void OneAdded_InsertsItWhereItBelongs_AndKeepsTheOthers()
    {
        var (shown, changes, original) = Shown("a", "c");
        var fresh = Fresh("a", "b", "c");

        Sync(shown, fresh);

        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
        Assert.Equal([original[0], fresh[1], original[1]], shown);
    }

    [Fact]
    public void Reordered_MovesTheRows_WithoutRemovingAny()
    {
        var (shown, changes, original) = Shown("a", "b", "c");

        Sync(shown, Fresh("c", "a", "b"));

        Assert.Equal([NotifyCollectionChangedAction.Move], changes);
        Assert.Equal([original[2], original[0], original[1]], shown);
    }

    [Fact]
    public void Reversed_OnlyMovesRows_AndUpdatesEveryOne()
    {
        var (shown, changes, original) = Shown("a", "b", "c", "d");

        Sync(shown, Fresh("d", "c", "b", "a"));

        Assert.NotEmpty(changes);
        Assert.All(changes, c => Assert.Equal(NotifyCollectionChangedAction.Move, c));
        Assert.Equal([original[3], original[2], original[1], original[0]], shown);
        Assert.All(shown, r => Assert.Equal(1, r.Item.Version));
    }

    [Fact]
    public void MovedRow_GetsItsDataUpdated()
    {
        var (shown, _, original) = Shown("a", "b");

        Sync(shown, Fresh("b", "a"));

        Assert.Same(original[1], shown[0]);
        Assert.Equal(1, shown[0].Item.Version);
    }

    [Fact]
    public void AddRemoveAndMoveTogether_EndsInTheFreshOrder()
    {
        var (shown, _, original) = Shown("a", "b", "c", "d");
        var fresh = Fresh("d", "e", "b");

        Sync(shown, fresh);

        Assert.Equal(["d", "e", "b"], shown.Select(r => r.Item.Id));
        Assert.Same(original[3], shown[0]);
        Assert.Same(fresh[1], shown[1]);
        Assert.Same(original[1], shown[2]);
    }

    [Fact]
    public void EmptyFresh_RemovesEverything()
    {
        var (shown, _, _) = Shown("a", "b");

        Sync(shown, Fresh());

        Assert.Empty(shown);
    }
}
