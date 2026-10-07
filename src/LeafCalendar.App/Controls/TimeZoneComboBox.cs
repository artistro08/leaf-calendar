using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The stock dropdown of every time zone, written as Windows writes them ("(UTC-05:00) New York", west to east). Used
/// for the event's zone in the editor, the share zone, and Settings' primary zone.
/// </summary>
/// <remarks>
/// The items are plain strings (a list of Core records can't go to WinRT under Native AOT) and the pick is read by index
/// from our own list of the IDs on show. Every zone can be picked, the one already set included, so a box can always go
/// back to where it started. With <c>IsEditable</c> on, typing filters the list to the zones that match, ranked like the
/// old search boxes (a city, an alias such as NYC or PHX, a Windows name, a small typo such as "Pheonix", then any row
/// holding the text). The typed text stays exactly as typed: the stock "type to jump" is off, and nothing is highlighted
/// while typing. Enter or a click picks a row, Enter on typed text or leaving the box takes the best match (no match puts
/// the picked zone back), Esc goes back to the zone that was picked, and clearing the text shows every zone again.
/// The box has no XAML file of its own: a XAML file whose root is a ComboBox subclass fails to parse when the
/// box is built (XamlParseException), which kept the calendar page from opening. Rows are the stock string rows.
/// </remarks>
public sealed partial class TimeZoneComboBox : ComboBox
{
    // Every zone's IANA ID and row, in the box's order
    private readonly List<string> _allIds = [];
    private readonly List<string> _allLabels = [];

    // The rows on show (every zone, or the ones matching the typed text), and whether they need putting back to every zone
    private List<string> _ids = [];
    private List<string> _labels = [];
    private bool _filtered;

    private DateTimeOffset _now;

    // The zone the user last picked (a row on show isn't a pick until it's clicked or submitted)
    private string? _picked;

    // The box's text field, and whether the box is changing its own rows or text (no pick, no filtering then)
    private TextBox? _editable;
    private bool _filling;

    /// <summary>Creates the box (filled by <see cref="Show"/>).</summary>
    public TimeZoneComboBox()
    {
        // The app's own ComboBox style, the one a plain ComboBox takes (the framework's default for the type draws the
        // dropdown arrow 4 DIP further right)
        DefaultStyleKey = typeof(ComboBox);
        Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["DefaultComboBoxStyle"];
        HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;

        // Stock "type to jump" matches from the start of a row ("(UTC-07:00)...") and completes the text; the box filters instead
        IsTextSearchEnabled = false;

        SelectionChanged += OnSelectionChanged;
        DropDownClosed += OnDropDownClosed;
        TextSubmitted += OnTextSubmitted;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>The user picked a zone (its IANA ID). Not raised by <see cref="Show"/>.</summary>
    public event EventHandler<string>? ZoneChanged;

    /// <summary>The picked zone's IANA ID (not one merely on show while typing), or null.</summary>
    public string? ZoneId => _picked;

    /// <summary>
    /// Lists every zone with offsets at <paramref name="now"/> and picks <paramref name="zoneId"/>. A known zone the
    /// list doesn't hold (another IANA name for one of its zones) gets its own row at the top.
    /// </summary>
    public void Show(string zoneId, DateTimeOffset now)
    {
        _now = now;
        _allIds.Clear();
        _allLabels.Clear();
        foreach (var (id, label) in TimeZoneCatalog.All(now))
        {
            _allIds.Add(id);
            _allLabels.Add(label);
        }

        if (!_allIds.Contains(zoneId) && TimeZoneCatalog.IsKnown(zoneId))
        {
            _allIds.Insert(0, zoneId);
            _allLabels.Insert(0, TimeZoneCatalog.ListLabel(zoneId, now));
        }

        _picked = _allIds.Contains(zoneId) ? zoneId : null;
        _filtered = true;
        ShowPicked();
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

    // Every zone on show again, the picked one selected and its row in the text. The rows are only replaced when typing
    // changed them: new rows while the box has focus take focus back, which kept Tab from leaving it
    private void ShowPicked()
    {
        _filling = true;
        if (_filtered)
        {
            _ids = [.. _allIds];
            _labels = [.. _allLabels];
            ItemsSource = _labels.ToList();
            _filtered = false;
        }

        var index = _picked is null ? -1 : _ids.IndexOf(_picked);
        if (SelectedIndex != index)
        {
            SelectedIndex = index;
        }

        var text = index >= 0 ? _labels[index] : "";
        if (IsEditable && Text != text)
        {
            Text = text;
        }

        _filling = false;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling)
        {
            Pick();
        }
    }

    // The selected row becomes the pick, said once; then every zone shows again (once the list has finished closing)
    private void Pick()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _ids.Count)
        {
            return;
        }

        var id = _ids[SelectedIndex];
        if (id != _picked)
        {
            _picked = id;
            ZoneChanged?.Invoke(this, id);
        }

        if (_filtered)
        {
            DispatcherQueue.TryEnqueue(ShowPicked);
        }
    }

    // Typing shows only the matching zones (every zone for empty text). The text and caret stay as typed: new rows can
    // rewrite the field, so both are put back
    private void OnEditableTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling || _editable is not { } field)
        {
            return;
        }

        // A row's own text (the pick, or a row the arrow keys moved to) isn't a search
        var text = field.Text;
        if (_labels.Contains(text))
        {
            return;
        }

        var caret = field.SelectionStart;
        var ids = text.Trim().Length == 0 ? [.. _allIds] : Matches(text);

        // The same rows as before (a trailing space, say) stay as they are
        if (_filtered && ids.SequenceEqual(_ids))
        {
            IsDropDownOpen = ids.Count > 0;
            return;
        }

        _filling = true;
        _ids = ids;
        _labels = [.. ids.Select(id => _allLabels[_allIds.IndexOf(id)])];
        ItemsSource = _labels.ToList();
        _filtered = true;
        SelectedIndex = -1;
        if (field.Text != text)
        {
            field.Text = text;
        }

        field.SelectionStart = Math.Min(caret, text.Length);
        field.SelectionLength = 0;
        IsDropDownOpen = ids.Count > 0;
        _filling = false;
    }

    // The search boxes' exact and part-of-a-name matches (cities, aliases, Windows names) first, then any row holding the
    // text ("CEST" in Berlin's row), then small typos
    private List<string> Matches(string text)
    {
        text = text.Trim();
        return [.. TimeZoneCatalog.Search(text, _now, int.MaxValue, typos: false)
            .Select(c => c.Id)
            .Where(_allIds.Contains)
            .Concat(_allIds.Where((_, i) => _allLabels[i].Contains(text, StringComparison.OrdinalIgnoreCase)))
            .Concat(TimeZoneCatalog.Search(text, _now, int.MaxValue).Select(c => c.Id).Where(_allIds.Contains))
            .Distinct()];
    }

    // The list closing on a selected row picks it (a click on the row already selected raises no selection change)
    private void OnDropDownClosed(object? sender, object e) => Pick();

    // Esc goes back to the picked zone (typed text and the rows it showed were never a pick). An open list is closed here,
    // after its rows are put back (new rows while the stock close runs crash the framework), and the key goes no further,
    // so Esc in an open list closes only the list. Enter on typed text takes its best match (the open list, with no row
    // selected, would otherwise just close); Ctrl+Enter is left to the page (the editor saves with it)
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (IsDropDownOpen)
            {
                e.Handled = true;
                ShowPicked();
                IsDropDownOpen = false;
            }
            else
            {
                ShowPicked();
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && !KeyState.IsDown(Windows.System.VirtualKey.Control) && _editable is { } field && !_labels.Contains(field.Text))
        {
            e.Handled = true;
            Submit(field.Text);
            IsDropDownOpen = false;
        }
    }

    // Typed text submitted (leaving the box with it) takes its best match. Handled, so typed text never becomes a value of
    // its own. Every zone shows again once focus has moved on (new rows mid-move would pull focus back)
    private void OnTextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        args.Handled = true;
        Submit(args.Text, later: true);
    }

    // Typed text picks the best match; no match puts the picked zone back
    private void Submit(string text, bool later = false)
    {
        if (text.Trim().Length > 0 && Matches(text) is [var best, ..] && best != _picked)
        {
            _picked = best;
            ZoneChanged?.Invoke(this, best);
        }

        if (later)
        {
            DispatcherQueue.TryEnqueue(ShowPicked);
        }
        else
        {
            ShowPicked();
        }
    }
}
