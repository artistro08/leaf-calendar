using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Search;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace LeafCalendar.App.Views;

/// <summary>What a command-menu row does when run.</summary>
public enum CommandRowKind
{
    /// <summary>Opens a stored event.</summary>
    Event,

    /// <summary>Runs an action from <see cref="CommandCatalog"/>.</summary>
    Action,

    /// <summary>Goes to a typed date.</summary>
    Date,

    /// <summary>A section title ("Events", "Actions", "Go to"); never selected or run.</summary>
    Header,
}

/// <summary>One command-menu row (an App class, so a WinRT list never holds Core types).</summary>
public sealed class CommandRow
{
    private CommandRow(CommandRowKind kind, string title, string detail, string keys, string glyph, string color, string automationId)
    {
        Kind = kind;
        Title = title;
        Detail = detail;
        Keys = keys;
        Glyph = glyph;
        Color = color;
        AutomationId = automationId;
        EventVisibility = kind == CommandRowKind.Event ? Visibility.Visible : Visibility.Collapsed;
        ActionVisibility = kind is CommandRowKind.Action or CommandRowKind.Date ? Visibility.Visible : Visibility.Collapsed;
        KeysVisibility = keys.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderVisibility = kind == CommandRowKind.Header ? Visibility.Visible : Visibility.Collapsed;
        RowVisibility = kind == CommandRowKind.Header ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What the row does.</summary>
    public CommandRowKind Kind { get; }

    /// <summary>The event's title, the action's name, or "Go to Tue, Oct 20".</summary>
    public string Title { get; }

    /// <summary>An event's day, time, and where the words matched; empty for actions and dates.</summary>
    public string Detail { get; }

    /// <summary>The action's shortcut, or empty.</summary>
    public string Keys { get; }

    /// <summary>The action's glyph (Segoe Fluent Icons).</summary>
    public string Glyph { get; }

    /// <summary>The event's color (hex).</summary>
    public string Color { get; }

    /// <summary><c>SearchResult_&lt;eventId&gt;</c>, <c>CommandResult_&lt;id&gt;</c>, or <c>CommandResult_date</c>.</summary>
    public string AutomationId { get; }

    /// <summary>Shows the color swatch (events).</summary>
    public Visibility EventVisibility { get; }

    /// <summary>Shows the glyph (actions and dates).</summary>
    public Visibility ActionVisibility { get; }

    /// <summary>The space between the title and a detail (an em space; none without a detail).</summary>
    public string DetailGap => Detail.Length > 0 ? " " : "";

    /// <summary>Shows the shortcut chip (only when there's a shortcut).</summary>
    public Visibility KeysVisibility { get; }

    /// <summary>Shows the section title (headers).</summary>
    public Visibility HeaderVisibility { get; }

    /// <summary>Shows the row (everything but headers).</summary>
    public Visibility RowVisibility { get; }

    /// <summary>What the footer calls the row: "Event", "Action", or "Date"; empty for headers.</summary>
    public string KindLabel => Kind switch
    {
        CommandRowKind.Event => "Event",
        CommandRowKind.Action => "Action",
        CommandRowKind.Date => "Date",
        _ => "",
    };

    /// <summary>What Enter does to the row, for the footer: "Open" an event, "Run" an action, "Go" to a date; empty for headers.</summary>
    public string Verb => Kind switch
    {
        CommandRowKind.Event => "Open",
        CommandRowKind.Action => "Run",
        CommandRowKind.Date => "Go",
        _ => "",
    };

    /// <summary>The new event's title on the "Create event “…”" row offered when nothing matches; empty on every other row.</summary>
    public string EventTitle { get; private init; } = "";

    /// <summary>The event, for <see cref="CommandRowKind.Event"/>.</summary>
    public SearchHit? Hit { get; private init; }

    /// <summary>The action, for <see cref="CommandRowKind.Action"/>.</summary>
    public CommandItem? Item { get; private init; }

    /// <summary>The date, for <see cref="CommandRowKind.Date"/>.</summary>
    public DateOnly? Date { get; private init; }

    /// <summary>A found event: "Mon, Oct 12 · 9:00 AM", plus " · in location" (or guests, description) when the title didn't match.</summary>
    public static CommandRow ForHit(SearchHit hit, DateOnly today, TimeZoneInfo zone, bool use24h)
    {
        var day = hit.IsAllDay ? DateOnly.FromDateTime(hit.Start.UtcDateTime) : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(hit.Start, zone).DateTime);
        var time = hit.IsAllDay ? "All day" : TimeLabels.TimeOfDay(hit.Start, zone, use24h);
        var where = hit.Field switch
        {
            SearchField.Location => " · in location",
            SearchField.Guest => " · in guests",
            SearchField.Description => " · in description",
            _ => "",
        };

        return new(CommandRowKind.Event, hit.Title, $"{DateQuery.Label(day, today)} · {time}{where}", "", "", hit.Color, $"SearchResult_{hit.EventId}") { Hit = hit };
    }

    /// <summary>An action from the catalog.</summary>
    public static CommandRow ForAction(CommandItem item) =>
        new(CommandRowKind.Action, item.Title, "", item.Keys, GlyphFor(item.Id), "", $"CommandResult_{item.Id}") { Item = item };

    /// <summary>Create event, with the typed words as the new event's title (offered when nothing matches).</summary>
    public static CommandRow ForNewEvent(string title) =>
        new(CommandRowKind.Action, $"Create event “{title}”", "", "", GlyphFor("create-event"), "", "CommandResult_create-event-titled")
        {
            Item = CommandCatalog.All.Single(c => c.Id == "create-event"),
            EventTitle = title,
        };

    /// <summary>A section title.</summary>
    public static CommandRow ForHeader(string title) =>
        new(CommandRowKind.Header, title, "", "", "", "", "CommandHeader_" + title.Replace(' ', '-').ToLowerInvariant());

    /// <summary>A typed date ("Go to Tue, Oct 20", "Go to today").</summary>
    public static CommandRow ForDate(DateOnly date, DateOnly today) =>
        new(CommandRowKind.Date, DateQuery.GoTo(date, today), "", "", "", "", "CommandResult_date") { Date = date };

    // The design standard's glyphs where it names one, Segoe Fluent Icons otherwise
    private static string GlyphFor(string id) => id switch
    {
        "create-event" => "",
        "go-to-date" or "today" => "",
        "join" => "",
        "overlay" or "meet-with" => "",
        "time-travel" => "",
        "share" => "",
        "theme-dark" or "theme-light" => "",
        "shortcuts" or "settings-shortcuts" => "",
        "settings-about" => "",
        "sync" => "",
        "back" => "",
        "forward" => "",
        "quit" => "\uE7E8",
        _ when id.StartsWith("settings", StringComparison.Ordinal) => "",
        _ when id.StartsWith("view-", StringComparison.Ordinal) => "",
        _ => "",
    };
}

/// <summary>
/// The command menu (Ctrl+K, Ctrl+F, /, the title bar's search icon): one box that searches every stored event, the
/// app's actions, and typed dates (spec 6.6). Up and Down move the selection while focus stays in the box; Enter runs
/// the selected row, and Alt+Enter on an event also moves the calendar to it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A control isn't disposable; the running search is canceled when the menu unloads (the flyout closes).")]
public sealed partial class CommandMenu : UserControl
{
    private readonly CalendarViewModel _vm;
    private readonly Action<CommandRow, bool> _run;
    private readonly LatestSearch<IReadOnlyList<SearchHit>> _search = new();
    private readonly Dictionary<CommandRow, UIElement> _containers = [];
    private List<CommandRow> _rows = [];

    // Jump to date: the chip shows, the box asks for a date, and only the date row is offered
    private bool _dateMode;

    /// <summary>Creates the menu; <paramref name="run"/> gets the picked row and whether to jump (Alt+Enter).</summary>
    public CommandMenu(CalendarViewModel vm, Action<CommandRow, bool> run)
    {
        _vm = vm;
        _run = run;
        InitializeComponent();
        ScrollIndicator.ShowOnHover(ResultsScroll);

        // Closing Stops A Search Still Running
        Unloaded += (_, _) => _search.Cancel();
    }

    /// <summary>x:Bind helper: the swatch brush for a hex color.</summary>
    public static Brush Brush(string hex) => LeafBrushes.FromHex(hex);

    /// <summary>x:Bind helper: a row's shortcut, each key on its own cap (nothing for a row without one).</summary>
    public static UIElement? Legend(string keys) => keys.Length == 0 ? null : ShortcutLegend.Build(keys);

    /// <summary>
    /// The results list's tallest: the default actions under their header (8 + 28 + 8 × 44 + 8 = 396) with room to
    /// spare, so the menu opens without a scroll.
    /// </summary>
    public const double ResultsMaxHeight = 416;

    /// <summary>Caps the results list at the given height (at most <see cref="ResultsMaxHeight"/>), so the menu never runs past a short window.</summary>
    public void LimitResultsHeight(double height) => ResultsScroll.MaxHeight = Math.Min(ResultsMaxHeight, height);

    // What the box asks for: anything, or (Jump to date, whose chip already names it) a date in words
    private const string SearchPrompt = "Search events, or type a command or a date";
    private const string DatePrompt = "Try nov 5th, 10 weeks, next fri, or 3 days ago";

    /// <summary>Clears the box and shows the default actions.</summary>
    public void Reset()
    {
        _search.Cancel();
        _dateMode = false;
        ModeChip.Visibility = Visibility.Collapsed;
        SearchGlyph.Visibility = Visibility.Visible;
        CommandSearchBox.Text = "";
        CommandSearchBox.PlaceholderText = SearchPrompt;
        Show(null, [], [.. CommandCatalog.Defaults.Select(CommandRow.ForAction)]);
    }

    /// <summary>
    /// Jump to date: the chip shows the action in place of the search glyph, the box asks for a date in words, and only
    /// the date row is offered. Backspace on the empty box leaves the mode.
    /// </summary>
    public void AskForDate()
    {
        _search.Cancel();
        _dateMode = true;
        ModeGlyph.Glyph = CommandRow.ForAction(CommandCatalog.All.Single(c => c.Id == "go-to-date")).Glyph;
        ModeText.Text = "Jump to date";
        ModeChip.Visibility = Visibility.Visible;
        SearchGlyph.Visibility = Visibility.Collapsed;
        CommandSearchBox.Text = "";
        CommandSearchBox.PlaceholderText = DatePrompt;
        Show(null, [], []);
        FocusBox();
    }

    /// <summary>Puts keyboard focus in the search box.</summary>
    public void FocusBox() => CommandSearchBox.Focus(FocusState.Programmatic);

    // =========================================================================
    // RESULTS
    // =========================================================================

    // Every keystroke shows its results at once (no settling delay: a keyboard flow wants what's typed on screen
    // already): the date, the actions, and the events from the warmed index. Only before the index is ready does a
    // search run off the thread, and its events follow
    private void OnTextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var text = CommandSearchBox.Text;
        var today = _vm.Today;
        var date = DateQuery.TryParse(text, today, out var day) ? CommandRow.ForDate(day, today) : null;
        if (_dateMode)
        {
            Show(date, [], []);
            return;
        }

        // The Theme Action Offers The Theme That Isn't Showing (the window's, which Ctrl+Shift+L flips)
        var dark = XamlRoot?.Content is FrameworkElement { ActualTheme: ElementTheme.Dark };
        var actions = CommandCatalog.Match(text, dark).Select(CommandRow.ForAction).ToList();
        var actionsFirst = CommandCatalog.NamesAnAction(text, dark); // an action named by what's typed leads and is selected
        var (zone, use24h) = (_vm.Zone, _vm.Settings.Use24HourTime);

        // A Date Row For Today Would Repeat "Go to today", So Only The Action Shows
        if (date?.Date == today && actions.Exists(a => a.Item?.Id == "today"))
        {
            date = null;
        }

        if (EventSearch.Words(text).Count == 0)
        {
            _search.Cancel();
            Show(date, [], actions, actionsFirst);
            return;
        }

        if (_vm.SearchEventsNow(text) is { } hits)
        {
            _search.Cancel();
            Show(date, [.. hits.Select(h => CommandRow.ForHit(h, today, zone, use24h))], actions, actionsFirst);
            return;
        }

        Show(date, [], actions, actionsFirst);
        _vm.Fire(async () =>
        {
            // A slower older search can't overwrite a newer one, and the box must still say what was searched
            if (await _search.RunAsync(ct => _vm.SearchEventsAsync(text, ct)) is { } found && CommandSearchBox.Text == text)
            {
                Show(date, [.. found.Select(h => CommandRow.ForHit(h, today, zone, use24h))], actions, actionsFirst);
            }
        }, "command.search.failed");
    }

    // Each non-empty section under its header ("Go to", then "Events" and "Actions", in that order unless the typed
    // words name an action), the first row selected. Jump to date shows its one date row with no header. When nothing
    // matches, the typed words are offered as a new event's title
    private void Show(CommandRow? date, List<CommandRow> events, List<CommandRow> actions, bool actionsFirst = false)
    {
        List<CommandRow> rows = [];
        if (_dateMode)
        {
            if (date is not null)
            {
                rows.Add(date);
            }
        }
        else
        {
            var (first, second) = actionsFirst ? (("Actions", actions), ("Events", events)) : (("Events", events), ("Actions", actions));
            foreach (var (title, section) in new[] { ("Go to", date is null ? [] : new List<CommandRow> { date }), first, second })
            {
                if (section.Count > 0)
                {
                    rows.Add(CommandRow.ForHeader(title));
                    rows.AddRange(section);
                }
            }
        }

        // Nothing Matched What's Typed (an empty box has matched nothing yet, so the empty state stays hidden)
        var typed = string.Join(' ', EventSearch.Words(CommandSearchBox.Text));
        var nothing = rows.Count == 0 && typed.Length > 0;
        if (nothing && !_dateMode)
        {
            rows.Add(CommandRow.ForNewEvent(typed));
        }

        // The Selection Stays On The Same Row When It's Still Listed (events arriving under a chosen action don't move it)
        var kept = _rows.Find(r => ReferenceEquals(r, CommandResults.SelectedItem))?.AutomationId;
        var index = kept is null ? -1 : rows.FindIndex(r => r.AutomationId == kept);

        _rows = rows;
        _containers.Clear();
        CommandResults.ItemsSource = _rows;
        CommandResults.SelectedIndex = index >= 0 ? index : _rows.FindIndex(r => r.Kind != CommandRowKind.Header);

        // The Empty State Says What Didn't Match: Jump To Date Reads Only Dates
        CommandEmptyText.Text = _dateMode ? "Leaf can't read that as a date." : "No events, actions, or dates match.";
        CommandEmptyPanel.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroll.ChangeView(null, 0, null, disableAnimation: true);
        ShowHints();
    }

    // Each row's container, kept (by reference to our own row) so Up and Down can scroll it into view. Headers are 28
    // high (40 below another section, so groups don't run together) and can't be clicked or selected; rows are 44 high
    // (containers are reused, so both are set every time)
    private void OnRowChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (_rows.Find(r => ReferenceEquals(r, args.Item)) is not { } row)
        {
            return;
        }

        var header = row.Kind == CommandRowKind.Header;
        var height = !header ? 44 : args.ItemIndex == 0 ? 28 : 40;
        args.ItemContainer.MinHeight = height;
        args.ItemContainer.Height = height;
        args.ItemContainer.IsHitTestVisible = !header;
        args.ItemContainer.IsTabStop = !header;

        if (args.InRecycleQueue)
        {
            _containers.Remove(row);
        }
        else
        {
            _containers[row] = args.ItemContainer;
        }
    }

    // =========================================================================
    // KEYS AND CLICKS
    // =========================================================================

    // Preview, so the box's own caret handling doesn't take Up and Down first
    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down or VirtualKey.Up:
                Move(e.Key == VirtualKey.Down ? 1 : -1);
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                RunSelected(jump: KeyState.IsDown(VirtualKey.Menu));
                e.Handled = true;
                break;

            // Backspace On The Empty Box Leaves Jump To Date
            case VirtualKey.Back when _dateMode && CommandSearchBox.Text.Length == 0:
                Reset();
                e.Handled = true;
                break;
        }
    }

    // Alt+Enter when WinUI routes it as a system key instead of a key down
    private void OnJumpInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        RunSelected(jump: true);
        args.Handled = true;
    }

    // The next row up or down, skipping headers; at the ends it stays put. Back at the first row, the list scrolls to
    // the top so its header shows too
    private void Move(int step)
    {
        var index = CommandResults.SelectedIndex + step;
        while (index >= 0 && index < _rows.Count && _rows[index].Kind == CommandRowKind.Header)
        {
            index += step;
        }

        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        CommandResults.SelectedIndex = index;
        if (_rows.FindIndex(r => r.Kind != CommandRowKind.Header) == index)
        {
            ResultsScroll.ChangeView(null, 0, null, disableAnimation: true);
        }
        else if (_containers.TryGetValue(_rows[index], out var container))
        {
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    private void RunSelected(bool jump)
    {
        var index = CommandResults.SelectedIndex;
        if (index >= 0 && index < _rows.Count && _rows[index].Kind != CommandRowKind.Header)
        {
            _run(_rows[index], jump);
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (_rows.Find(r => ReferenceEquals(r, e.ClickedItem)) is { Kind: not CommandRowKind.Header } row)
        {
            _run(row, KeyState.IsDown(VirtualKey.Menu));
        }
    }

    // =========================================================================
    // FOOTER
    // =========================================================================

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowHints();

    // What the selected row is, what Enter does to it, and Alt+Enter's hint for events; nothing when the list is empty,
    // except in Jump to date, which keeps its footer (Date, Go) before a date is typed
    private void ShowHints()
    {
        var index = CommandResults.SelectedIndex;
        var row = index >= 0 && index < _rows.Count ? _rows[index] : null;

        CommandFooter.Visibility = row is null && !_dateMode ? Visibility.Collapsed : Visibility.Visible;
        FooterKind.Text = row?.KindLabel ?? "Date";
        FooterVerb.Text = row?.Verb ?? "Go";
        FooterJump.Visibility = row?.Kind == CommandRowKind.Event ? Visibility.Visible : Visibility.Collapsed;
    }
}
