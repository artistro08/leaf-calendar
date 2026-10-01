using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Keeps each item of an items control alive for exactly as long as a container shows it.
/// </summary>
/// <remarks>
/// WinUI holds an item through its container with a reference .NET's garbage collector doesn't count. When a list is
/// swapped, a container that isn't cleared yet still points at its old item; in the Native AOT build a collection in
/// between frees the item, and WinUI's next look at it ends the process (a <c>NullReferenceException</c> in
/// <c>ComWrappers.ManagedObjectWrapper.QueryInterface</c>, queried for <c>IFrameworkElement</c>). The pin is taken when
/// the container is prepared and dropped when it's cleared, so the item lives as long as WinUI can reach it.
/// </remarks>
internal sealed class ItemPins
{
    // Containers By Reference (the same native container always comes back as the same .NET wrapper while we hold it)
    readonly Dictionary<DependencyObject, object> _pins = new(ReferenceEqualityComparer.Instance);

    /// <summary>Pins <paramref name="item"/> to <paramref name="container"/> (replacing what it showed before).</summary>
    public void Pin(DependencyObject container, object? item)
    {
        if (item is null)
        {
            _pins.Remove(container);
            return;
        }

        _pins[container] = item;
    }

    /// <summary>Lets go of what <paramref name="container"/> showed.</summary>
    public void Unpin(DependencyObject container) => _pins.Remove(container);
}

/// <summary>An <see cref="ItemsControl"/> whose items live as long as their containers (see <see cref="ItemPins"/>).</summary>
public partial class PinnedItemsControl : ItemsControl
{
    readonly ItemPins _pins = new();

    /// <summary>Creates the control with the stock ItemsControl look.</summary>
    public PinnedItemsControl() => DefaultStyleKey = typeof(ItemsControl);

    /// <inheritdoc />
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        _pins.Pin(element, item);
    }

    /// <inheritdoc />
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        base.ClearContainerForItemOverride(element, item);
        _pins.Unpin(element);
    }
}

/// <summary>A <see cref="ListView"/> whose items live as long as their containers (see <see cref="ItemPins"/>).</summary>
public partial class PinnedListView : ListView
{
    readonly ItemPins _pins = new();

    /// <summary>Creates the list with the stock ListView look.</summary>
    public PinnedListView() => DefaultStyleKey = typeof(ListView);

    /// <inheritdoc />
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        _pins.Pin(element, item);
    }

    /// <inheritdoc />
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        base.ClearContainerForItemOverride(element, item);
        _pins.Unpin(element);
    }
}

/// <summary>A <see cref="ComboBox"/> whose items live as long as their containers (see <see cref="ItemPins"/>).</summary>
public partial class PinnedComboBox : ComboBox
{
    readonly ItemPins _pins = new();

    /// <summary>Creates the box with the stock ComboBox look.</summary>
    public PinnedComboBox() => DefaultStyleKey = typeof(ComboBox);

    /// <inheritdoc />
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        _pins.Pin(element, item);
    }

    /// <inheritdoc />
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        base.ClearContainerForItemOverride(element, item);
        _pins.Unpin(element);
    }
}
