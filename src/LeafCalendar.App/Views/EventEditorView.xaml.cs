using System.ComponentModel;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace LeafCalendar.App.Views;

/// <summary>
/// The event editor, shown in the details panel. Fields bind to an <see cref="EventEditorViewModel"/>; saving goes
/// through <see cref="CalendarViewModel.SaveEditorAsync"/>, which asks about repeating events.
/// </summary>
public sealed partial class EventEditorView : UserControl
{
    // The calendar's own color first, then Google's 11 event colors
    static readonly (string? Id, string Name)[] Colors =
        [(null, "Calendar color"), .. EventColors.EventColorNames.Select(c => ((string?)c.Id, c.Name))];

    readonly List<(Button Swatch, string? Id)> _swatches = [];
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _suggestTimer;
    CalendarViewModel? _owner;
    bool _endTimeAsked;
    bool _reminderDropDownOpen;

    /// <summary>Creates the editor.</summary>
    public EventEditorView()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(BodyScroll);

        // Contact Search Waits For A Pause In Typing
        _suggestTimer = DispatcherQueue.CreateTimer();
        _suggestTimer.Interval    = TimeSpan.FromMilliseconds(250);
        _suggestTimer.IsRepeating = false;
        _suggestTimer.Tick       += (_, _) => RefreshSuggestions();
    }

    /// <summary>The fields being edited, or null.</summary>
    public EventEditorViewModel? Editor { get; private set; }

    /// <summary>x:Bind helper: a brush for a hex color.</summary>
    public static SolidColorBrush Brush(string hex) => LeafBrushes.FromHex(hex);

    /// <summary>x:Bind helper: the date takes the whole row when the time picker hides (all-day).</summary>
    public static int DateSpan(bool showTimes) => showTimes ? 1 : 2;

    /// <summary>Shows <paramref name="editor"/> and focuses the title (or the end time for "E then U").</summary>
    public void Attach(CalendarViewModel owner, EventEditorViewModel editor)
    {
        if (ReferenceEquals(Editor, editor))
        {
            return;
        }

        Detach();
        _owner                = owner;
        Editor                = editor;
        _endTimeAsked         = false;
        _reminderDropDownOpen = false;

        // A new editor starts at the top
        ScrollIndicator.Hide(BodyScroll);
        BodyScroll.ChangeView(null, 0, null, true);

        // Filling the calendar list can write "nothing picked" back through the TwoWay binding; put the pick back
        var calendarIndex = editor.CalendarIndex;
        Bindings.Update();
        editor.CalendarIndex = calendarIndex;
        CalendarBox.SelectedIndex = calendarIndex;

        editor.PropertyChanged += OnEditorPropertyChanged;
        BuildColors();

        // Focus After The Key That Opened The Editor Is Done (its character never reaches the title), or once the
        // editor first loads (a focus call before then fails)
        if (TitleBox.IsLoaded)
        {
            DispatcherQueue.TryEnqueue(FocusFirst);
        }
        else
        {
            TitleBox.Loaded -= OnTitleLoaded;
            TitleBox.Loaded += OnTitleLoaded;
        }
    }

    /// <summary>
    /// Focuses the title now with the caret at the end, so the key being pressed (the first one typed after an instant E)
    /// lands there. Returns false when the title can't take focus yet.
    /// </summary>
    public bool FocusTitleNow()
    {
        if (!TitleBox.Focus(FocusState.Programmatic))
        {
            return false;
        }

        TitleBox.Select(TitleBox.Text.Length, 0);
        return true;
    }

    void OnTitleLoaded(object sender, RoutedEventArgs e)
    {
        TitleBox.Loaded -= OnTitleLoaded;
        FocusFirst();
    }

    // The end time for "E then U", else the title
    void FocusFirst()
    {
        if (Editor?.FocusEnd == true || _endTimeAsked)
        {
            EndTimePicker.Focus(FocusState.Programmatic);
        }
        else
        {
            FocusTitleNow();
        }
    }

    /// <summary>Moves focus to the end time ("E then U" right after an instant E), after any pending title focus.</summary>
    public void FocusEndTime()
    {
        _endTimeAsked = true;
        DispatcherQueue.TryEnqueue(() => EndTimePicker.Focus(FocusState.Programmatic));
    }

    /// <summary>Lets go of the current editor and empties the calendar picker, so the next editor's pick isn't reset.</summary>
    public void Detach()
    {
        _owner = null;
        _suggestTimer.Stop();
        if (Editor is not { } editor)
        {
            return;
        }

        // x:Bind skips a null Editor, so the calendar list is let go by hand (with Editor null nothing is written back)
        editor.PropertyChanged -= OnEditorPropertyChanged;
        Editor = null;
        CalendarBox.ItemsSource = null;
    }

    void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EventEditorViewModel.ColorId))
        {
            PaintSwatches();
        }

        // A Contacts Line That Turns On Scrolls Into View (the guest box sits low, near the pinned footer); after layout,
        // so it's measured
        if (e.PropertyName == nameof(EventEditorViewModel.ContactsAccess) && Editor is { } editor && (editor.ShowAllowContacts || editor.ShowContactsApiOff))
        {
            FrameworkElement line = editor.ShowAllowContacts ? AllowContactsLink : ContactsApiOffText;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => line.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }));
        }

        // The "Calendar color" swatch follows the picked calendar
        if (e.PropertyName == nameof(EventEditorViewModel.CalendarIndex))
        {
            BuildColors();
        }
    }

    // Swatches keep their own ids (never read back from the button)
    void BuildColors()
    {
        ColorPanel.Children.Clear();
        _swatches.Clear();
        var calendarColor = Editor is { } e && e.CalendarIndex >= 0 && e.CalendarIndex < e.Calendars.Count ? e.Calendars[e.CalendarIndex].Color : CalendarInfo.DefaultColor;

        foreach (var (id, name) in Colors)
        {
            var color  = LeafBrushes.FromHex(EventColors.ResolveAccent(id, calendarColor)).Color;
            var ring   = LeafBrushes.PrimaryText(ActualTheme == ElementTheme.Dark);
            var swatch = new Button
            {
                Width           = 24,
                Height          = 24,
                Padding         = new Thickness(0),
                CornerRadius    = new CornerRadius(12),
                Background      = new SolidColorBrush(color),
                BorderBrush     = ring,
            };

            // Hover And Press Tint The Color Instead Of Replacing It (the picked ring stays too)
            swatch.Resources["ButtonBackgroundPointerOver"]  = new SolidColorBrush(color) { Opacity = 0.8 };
            swatch.Resources["ButtonBackgroundPressed"]      = new SolidColorBrush(color) { Opacity = 0.6 };
            swatch.Resources["ButtonBorderBrushPointerOver"] = ring;
            swatch.Resources["ButtonBorderBrushPressed"]     = ring;
            AutomationProperties.SetName(swatch, name);
            AutomationProperties.SetAutomationId(swatch, $"EditorColor_{id ?? "Calendar"}");
            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += (_, _) => Editor?.ColorId = id;
            _swatches.Add((swatch, id));
            ColorPanel.Children.Add(swatch);
        }

        PaintSwatches();
    }

    void PaintSwatches()
    {
        foreach (var (swatch, id) in _swatches)
        {
            swatch.BorderThickness = new Thickness(id == Editor?.ColorId ? 2 : 0);
        }
    }

    void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && KeyState.IsDown(VirtualKey.Control))
        {
            e.Handled = true;
            Save(sendUpdates: !KeyState.IsDown(VirtualKey.Shift));
            return;
        }

        // Esc in an open dropdown (or the guest suggestions) only closes it
        if (e.Key == VirtualKey.Escape && !RepeatBox.IsDropDownOpen && !EndsBox.IsDropDownOpen && !CalendarBox.IsDropDownOpen && !_reminderDropDownOpen && !GuestBox.IsSuggestionListOpen && !RoomBox.IsSuggestionListOpen)
        {
            e.Handled = true;
            _owner?.CancelEdit();
        }
    }

    // =========================================================================
    // GUESTS
    // =========================================================================

    // Only typing searches (not our own text changes, like the box emptying after a pick)
    void OnGuestTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _suggestTimer.Stop();
        _suggestTimer.Start();
    }

    void RefreshSuggestions()
    {
        if (_owner is { } owner && Editor is { } editor)
        {
            owner.Fire(editor.RefreshSuggestionsAsync, "contacts.suggest.failed");
        }
    }

    // A picked suggestion (click, or arrows then Enter) adds its address; Enter on typed text adds that. The pick is
    // found by reference in our own list (never cast back from WinRT). SuggestionChosen isn't used: arrowing through
    // the list raises it for every row passed.
    void OnGuestQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        _suggestTimer.Stop();
        if (args.ChosenSuggestion is { } chosen && editor.Suggestions.FirstOrDefault(s => ReferenceEquals(s, chosen)) is { } suggestion)
        {
            editor.PickSuggestion(suggestion);
            return;
        }

        // The submitted text itself (the box's two-way text can lag right after typing)
        editor.GuestInput = args.QueryText;
        editor.AddGuest();
    }

    void OnAddGuestClick(object sender, RoutedEventArgs e) => Editor?.AddGuest();

    // Rooms come from local events, so typing lists them right away
    void OnRoomTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && _owner is { } owner && Editor is { } editor)
        {
            owner.Fire(editor.RefreshRoomSuggestionsAsync, "rooms.suggest.failed");
        }
    }

    // A picked room is found by reference in our own list (never cast back from WinRT); typed text alone adds nothing
    void OnRoomQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (Editor is { } editor && (args.ChosenSuggestion ?? editor.RoomSuggestions.FirstOrDefault()) is { } chosen
            && editor.RoomSuggestions.FirstOrDefault(s => ReferenceEquals(s, chosen)) is { } room)
        {
            editor.PickRoom(room);
        }
    }

    void OnAllowContactsClick(object sender, RoutedEventArgs e)
    {
        if (_owner is { } owner && Editor is { } editor)
        {
            owner.Fire(() => owner.AllowContactsAsync(editor.ContactsAccountId), "contacts.allow.failed");
        }
    }

    // =========================================================================
    // VIDEO CALL
    // =========================================================================

    // Focus moves to the button that takes the clicked one's place
    void OnAddConferenceClick(object sender, RoutedEventArgs e)
    {
        Editor?.HasConference = true;
        DispatcherQueue.TryEnqueue(() => RemoveConferenceButton.Focus(FocusState.Programmatic));
    }

    void OnRemoveConferenceClick(object sender, RoutedEventArgs e)
    {
        Editor?.HasConference = false;
        DispatcherQueue.TryEnqueue(() => AddConferenceButton.Focus(FocusState.Programmatic));
    }

    void OnAddReminderClick(object sender, RoutedEventArgs e) => Editor?.AddReminder();

    // Reminder dropdowns live in a template, so their open state is tracked here (one opens at a time)
    void OnReminderDropDownOpened(object? sender, object e) => _reminderDropDownOpen = true;

    void OnReminderDropDownClosed(object? sender, object e) => _reminderDropDownOpen = false;

    void OnSaveClick(object sender, RoutedEventArgs e) => Save(sendUpdates: true);

    void OnSaveQuietClick(object sender, RoutedEventArgs e) => Save(sendUpdates: false);

    void OnCancelClick(object sender, RoutedEventArgs e) => _owner?.CancelEdit();

    void Save(bool sendUpdates)
    {
        if (_owner is { } owner)
        {
            owner.Fire(() => owner.SaveEditorAsync(sendUpdates), "event.save.failed");
        }
    }
}
