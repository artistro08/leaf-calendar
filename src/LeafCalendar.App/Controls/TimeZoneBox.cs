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
/// At rest the box shows the picked zone's row. Focusing it selects that text (so typing replaces it) and lists every
/// zone, so you can still browse. Typing filters the list to the zones that match, ranked like the old search boxes (a
/// city, an alias such as NYC or PHX, a Windows name, then any row holding the text, then a small typo such as
/// "Pheonix"). Like the guest box, the rows are only replaced while typing, so focus and the caret stay where they are.
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

    private DateTimeOffset _now;

    // The zone the user last picked (a row on show isn't a pick until it's clicked or submitted)
    private string? _picked;

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

        if (!_allIds.Contains(zoneId) && TimeZoneCatalog.IsKnown(zoneId))
        {
            _allIds.Insert(0, zoneId);
            _allLabels.Insert(0, TimeZoneCatalog.ListLabel(zoneId, now));
        }

        _picked = _allIds.Contains(zoneId) ? zoneId : null;
        ShowPicked();
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

    // Shows the rows of these zones (a new list each time: the box keeps the one it's given)
    private void ShowRows(IEnumerable<string> ids) =>
        _box.ItemsSource = ids.Select(id => _allLabels[_allIds.IndexOf(id)]).ToList();

    // Coming into the box lists every zone and selects the text, so typing replaces it. The select-all runs again once
    // the click that focused the box has placed its caret (unless a letter was typed since)
    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (_focused)
        {
            return;
        }

        _focused = true;
        ShowRows(_allIds);
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

    // The picked zone's row (empty when none is picked)
    private string PickedText() => _picked is null ? "" : _allLabels[_allIds.IndexOf(_picked)];

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
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        var ids = _box.Text.Trim().Length == 0 ? _allIds : Matches(_box.Text);
        ShowRows(ids);
        _box.IsSuggestionListOpen = ids.Count > 0;
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

    // A clicked row becomes the pick
    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        Submit(args.ChosenSuggestion as string ?? args.QueryText);

    // A row's own text (one clicked, or one the arrow keys moved to) or the best match for typed text becomes the pick,
    // said once; no match puts the picked zone back
    private void Submit(string text)
    {
        var index = _allLabels.IndexOf(text);
        var id = index >= 0 ? _allIds[index]
            : text.Trim().Length > 0 && Matches(text) is [var best, ..] ? best
            : null;
        if (id is not null && id != _picked)
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
