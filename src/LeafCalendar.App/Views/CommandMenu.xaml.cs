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
    CommandRow(CommandRowKind kind, string title, string detail, string keys, string glyph, string color, string automationId)
    {
        Kind              = kind;
        Title             = title;
        Detail            = detail;
        Keys              = keys;
        Glyph             = glyph;
        Color             = color;
        AutomationId      = automationId;
        EventVisibility   = kind == CommandRowKind.Event ? Visibility.Visible : Visibility.Collapsed;
        ActionVisibility  = kind is CommandRowKind.Action or CommandRowKind.Date ? Visibility.Visible : Visibility.Collapsed;
        KeysVisibility    = keys.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderVisibility  = kind == CommandRowKind.Header ? Visibility.Visible : Visibility.Collapsed;
        RowVisibility     = kind == CommandRowKind.Header ? Visibility.Collapsed : Visibility.Visible;
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
        CommandRowKind.Event  => "Event",
        CommandRowKind.Action => "Action",
        CommandRowKind.Date   => "Date",
        _                     => "",
    };

    /// <summary>The event, for <see cref="CommandRowKind.Event"/>.</summary>
    public SearchHit? Hit { get; private init; }

    /// <summary>The action, for <see cref="CommandRowKind.Action"/>.</summary>
    public CommandItem? Item { get; private init; }

    /// <summary>The date, for <see cref="CommandRowKind.Date"/>.</summary>
    public DateOnly? Date { get; private init; }

    /// <summary>A found event: "Mon, Oct 12 · 9:00 AM", plus " · in location" (or guests, description) when the title didn't match.</summary>
    public static CommandRow ForHit(SearchHit hit, DateOnly today, TimeZoneInfo zone, bool use24h)
    {
        var day   = hit.IsAllDay ? DateOnly.FromDateTime(hit.Start.UtcDateTime) : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(hit.Start, zone).DateTime);
        var time  = hit.IsAllDay ? "All day" : TimeLabels.TimeOfDay(hit.Start, zone, use24h);
        var where = hit.Field switch
        {
            SearchField.Location    => " · in location",
            SearchField.Guest       => " · in guests",
            SearchField.Description => " · in description",
            _                       => "",
        };

        return new(CommandRowKind.Event, hit.Title, $"{DateQuery.Label(day, today)} · {time}{where}", "", "", hit.Color, $"SearchResult_{hit.EventId}") { Hit = hit };
    }

    /// <summary>An action from the catalog.</summary>
    public static CommandRow ForAction(CommandItem item) =>
        new(CommandRowKind.Action, item.Title, "", item.Keys, GlyphFor(item.Id), "", $"CommandResult_{item.Id}") { Item = item };

    /// <summary>A section title.</summary>
    public static CommandRow ForHeader(string title) =>
        new(CommandRowKind.Header, title, "", "", "", "", "CommandHeader_" + title.Replace(' ', '-').ToLowerInvariant());

    /// <summary>A typed date.</summary>
    public static CommandRow ForDate(DateOnly date, DateOnly today) =>
        new(CommandRowKind.Date, "Go to " + DateQuery.Label(date, today), "", "", "", "", "CommandResult_date") { Date = date };

    // The design standard's glyphs where it names one, Segoe Fluent Icons otherwise
    static string GlyphFor(string id) => id switch
    {
        "create-event"                                   => "",
        "go-to-date" or "today"                          => "",
        "join"                                           => "",
        "overlay" or "meet-with"                         => "",
        "time-travel"                                    => "",
        "share"                                          => "",
        "toggle-theme"                                   => "",
        "shortcuts" or "settings-shortcuts"              => "",
        "settings-about"                                 => "",
        "sync"                                           => "",
        "back"                                           => "",
        "forward"                                        => "",
        _ when id.StartsWith("settings", StringComparison.Ordinal) => "",
        _ when id.StartsWith("view-", StringComparison.Ordinal)    => "",
        _                                                => "",
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
    // Typing settles this long before a search runs
    static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(120);

    readonly CalendarViewModel _vm;
    readonly Action<CommandRow, bool> _run;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _typing;
    readonly LatestSearch<IReadOnlyList<SearchHit>> _search = new();
    readonly Dictionary<CommandRow, UIElement> _containers = [];
    List<CommandRow> _rows = [];

    /// <summary>Creates the menu; <paramref name="run"/> gets the picked row and whether to jump (Alt+Enter).</summary>
    public CommandMenu(CalendarViewModel vm, Action<CommandRow, bool> run)
    {
        _vm  = vm;
        _run = run;
        InitializeComponent();
        ScrollIndicator.ShowOnHover(ResultsScroll);

        // Search Once Typing Settles
        _typing             = DispatcherQueue.CreateTimer();
        _typing.Interval    = TypingDelay;
        _typing.IsRepeating = false;
        _typing.Tick       += (_, _) => _vm.Fire(RefreshAsync, "command.search.failed");

        // Closing Stops A Search Still Running
        Unloaded += (_, _) =>
        {
            _typing.Stop();
            _search.Cancel();
        };
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

    // The rows' entrance: they slide down into place and fade in with the flyout, one after another
    const float EntranceOffset = -12;
    const int EntranceStaggerLimit = 10;
    static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan EntranceStagger  = TimeSpan.FromMilliseconds(17);

    /// <summary>Caps the results list at the given height (at most <see cref="ResultsMaxHeight"/>), so the menu never runs past a short window.</summary>
    public void LimitResultsHeight(double height) => ResultsScroll.MaxHeight = Math.Min(ResultsMaxHeight, height);

    /// <summary>
    /// Slides the shown rows down into place as they fade in, a little after one another, so they arrive with the rest
    /// of the menu as the flyout opens (nothing moves when Windows' animations are off).
    /// </summary>
    public void PlayEntrance()
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            return;
        }

        var order = 0;
        foreach (var row in _rows)
        {
            if (!_containers.TryGetValue(row, out var container))
            {
                continue;
            }

            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(container, true);
            var visual     = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(container);
            var compositor = visual.Compositor;
            var easing     = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1));
            var delay      = EntranceStagger * Math.Min(order++, EntranceStaggerLimit);

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0, new System.Numerics.Vector3(0, EntranceOffset, 0));
            slide.InsertKeyFrame(1, System.Numerics.Vector3.Zero, easing);
            slide.Duration      = EntranceDuration;
            slide.DelayTime     = delay;
            slide.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation("Translation", slide);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1, easing);
            fade.Duration      = EntranceDuration;
            fade.DelayTime     = delay;
            fade.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation("Opacity", fade);
        }
    }

    // What the box asks for: anything, or (Jump to date) a date in words
    const string SearchPrompt = "Search events, or type a command or a date";
    const string DatePrompt   = "Jump to a date: nov 5th, 10 weeks, next fri, 3 days ago…";

    /// <summary>Clears the box and shows the default actions.</summary>
    public void Reset()
    {
        _typing.Stop();
        _search.Cancel();
        CommandSearchBox.Text            = "";
        CommandSearchBox.PlaceholderText = SearchPrompt;
        Show(null, [], [.. CommandCatalog.Defaults.Select(CommandRow.ForAction)]);
    }

    /// <summary>Jump to date: the box asks for a date in words (what's typed is read as one, as always).</summary>
    public void AskForDate()
    {
        _typing.Stop();
        _search.Cancel();
        CommandSearchBox.Text            = "";
        CommandSearchBox.PlaceholderText = DatePrompt;
        Show(null, [], []);
        FocusBox();
    }

    /// <summary>Puts keyboard focus in the search box.</summary>
    public void FocusBox() => CommandSearchBox.Focus(FocusState.Programmatic);

    // =========================================================================
    // RESULTS
    // =========================================================================

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _typing.Stop();
        _typing.Start();
    }

    // The date first, then events, then actions; a slower older search can't overwrite a newer one
    async Task RefreshAsync()
    {
        var text  = CommandSearchBox.Text;
        var today = _vm.Today;
        var date  = DateQuery.TryParse(text, today, out var day) ? CommandRow.ForDate(day, today) : null;

        IReadOnlyList<SearchHit> hits = [];
        if (EventSearch.Words(text).Count == 0)
        {
            _search.Cancel();
        }
        else if (await _search.RunAsync(ct => _vm.SearchEventsAsync(text, ct)) is { } found)
        {
            hits = found;
        }
        else
        {
            return;
        }

        // An Action Named By What's Typed Comes Before The Events (and is the row selected); otherwise events first
        var (zone, use24h) = (_vm.Zone, _vm.Settings.Use24HourTime);
        Show(date, [.. hits.Select(h => CommandRow.ForHit(h, today, zone, use24h))], [.. CommandCatalog.Match(text).Select(CommandRow.ForAction)], actionsFirst: CommandCatalog.NamesAnAction(text));
    }

    // Each non-empty section under its header ("Go to", then "Events" and "Actions", in that order unless the typed
    // words name an action), the first row selected
    void Show(CommandRow? date, List<CommandRow> events, List<CommandRow> actions, bool actionsFirst = false)
    {
        List<CommandRow> rows = [];
        var (first, second) = actionsFirst ? (("Actions", actions), ("Events", events)) : (("Events", events), ("Actions", actions));
        foreach (var (title, section) in new[] { ("Go to", date is null ? [] : new List<CommandRow> { date }), first, second })
        {
            if (section.Count > 0)
            {
                rows.Add(CommandRow.ForHeader(title));
                rows.AddRange(section);
            }
        }

        _rows = rows;
        _containers.Clear();
        CommandResults.ItemsSource   = _rows;
        CommandResults.SelectedIndex = _rows.FindIndex(r => r.Kind != CommandRowKind.Header);
        CommandEmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroll.ChangeView(null, 0, null, disableAnimation: true);
        ShowHints();
    }

    // Each row's container, kept (by reference to our own row) so Up and Down can scroll it into view. Headers are 28
    // high and can't be clicked or selected; rows are 44 high (containers are reused, so both are set every time)
    void OnRowChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (_rows.Find(r => ReferenceEquals(r, args.Item)) is not { } row)
        {
            return;
        }

        var header = row.Kind == CommandRowKind.Header;
        args.ItemContainer.MinHeight         = header ? 28 : 44;
        args.ItemContainer.Height            = header ? 28 : 44;
        args.ItemContainer.IsHitTestVisible  = !header;
        args.ItemContainer.IsTabStop         = !header;

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
    void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
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
        }
    }

    // Alt+Enter when WinUI routes it as a system key instead of a key down
    void OnJumpInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        RunSelected(jump: true);
        args.Handled = true;
    }

    // The next row up or down, skipping headers; at the ends it stays put. Back at the first row, the list scrolls to
    // the top so its header shows too
    void Move(int step)
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

    void RunSelected(bool jump)
    {
        var index = CommandResults.SelectedIndex;
        if (index >= 0 && index < _rows.Count && _rows[index].Kind != CommandRowKind.Header)
        {
            _run(_rows[index], jump);
        }
    }

    void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (_rows.Find(r => ReferenceEquals(r, e.ClickedItem)) is { Kind: not CommandRowKind.Header } row)
        {
            _run(row, KeyState.IsDown(VirtualKey.Menu));
        }
    }

    // =========================================================================
    // FOOTER
    // =========================================================================

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowHints();

    // What the selected row is, and Alt+Enter's hint for events; nothing when the list is empty
    void ShowHints()
    {
        var index = CommandResults.SelectedIndex;
        var row   = index >= 0 && index < _rows.Count ? _rows[index] : null;

        CommandFooter.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        FooterKind.Text          = row?.KindLabel ?? "";
        FooterJump.Visibility    = row?.Kind == CommandRowKind.Event ? Visibility.Visible : Visibility.Collapsed;
    }
}
