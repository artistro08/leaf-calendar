using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The stock dropdown of every time zone, written as Windows writes them ("(UTC-05:00) New York", west to east). Used
/// for the event's zone in the editor, the share zone, and Settings' primary zone.
/// </summary>
/// <remarks>
/// The items are plain strings (a list of Core records can't go to WinRT under Native AOT) and the pick is read by
/// index from our own list of IDs. With <c>IsEditable</c> on, typed text is matched like the old search boxes (a city,
/// an alias such as NYC, or any part of a row) and only a zone from the list is taken.
/// </remarks>
public sealed partial class TimeZoneComboBox : ComboBox
{
    // The rows' IANA IDs, in the box's order
    readonly List<string> _ids = [];
    readonly List<string> _labels = [];
    bool _filling;

    /// <summary>Creates the box (filled by <see cref="Show"/>).</summary>
    public TimeZoneComboBox()
    {
        DefaultStyleKey = typeof(ComboBox);
        InitializeComponent();
        SelectionChanged += OnSelectionChanged;
        TextSubmitted    += OnTextSubmitted;
    }

    /// <summary>The user picked a zone (its IANA ID). Not raised by <see cref="Show"/>.</summary>
    public event EventHandler<string>? ZoneChanged;

    /// <summary>The picked zone's IANA ID, or null.</summary>
    public string? ZoneId => SelectedIndex >= 0 && SelectedIndex < _ids.Count ? _ids[SelectedIndex] : null;

    /// <summary>
    /// Lists every zone with offsets at <paramref name="now"/> and picks <paramref name="zoneId"/>. A known zone the
    /// list doesn't hold (another IANA name for one of its zones) gets its own row at the top.
    /// </summary>
    public void Show(string zoneId, DateTimeOffset now)
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

        ItemsSource   = _labels.ToList();
        SelectedIndex = _ids.IndexOf(zoneId);
        _filling      = false;
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && ZoneId is { } id)
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

            if (found >= 0)
            {
                SelectedIndex = found;
            }
        }

        Text = SelectedIndex >= 0 ? _labels[SelectedIndex] : "";
    }
}
