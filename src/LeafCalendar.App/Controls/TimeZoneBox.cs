using LeafCalendar.Core.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Controls;

/// <summary>
/// A stock search box (an <see cref="AutoSuggestBox"/>, like the guest box) for picking a time zone, its rows written as Windows writes them ("(UTC-05:00) New York", west to
/// east). Used for the event's zone in the editor, the share zone, and Settings' primary zone.
/// </summary>
/// <remarks>
/// At rest the box shows the picked zone's row, or the city's row when a city was picked. Focusing it selects that text (so typing replaces it) and lists every
/// zone, so you can still browse. Typing filters the list to the zones that match, ranked like the old search boxes (a
/// city, an alias such as NYC or PHX, a Windows name, then any row holding the text, then a small typo such as
/// "Pheonix"), with built-in cities among them, each with its zone's row ("Austin, Texas, United States · (UTC-05:00)
/// Chicago (CDT)"); picking a city picks its zone. Like the guest box, the rows are only replaced while typing, so focus and the caret stay where they are.
/// Enter or a click picks a row; Enter on typed text takes the best match (no match puts the picked zone back). Esc, or
/// leaving the box without picking, puts the picked zone back; Esc in an open list closes only the list.
/// The rows are plain strings (a list of Core records can't go to WinRT under Native AOT), and a pick is found by its row
/// text in our own list. The search box is sealed, so this control hosts one, built in code (a XAML file whose root
/// subclasses a stock control failed to parse), and hands it the name and automation ID set on the control.
/// </remarks>
public sealed partial class TimeZoneBox : UserControl
{
    private readonly AutoSuggestBox _box = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    // Every zone's IANA ID and row, in the box's order
    private readonly List<string> _allIds = [];
    private readonly List<string> _allLabels = [];

    // The city rows of the newest search, with their zones (kept until the next search, so a click on one that lands
    // after focus left the box still finds its zone)
    private Dictionary<string, string> _cityRows = [];

    private DateTimeOffset _now;

    // The zone the user last picked (a row on show isn't a pick until it's clicked or submitted)
    private string? _picked;

    // The city row picked for it, shown in its place (null for a zone row, or a zone set by Show)
    private string? _pickedRow;

    // The box's text field and suggestion list, and whether focus is in the box (set once per visit)
    private TextBox? _field;
    private Popup? _popup;
    private bool _focused;

    /// <summary>Creates the box (filled by <see cref="Show"/>).</summary>
    public TimeZoneBox()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Content = _box;

        Loaded += OnLoaded;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        _box.TextChanged += OnTextChanged;
        _box.QuerySubmitted += OnQuerySubmitted;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>The label above the box.</summary>
    public object? Header
    {
        get => _box.Header;
        set => _box.Header = value;
    }

    /// <summary>Whether the list of zones is open (Esc then closes only the list).</summary>
    public bool IsSuggestionListOpen => _box.IsSuggestionListOpen;

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

        AddRow(zoneId);
        _picked = _allIds.Contains(zoneId) ? zoneId : null;
        _pickedRow = null;
        ShowPicked();
    }

    // A known zone the list doesn't hold (another IANA name for one of its zones) gets its own row at the top
    private void AddRow(string zoneId)
    {
        if (!_allIds.Contains(zoneId) && TimeZoneCatalog.IsKnown(zoneId))
        {
            _allIds.Insert(0, zoneId);
            _allLabels.Insert(0, TimeZoneCatalog.ListLabel(zoneId, _now));
        }
    }

    // The search box takes the name and automation ID set on the control (screen readers and UI tests find it, and its
    // text field, by them), and its template parts are found once it's built
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (AutomationProperties.GetAutomationId(this) is { Length: > 0 } id)
        {
            AutomationProperties.SetAutomationId(_box, id);
            AutomationProperties.SetAutomationId(this, "");
        }

        if (AutomationProperties.GetName(this) is { Length: > 0 } name)
        {
            AutomationProperties.SetName(_box, name);
            AutomationProperties.SetName(this, "");
        }

        _field ??= TemplatePart.Find<TextBox>(_box, "TextBox");
        _popup ??= TemplatePart.Find<Popup>(_box, "SuggestionsPopup");
    }

    // The picked zone's row in the text, and the list closed
    private void ShowPicked()
    {
        _box.IsSuggestionListOpen = false;
        var text = PickedText();
        _box.Text = text;

        // The search box's own text can still hold the picked row while its field shows typed text, so the field is set too
        if (_field is { } field && field.Text != text)
        {
            field.Text = text;
        }
    }

    // Shows these rows (a new list each time: the box keeps the one it's given)
    private void ShowRows(List<string> rows) => _box.ItemsSource = rows;

    // Coming into the box lists every zone and selects the text, so typing replaces it. The select-all runs again once
    // the click that focused the box has placed its caret (unless a letter was typed since)
    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (_focused)
        {
            return;
        }

        _focused = true;
        TimeZoneCatalog.WarmCities();
        ShowRows([.. _allLabels]);
        _box.IsSuggestionListOpen = _allIds.Count > 0;
        _field?.SelectAll();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_focused && _field is { } field && field.Text == PickedText())
            {
                field.SelectAll();
            }
        });
    }

    // The picked city's row, else the picked zone's row (empty when none is picked)
    private string PickedText() => _picked is null ? "" : _pickedRow ?? Row(_picked);

    // Leaving the box without picking puts the picked zone back. Checked once focus has landed, as focus moving inside
    // the box (or into its list) raises this too
    private void OnLostFocus(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_focused && !FocusIsWithin())
            {
                _focused = false;
                ShowPicked();
            }
        });

    private bool FocusIsWithin()
    {
        if (XamlRoot is null)
        {
            return false;
        }

        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, this) || (_popup?.Child is { } list && ReferenceEquals(element, list)))
            {
                return true;
            }
        }

        return false;
    }

    // Typing shows only the matching zones (every zone for empty text). Only the rows change, so the text, the caret and
    // focus stay as typed; a row the arrow keys moved to isn't a search
    private void OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // The box writing the picked row back into its text field also reads as user input; that isn't a search, and
        // neither is any change while the box doesn't have focus
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || !_focused || _box.Text == PickedText())
        {
            return;
        }

        var rows = _box.Text.Trim().Length == 0 ? [.. _allLabels] : Matches(_box.Text);
        ShowRows(rows);
        _box.IsSuggestionListOpen = rows.Count > 0;
    }

    // The search boxes' exact and part-of-a-name matches (cities, aliases, Windows names, and cities with their zone's
    // row: "Austin, Texas, United States · (UTC-05:00) Chicago (CDT)") first, then any zone row holding the text ("CEST"
    // in Berlin's row), then small typos. The cities' rows are kept with their zones, so a pick can find one
    private List<string> Matches(string text)
    {
        text = text.Trim();
        var cities = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> rows = [];
        foreach (var choice in TimeZoneCatalog.Search(text, _now, int.MaxValue, typos: false))
        {
            if (choice.Place is null)
            {
                rows.AddRange(_allIds.Contains(choice.Id) ? [Row(choice.Id)] : []);
            }
            else
            {
                var row = $"{choice.City}, {choice.Place} · {TimeZoneCatalog.ListLabel(choice.Id, _now)}";
                cities[row] = choice.Id;
                rows.Add(row);
            }
        }

        _cityRows = cities;
        return [.. rows
            .Concat(_allLabels.Where(label => label.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .Concat(TimeZoneCatalog.Search(text, _now, int.MaxValue).Where(c => c.Place is null && _allIds.Contains(c.Id)).Select(c => Row(c.Id)))
            .Distinct()];
    }

    private string Row(string id) => _allLabels[_allIds.IndexOf(id)];

    // A row's zone: a zone row's own, or a city's (its zone's row in the list, or one on the same Windows zone, else a new
    // row at the top), or null
    private string? ZoneOf(string row)
    {
        var index = _allLabels.IndexOf(row);
        if (index >= 0)
        {
            return _allIds[index];
        }

        if (!_cityRows.TryGetValue(row, out var zoneId))
        {
            return null;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var id = _allIds.Contains(zoneId) ? zoneId : _allIds.Find(i => TimeZoneCatalog.IsSameZone(i, zone)) ?? zoneId;
        AddRow(id);
        return id;
    }

    // A clicked row becomes the pick
    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        Submit(args.ChosenSuggestion as string ?? args.QueryText);

    // A row's own text (one clicked, or one the arrow keys moved to) or the best match for typed text becomes the pick,
    // said once, and a city's row stays in the box; no match puts the picked zone back
    private void Submit(string text)
    {
        var row = ZoneOf(text) is null && text.Trim().Length > 0 && Matches(text) is [var best, ..] ? best : text;
        if (ZoneOf(row) is not { } id)
        {
            ShowPicked();
            return;
        }

        _pickedRow = _cityRows.ContainsKey(row) ? row : null;
        if (id != _picked)
        {
            _picked = id;
            ZoneChanged?.Invoke(this, id);
        }

        ShowPicked();
    }

    // Esc goes back to the picked zone; with the list open the key goes no further, so Esc there closes only the list.
    // Enter submits the text here, so typed text with no rows on show is taken (or put back) too; Ctrl+Enter is left to
    // the page (the editor saves with it)
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = _box.IsSuggestionListOpen;
            ShowPicked();
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && !KeyState.IsDown(Windows.System.VirtualKey.Control))
        {
            // Once the key is done (text set during it doesn't reach the field)
            e.Handled = true;
            var text = _field?.Text ?? _box.Text;
            DispatcherQueue.TryEnqueue(() => Submit(text));
        }
    }
}
