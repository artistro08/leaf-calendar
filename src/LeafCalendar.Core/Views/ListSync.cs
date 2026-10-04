using System.Collections.ObjectModel;

namespace LeafCalendar.Core.Views;

/// <summary>
/// Brings a shown list in line with a freshly built one, item by item, so a list control keeps every row that's
/// still there (no rebuild, no animation on it) and only animates the rows that really came or went.
/// </summary>
public static class ListSync
{
    /// <summary>
    /// Makes <paramref name="shown"/> hold the same keys as <paramref name="fresh"/>, in the same order.
    /// </summary>
    /// <remarks>
    /// Rows whose key is gone are removed, new keys are inserted from <paramref name="fresh"/>, rows that moved are
    /// moved (one <c>Move</c>, not a remove and an add), and every kept row gets <paramref name="update"/> with its
    /// fresh twin so its data catches up in place. Keys must be unique within each list.
    /// </remarks>
    /// <param name="shown">The list the UI is bound to (changed in place).</param>
    /// <param name="fresh">The newly built list.</param>
    /// <param name="key">The identity of an item (a calendar ID, an account email).</param>
    /// <param name="update">Copies the fresh item's data onto the kept one (kept, fresh).</param>
    public static void Apply<T, TKey>(ObservableCollection<T> shown, IReadOnlyList<T> fresh, Func<T, TKey> key, Action<T, T> update)
        where TKey : notnull
    {
        // Remove Rows That Are Gone (from the end, so the indexes stay right)
        var freshKeys = fresh.Select(key).ToHashSet();
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!freshKeys.Contains(key(shown[i])))
            {
                shown.RemoveAt(i);
            }
        }

        // Walk The Fresh Order: keep, move up, or insert
        var comparer = EqualityComparer<TKey>.Default;
        for (var i = 0; i < fresh.Count; i++)
        {
            var want = key(fresh[i]);
            var at = -1;
            for (var j = i; j < shown.Count; j++)
            {
                if (comparer.Equals(key(shown[j]), want))
                {
                    at = j;
                    break;
                }
            }

            if (at < 0)
            {
                shown.Insert(i, fresh[i]);
                continue;
            }

            if (at != i)
            {
                shown.Move(at, i);
            }

            update(shown[i], fresh[i]);
        }
    }
}
