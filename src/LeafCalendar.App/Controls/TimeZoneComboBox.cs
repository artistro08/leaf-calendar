using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The stock dropdown of every time zone, written as Windows writes them ("(UTC-05:00) New York", west to east). Used
/// for the event's zone in the editor, the share zone, and Settings' primary zone.
/// </summary>
/// <remarks>
/// The items are plain strings (a list of Core records can't go to WinRT under Native AOT; typing to search only works on
/// string rows) and the pick is read by index from our own list of IDs. Every zone can be picked, the one already set
/// included, so a box can always go back to where it started. With <c>IsEditable</c> on, typed text is matched like the old search boxes (a city,
/// an alias such as NYC, or any part of a row) and only a zone from the list is taken. While typing, the best match is
/// highlighted in the open list (the typed text stays); Enter or leaving the box takes it, and Esc goes back to the zone
/// that was picked.
/// The box has no XAML file of its own: a XAML file whose root is a ComboBox subclass fails to parse when the
/// box is built (XamlParseException), which kept the calendar page from opening. Rows are the stock string rows.
/// </remarks>
public sealed partial class TimeZoneComboBox : ComboBox
{
    // The rows' IANA IDs, in the box's order
    private readonly List<string> _ids = [];
    private readonly List<string> _labels = [];

    private bool _filling;

    // The row the user last picked (a highlight while typing isn't a pick until it's submitted)
    private int _picked = -1;

    // The box's text field, and whether the box is moving the highlight itself (its own text changes then)
    private TextBox? _editable;
    private bool _highlighting;

    /// <summary>Creates the box (filled by <see cref="Show"/>).</summary>
    public TimeZoneComboBox()
    {
        // The app's own ComboBox style, the one a plain ComboBox takes (the framework's default for the type draws the
        // dropdown arrow 4 DIP further right)
        DefaultStyleKey = typeof(ComboBox);
        Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["DefaultComboBoxStyle"];
        HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;

        SelectionChanged += OnSelectionChanged;
        DropDownClosed += OnDropDownClosed;
        TextSubmitted += OnTextSubmitted;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>The user picked a zone (its IANA ID). Not raised by <see cref="Show"/>.</summary>
    public event EventHandler<string>? ZoneChanged;

    /// <summary>The picked zone's IANA ID (not one highlighted while typing), or null.</summary>
    public string? ZoneId => _picked >= 0 && _picked < _ids.Count ? _ids[_picked] : null;

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

        ItemsSource = _labels.ToList();
        SelectedIndex = _picked = _ids.IndexOf(zoneId);
        _filling = false;
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_editable is not null)
        {
            _editable.TextChanged -= OnEditableTextChanged;
        }

        _editable = GetTemplateChild("EditableText") as TextBox;
        if (_editable is not null)
        {
            _editable.TextChanged += OnEditableTextChanged;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && !_highlighting)
        {
            Pick();
        }
    }

    // The selected row becomes the pick, said once
    private void Pick()
    {
        if (SelectedIndex != _picked && SelectedIndex >= 0 && SelectedIndex < _ids.Count)
        {
            _picked = SelectedIndex;
            ZoneChanged?.Invoke(this, _ids[_picked]);
        }
    }

    // Typing highlights the best match in the open list without picking it: the selection moves (which also rewrites the
    // field), then the typed text and the caret are put back
    private void OnEditableTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_highlighting || _filling || _editable is not { } field)
        {
            return;
        }

        var text = field.Text;
        if (text.Trim().Length == 0 || (SelectedIndex >= 0 && text == _labels[SelectedIndex]) || BestMatch(text) is not (>= 0 and var best))
        {
            return;
        }

        _highlighting = true;
        IsDropDownOpen = true;
        SelectedIndex = best;
        field.Text = text;
        field.SelectionStart = text.Length;
        _highlighting = false;
    }

    // The list closing on the highlighted row (a click on it raises no selection change) picks it; Esc put the pick back first
    private void OnDropDownClosed(object? sender, object e) => Pick();

    // Esc puts the picked zone back (the highlight was never a pick)
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && SelectedIndex != _picked)
        {
            _highlighting = true;
            SelectedIndex = _picked;
            _highlighting = false;
        }
    }

    // The search boxes' ranking first, then any row holding the text; -1 when nothing matches
    private int BestMatch(string text)
    {
        text = text.Trim();
        return TimeZoneCatalog.Search(text, DateTimeOffset.UtcNow, 1) is [var best] && _ids.IndexOf(best.Id) is >= 0 and var ranked
            ? ranked
            : _labels.FindIndex(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    // Typed text picks the best match (the search boxes' ranking, then any row holding the text); no match puts the
    // picked zone's row back. Handled either way, so typed text never becomes a value of its own
    private void OnTextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        args.Handled = true;
        if (args.Text.Trim().Length > 0 && BestMatch(args.Text) is >= 0 and var found)
        {
            _highlighting = true;
            SelectedIndex = found;
            _highlighting = false;
            Pick();
        }
        else
        {
            _highlighting = true;
            SelectedIndex = _picked;
            _highlighting = false;
        }

        Text = SelectedIndex >= 0 ? _labels[SelectedIndex] : "";
    }
}
