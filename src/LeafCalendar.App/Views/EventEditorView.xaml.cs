using System.ComponentModel;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
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
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _zoneFocusCheck;
    CalendarViewModel? _owner;
    bool _endTimeAsked;
    bool _zoneAsked;
    bool _zoneEnter;
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

        // The Zone Box Checks Where Focus Went After A Press In Its List
        _zoneFocusCheck = DispatcherQueue.CreateTimer();
        _zoneFocusCheck.Interval    = TimeSpan.FromMilliseconds(300);
        _zoneFocusCheck.IsRepeating = false;
        _zoneFocusCheck.Tick       += (_, _) => CheckZoneFocus();

        ActualThemeChanged += OnThemeChanged;
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
        _zoneAsked            = false;
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
        LeafBrushes.ContrastChanged += OnContrastChanged;
        BuildColors();
        LoadDescription();

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

    // The end time for "E then U", the time zone for "E then Z", else the title
    void FocusFirst()
    {
        if (Editor?.FocusEnd == true || _endTimeAsked)
        {
            EndTimePicker.Focus(FocusState.Programmatic);
        }
        else if (_zoneAsked && Editor is { ShowTimeZone: true })
        {
            TimeZoneBox.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            TimeZoneBox.Focus(FocusState.Programmatic);
        }
        else
        {
            FocusTitleNow();
        }
    }

    /// <summary>
    /// Focuses the time zone box ("E then Z"), after any pending title focus. An all-day event has no zone box, so nothing
    /// happens; the caller has already said why.
    /// </summary>
    public void FocusTimeZone()
    {
        if (Editor is not { ShowTimeZone: true })
        {
            return;
        }

        // Asked before the editor's first focus lands, that focus goes here instead of the title
        _zoneAsked = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            TimeZoneBox.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            TimeZoneBox.Focus(FocusState.Programmatic);
        });
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
        _zoneFocusCheck.Stop();
        if (Editor is not { } editor)
        {
            return;
        }

        // x:Bind skips a null Editor, so the calendar list is let go by hand (with Editor null nothing is written back)
        editor.PropertyChanged -= OnEditorPropertyChanged;
        LeafBrushes.ContrastChanged -= OnContrastChanged;
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

    // A Contrast Theme Turning On Or Off Re-Rings The Swatches (raised off the UI thread)
    void OnContrastChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (Editor is not null)
        {
            BuildColors();
        }
    });

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

        // Description Keys (only what Leaf saves gets through)
        if (DescriptionBox.FocusState != FocusState.Unfocused && DescriptionKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        // Esc in an open dropdown (or the guest suggestions) only closes it
        if (e.Key == VirtualKey.Escape && !RepeatBox.IsDropDownOpen && !EndsBox.IsDropDownOpen && !CalendarBox.IsDropDownOpen && !_reminderDropDownOpen && !GuestBox.IsSuggestionListOpen && !RoomBox.IsSuggestionListOpen
            && !TimeZoneBox.IsSuggestionListOpen && !EventTypeBox.IsDropDownOpen && !ShowAsBox.IsDropDownOpen && !VisibilityBox.IsDropDownOpen)
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
    // TIME ZONE
    // =========================================================================

    // Typing lists matching zones (the catalog is local, so right away)
    void OnZoneTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && Editor is { } editor)
        {
            editor.RefreshZoneSuggestions(DateTimeOffset.UtcNow);
        }
    }

    // Only a pick from the list, or Enter (the first match for typed text), changes the zone; the arrow only opens the
    // list. Picks are found by reference in our own list
    void OnZoneQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var enter  = _zoneEnter;
        _zoneEnter = false;
        if (Editor is not { } editor)
        {
            return;
        }

        if (args.ChosenSuggestion is { } chosen && editor.ZoneSuggestions.FirstOrDefault(s => ReferenceEquals(s, chosen)) is { } picked)
        {
            editor.PickZone(picked);
            return;
        }

        // The Arrow, Or Enter With Nothing Typed, Drops The List Down (every common zone, or the typed text's matches)
        var typed = args.QueryText != editor.TimeZoneText;
        if (!enter || !typed)
        {
            OpenZoneList(editor, all: !typed);
            return;
        }

        if (editor.ZoneSuggestions.FirstOrDefault() is { } first)
        {
            editor.PickZone(first);
            return;
        }

        editor.ResetZoneInput();
    }

    // Enter is told apart from the arrow (both submit); Alt+Down or F4 drops the list down like a ComboBox; Esc closes
    // the list and puts the event's zone back
    void OnZoneKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            _zoneEnter = true;
            return;
        }

        if (e.Key == VirtualKey.Escape && TimeZoneBox.IsSuggestionListOpen)
        {
            e.Handled = true;
            editor.ResetZoneInput();
            TimeZoneBox.IsSuggestionListOpen = false;
            return;
        }

        if ((e.Key == VirtualKey.Down && KeyState.IsDown(VirtualKey.Menu)) || e.Key == VirtualKey.F4)
        {
            e.Handled = true;
            OpenZoneList(editor, all: ZoneInputUntyped(editor));
        }
    }

    static bool ZoneInputUntyped(EventEditorViewModel editor) => editor.ZoneInput == editor.TimeZoneText;

    void OpenZoneList(EventEditorViewModel editor, bool all)
    {
        // Opened after the box is done with the submit (it closes its list as the query goes through)
        editor.RefreshZoneSuggestions(DateTimeOffset.UtcNow, all);
        TimeZoneBox.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(() => TimeZoneBox.IsSuggestionListOpen = ReferenceEquals(Editor, editor) && editor.ZoneSuggestions.Count > 0);
    }

    // Leaving the box without a pick shows the event's zone again. The suggestions stay: pressing one takes focus from
    // the box first, and its click still has to find it (the next typing replaces them)
    void OnZoneLostFocus(object sender, RoutedEventArgs e)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (!TimeZoneBox.IsSuggestionListOpen)
        {
            editor.ResetZoneInput(keepSuggestions: true);
            return;
        }

        // A Press In The Open List Takes Focus Before Its Click Picks: Check Again Once That's Done
        _zoneFocusCheck.Stop();
        _zoneFocusCheck.Start();
    }

    // Focus left the box: once its list has closed without a pick (a click elsewhere dismisses it), the event's zone shows
    // again. Focus coming back to the box stops the check
    void CheckZoneFocus()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (TimeZoneBox.IsSuggestionListOpen)
        {
            _zoneFocusCheck.Start();
            return;
        }

        editor.ResetZoneInput(keepSuggestions: true);
    }

    void OnZoneGotFocus(object sender, RoutedEventArgs e) => _zoneFocusCheck.Stop();

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
        CommitDescription();
        if (_owner is { } owner)
        {
            owner.Fire(() => owner.SaveEditorAsync(sendUpdates), "event.save.failed");
        }
    }

    // =========================================================================
    // DESCRIPTION
    // =========================================================================

    // The stock text box padding (TextControlThemePadding), and the same with the toolbar row on top: 4 margin, the 32
    // buttons, then 4 more before the first line
    static readonly Thickness TextPadding        = new(10, 5, 6, 6);
    static readonly Thickness ToolbarTextPadding = new(10, 40, 6, 6);

    List<(string Text, Uri Link)> _anchors = [];
    bool _descriptionTouched;
    bool _loadingDescription;

    // Loads the editor's description into the box; a description Leaf had to cut short is read-only with no toolbar
    void LoadDescription()
    {
        var tooLong = Editor?.DescriptionTooLong == true;

        _loadingDescription           = true;
        DescriptionBox.IsReadOnly     = false;
        _anchors                      = RichDescription.Load(DescriptionBox, DescriptionHtml.Lines(Editor?.Description ?? ""));
        DescriptionBox.IsReadOnly     = tooLong;
        DescriptionToolbar.Visibility = tooLong ? Visibility.Collapsed : Visibility.Visible;
        DescriptionBox.Padding        = tooLong ? TextPadding : ToolbarTextPadding;
        _descriptionTouched           = false;
        _loadingDescription           = false;
        SyncToolbar();
    }

    // Before saving: only a description the user changed is read back (an untouched one is never rewritten)
    void CommitDescription()
    {
        if (!_descriptionTouched || Editor is not { DescriptionTooLong: false } editor)
        {
            return;
        }

        editor.Description = DescriptionHtml.Write(RichDescription.Read(DescriptionBox, _anchors));
    }

    void OnDescriptionTextChanged(object sender, RoutedEventArgs e) => _descriptionTouched |= !_loadingDescription;

    // A key in the description, seen before the box: true when it's handled here. RichEdit's own Ctrl shortcuts (align,
    // all caps, sub/superscript, line spacing, other list styles) make formatting Leaf can't save, so only bold, italic,
    // underline, undo, redo, select all, copy, cut, and paste get through; Ctrl+Shift+L is the bulleted list. Tab moves
    // focus (in a list it would nest it). AltGr (Ctrl+Alt) still types.
    bool DescriptionKey(VirtualKey key)
    {
        // A Read-Only Description Takes No Shortcuts (they would edit it)
        if (DescriptionBox.IsReadOnly)
        {
            return false;
        }

        var ctrl  = KeyState.IsDown(VirtualKey.Control);
        var shift = KeyState.IsDown(VirtualKey.Shift);

        if (key == VirtualKey.Tab && !ctrl)
        {
            FocusManager.TryMoveFocus(shift ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = XamlRoot.Content });
            return true;
        }

        if (!ctrl || KeyState.IsDown(VirtualKey.Menu) || !IsCharacterKey(key))
        {
            return false;
        }

        if (shift && key == VirtualKey.L)
        {
            List(MarkerType.Bullet);
            return true;
        }

        if (!shift && key is VirtualKey.B or VirtualKey.I or VirtualKey.U)
        {
            _descriptionTouched = true;
            return false;
        }

        return !(shift ? key is VirtualKey.Z or VirtualKey.V : key is VirtualKey.Z or VirtualKey.Y or VirtualKey.A or VirtualKey.C or VirtualKey.X or VirtualKey.V);
    }

    // Space, letters, digits, and symbol keys (the ones a Ctrl shortcut uses)
    static bool IsCharacterKey(VirtualKey key) => (int)key is 0x20 or (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) or (>= 0x60 and <= 0x6F) or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF) or 0xE2;

    // Links keep their own tint in the new theme
    void OnThemeChanged(FrameworkElement sender, object args)
    {
        _loadingDescription = true;
        RichDescription.Recolor(DescriptionBox);
        _loadingDescription = false;
    }

    void OnBoldClick(object sender, RoutedEventArgs e) => Format(f => f.Bold = FormatEffect.Toggle);

    void OnItalicClick(object sender, RoutedEventArgs e) => Format(f => f.Italic = FormatEffect.Toggle);

    void OnUnderlineClick(object sender, RoutedEventArgs e) =>
        Format(f => f.Underline = f.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None);

    void OnBulletsClick(object sender, RoutedEventArgs e) => List(MarkerType.Bullet);

    void OnNumbersClick(object sender, RoutedEventArgs e) => List(MarkerType.Arabic);

    // Applies a character format to the selection, marks the description changed, and returns focus to the box
    void Format(Action<ITextCharacterFormat> change)
    {
        change(DescriptionBox.Document.Selection.CharacterFormat);
        _descriptionTouched = true;
        DescriptionBox.Focus(FocusState.Programmatic);
        SyncToolbar();
    }

    // Toggles the selected paragraphs in or out of a list of this kind
    void List(MarkerType kind)
    {
        var paragraph = DescriptionBox.Document.Selection.ParagraphFormat;
        RichDescription.SetList(paragraph, paragraph.ListType == kind ? MarkerType.None : kind);
        _descriptionTouched = true;
        DescriptionBox.Focus(FocusState.Programmatic);
        SyncToolbar();
    }

    // Typing at a link's edge is plain text, then the toolbar shows the caret's format
    void OnDescriptionSelectionChanged(object sender, RoutedEventArgs e)
    {
        RichDescription.PlainInsertion(DescriptionBox);
        SyncToolbar();
    }

    // Toolbar toggles show the selection's format
    void SyncToolbar()
    {
        var format = DescriptionBox.Document.Selection.CharacterFormat;
        var list   = DescriptionBox.Document.Selection.ParagraphFormat.ListType;

        BoldButton.IsChecked      = format.Bold == FormatEffect.On;
        ItalicButton.IsChecked    = format.Italic == FormatEffect.On;
        UnderlineButton.IsChecked = format.Underline != UnderlineType.None;
        BulletsButton.IsChecked   = list == MarkerType.Bullet;
        NumbersButton.IsChecked   = list is not (MarkerType.None or MarkerType.Undefined or MarkerType.Bullet);
    }

    // Paste is always plain text: no pictures, objects, or foreign formatting (Review Focus 3)
    void OnDescriptionPaste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        _owner?.Fire(PastePlainTextAsync, "editor.paste.failed");
    }

    // The text lands only in the editor it was pasted into (the panel may have moved on while the clipboard was read)
    async Task PastePlainTextAsync()
    {
        var editor  = Editor;
        var content = Clipboard.GetContent();
        if (editor is null || DescriptionBox.IsReadOnly || !content.Contains(StandardDataFormats.Text))
        {
            return;
        }

        var text = (await content.GetTextAsync()).Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (!ReferenceEquals(Editor, editor) || DescriptionBox.IsReadOnly)
        {
            return;
        }

        var selection = DescriptionBox.Document.Selection;
        selection.SetText(TextSetOptions.None, text);
        RichDescription.Untint(DescriptionBox, selection);
        selection.Collapse(false);
        _descriptionTouched = true;
    }
}
