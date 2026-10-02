using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The stock dropdown of every time zone, written as Windows writes them ("(UTC-05:00) New York", west to east). Used
/// for the event's zone in the editor, the share zone, and Settings' primary zone.
/// </summary>
/// <remarks>
/// The items are plain strings (a list of Core records can't go to WinRT under Native AOT; typing to search only works on
/// string rows) and the pick is read by index from our own list of IDs. The zone you're in is marked "· Current" and
/// can't be picked: choosing it puts the previous pick back. With <c>IsEditable</c> on, typed text is matched like the old search boxes (a city,
/// an alias such as NYC, or any part of a row) and only a zone from the list is taken.
/// The box has no XAML file of its own: a XAML file whose root is a ComboBox subclass fails to parse when the
/// box is built (XamlParseException), which kept the calendar page from opening. Rows are the stock string rows.
/// </remarks>
public sealed partial class TimeZoneComboBox : ComboBox
{
    // The rows' IANA IDs, in the box's order
    readonly List<string> _ids = [];
    readonly List<string> _labels = [];

    // The row of the zone you're in (it can't be picked), or -1, and the last real pick to go back to
    int _current = -1;
    int _picked = -1;
    bool _filling;

    /// <summary>Creates the box (filled by <see cref="Show"/>).</summary>
    public TimeZoneComboBox()
    {
        DefaultStyleKey     = typeof(ComboBox);
        HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        SelectionChanged += OnSelectionChanged;
        TextSubmitted    += OnTextSubmitted;
    }

    /// <summary>The user picked a zone (its IANA ID). Not raised by <see cref="Show"/>.</summary>
    public event EventHandler<string>? ZoneChanged;

    /// <summary>The picked zone's IANA ID, or null.</summary>
    public string? ZoneId => SelectedIndex >= 0 && SelectedIndex < _ids.Count ? _ids[SelectedIndex] : null;

    /// <summary>
    /// Lists every zone with offsets at <paramref name="now"/> and picks <paramref name="zoneId"/>. A known zone the
    /// list doesn't hold (another IANA name for one of its zones) gets its own row at the top. The row of
    /// <paramref name="current"/> (the zone you're in) reads "· Current" and can't be picked.
    /// </summary>
    public void Show(string zoneId, DateTimeOffset now, TimeZoneInfo? current = null)
    {
        _filling = true;
        _ids.Clear();
        _labels.Clear();
        foreach (var (id, label) in TimeZoneCatalog.All(now))
        {
            _ids.Add(id);
            _labels.Add(label);
        }

        if (!_ids.Contains(zoneId) && TimeZoneCatalog.IsKnown(zoneId))
        {
            _ids.Insert(0, zoneId);
            _labels.Insert(0, TimeZoneCatalog.ListLabel(zoneId, now));
        }

        _current = current is null ? -1 : _ids.FindIndex(id => TimeZoneCatalog.IsSameZone(id, current));
        if (_current >= 0)
        {
            _labels[_current] += CurrentMark;
        }

        ItemsSource   = _labels.ToList();
        SelectedIndex = _picked = _ids.IndexOf(zoneId);
        _filling      = false;
    }

    // What the zone you're in reads in the list
    const string CurrentMark = " · Current";

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        // The Zone You're In Can't Be Picked: the previous pick comes back (it may itself be the current zone, shown as set)
        if (SelectedIndex == _current && _picked != _current)
        {
            _filling      = true;
            SelectedIndex = _picked;
            _filling      = false;
            return;
        }

        _picked = SelectedIndex;
        if (ZoneId is { } id)
        {
            ZoneChanged?.Invoke(this, id);
        }
    }

    // Typed text picks the best match (the search boxes' ranking, then any row holding the text); no match puts the
    // picked zone's row back. Handled either way, so typed text never becomes a value of its own
    void OnTextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        args.Handled = true;
        var text = args.Text.Trim();
        if (text.Length > 0)
        {
            var found = TimeZoneCatalog.Search(text, DateTimeOffset.UtcNow, 1) is [var best] && _ids.IndexOf(best.Id) is >= 0 and var ranked
                ? ranked
                : _labels.FindIndex(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

            // Typing the zone you're in can't pick it either
            if (found >= 0 && found != _current)
            {
                SelectedIndex = found;
            }
        }

        Text = SelectedIndex >= 0 ? _labels[SelectedIndex] : "";
    }
}
