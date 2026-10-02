using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The time grid's day strip: one row of equal columns, realizing only the ones in (and around) the view.
/// </summary>
/// <remarks>
/// A uniform grid realizes by scroll offset ÷ column width. When the columns take a new width (a pane sliding, the
/// window resizing), the offset still belongs to the old width until the scroller catches up a layout pass later, so
/// the first day lands hundreds of days away and every column in view is built for those days, then built again.
/// <see cref="Pin"/> names the first day the view keeps; until the realization window holds that day again, the strip
/// realizes around it instead. Days near the window stay realized too (the repeater shrinks its window back to the
/// view whenever the view's size changes), so each step of a slide resizes the columns already there. Days the strip
/// stops asking for go back to the repeater on their own.
/// </remarks>
public sealed partial class DayStripLayout : VirtualizingLayout
{
    readonly List<UIElement> _realized = [];
    int _first = -1;
    int _last  = -1;
    int? _pin;

    /// <summary>Width of one day.</summary>
    public double ItemWidth { get; set; } = 120;

    /// <summary>Height of every day.</summary>
    public double ItemHeight { get; set; }

    /// <summary>Keeps <paramref name="index"/> as the first day in view until the scroller's offset matches the new width.</summary>
    public void Pin(int index)
    {
        _pin = index;
        InvalidateMeasure();
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var count = context.ItemCount;
        _realized.Clear();
        if (count == 0 || ItemWidth <= 0)
        {
            _first = _last = -1;
            return new Size(0, ItemHeight);
        }

        // The Window: the realization rect, or around the pinned day while the rect still belongs to the old width
        var rect  = context.RealizationRect;
        var span  = Math.Max(1, (int)Math.Ceiling(rect.Width / ItemWidth));
        var first = (int)Math.Floor(rect.X / ItemWidth);
        if (_pin is { } pin)
        {
            if (rect.X <= pin * ItemWidth && (pin + 1) * ItemWidth <= rect.X + rect.Width)
            {
                _pin = null;
            }
            else
            {
                first = pin - span / 3;
            }
        }

        first    = Math.Clamp(first, 0, count - 1);
        var last = Math.Min(count - 1, first + span);

        // Keep Days Still Near The Window
        if (_first >= 0)
        {
            first = Math.Max(Math.Min(first, _first), Math.Max(0, first - span));
            last  = Math.Min(Math.Max(last, Math.Min(_last, count - 1)), Math.Min(count - 1, last + span));
        }

        // Realize And Measure The Window (every pass asks for each day it keeps)
        var size = new Size(ItemWidth, ItemHeight);
        for (var i = first; i <= last; i++)
        {
            var element = context.GetOrCreateElementAt(i);
            element.Measure(size);
            _realized.Add(element);
        }

        (_first, _last) = (first, last);
        return new Size(count * ItemWidth, ItemHeight);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        for (var i = 0; i < _realized.Count; i++)
        {
            _realized[i].Arrange(new Rect((_first + i) * ItemWidth, 0, ItemWidth, ItemHeight));
        }

        return finalSize;
    }

    /// <inheritdoc />
    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        // A New Strip: the days around the old window mean nothing now
        _first = _last = -1;
        _pin   = null;
        InvalidateMeasure();
    }
}
