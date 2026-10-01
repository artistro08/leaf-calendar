using System.Collections.ObjectModel;
using System.Collections.Specialized;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ListSyncTests
{
    // Items are (Id, Version): the ID is the key, the version stands in for the row's data
    sealed record Item(string Id, int Version);

    sealed class Row(Item item)
    {
        public Item Item { get; set; } = item;
    }

    static (ObservableCollection<Row> Shown, List<NotifyCollectionChangedAction> Changes, List<Row> Original) Shown(params string[] ids)
    {
        var shown   = new ObservableCollection<Row>(ids.Select(id => new Row(new Item(id, 0))));
        var changes = new List<NotifyCollectionChangedAction>();
        shown.CollectionChanged += (_, e) => changes.Add(e.Action);
        return (shown, changes, [.. shown]);
    }

    static List<Row> Fresh(params string[] ids) => [.. ids.Select(id => new Row(new Item(id, 1)))];

    static void Sync(ObservableCollection<Row> shown, List<Row> fresh) =>
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
