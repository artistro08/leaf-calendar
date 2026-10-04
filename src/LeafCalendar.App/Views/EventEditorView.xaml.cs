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
    private static readonly (string? Id, string Name)[] Colors =
        [(null, "Calendar color"), .. EventColors.EventColorNames.Select(c => ((string?)c.Id, c.Name))];

    private readonly List<(Button Swatch, string? Id)> _swatches = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _suggestTimer;

    private CalendarViewModel? _owner;
    private bool _endTimeAsked;
    private bool _zoneAsked;
    private bool _reminderDropDownOpen;

    /// <summary>Creates the editor.</summary>
    public EventEditorView()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(BodyScroll);
        TimePickerFit.Shrink(StartTimePicker);
        TimePickerFit.Shrink(EndTimePicker);

        // Contact Search Waits For A Pause In Typing
        _suggestTimer = DispatcherQueue.CreateTimer();
        _suggestTimer.Interval = TimeSpan.FromMilliseconds(250);
        _suggestTimer.IsRepeating = false;
        _suggestTimer.Tick += (_, _) => RefreshSuggestions();


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
        _owner = owner;
        Editor = editor;
        _endTimeAsked = false;
        _zoneAsked = false;
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
        TimeZoneBox.Show(editor.TimeZoneId, editor.Before.Start);
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

    private void OnTitleLoaded(object sender, RoutedEventArgs e)
    {
        TitleBox.Loaded -= OnTitleLoaded;
        FocusFirst();
    }

    // The end time for "E then U", the time zone for "E then Z", else the title
    private void FocusFirst()
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

    /// <summary>Lets go of the current editor and empties every list (the calendar picker too, so the next editor's pick isn't reset).</summary>
    public void Detach()
    {
        _owner = null;
        _suggestTimer.Stop();
        if (Editor is not { } editor)
        {
            return;
        }

        editor.PropertyChanged -= OnEditorPropertyChanged;
        LeafBrushes.ContrastChanged -= OnContrastChanged;

        // x:Bind skips a null Editor, so every list is let go by hand: the controls clear their rows now instead of
        // holding the old editor's (see ItemPins)
        Editor = null;
        Bindings.Update();
        CalendarBox.ItemsSource = null;
        WeekdayList.ItemsSource = null;
        GuestList.ItemsSource = null;
        ReminderList.ItemsSource = null;
        TimeZoneBox.ItemsSource = null;
        GuestBox.ItemsSource = null;
        RoomBox.ItemsSource = null;
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
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
    private void BuildColors()
    {
        ColorPanel.Children.Clear();
        _swatches.Clear();
        var calendarColor = Editor is { } e && e.CalendarIndex >= 0 && e.CalendarIndex < e.Calendars.Count ? e.Calendars[e.CalendarIndex].Color : CalendarInfo.DefaultColor;

        foreach (var (id, name) in Colors)
        {
            var color = LeafBrushes.FromHex(EventColors.ResolveAccent(id, calendarColor)).Color;
            var ring = LeafBrushes.PrimaryText(ActualTheme == ElementTheme.Dark);
            var swatch = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(color),
                BorderBrush = ring,
            };

            // Hover And Press Tint The Color Instead Of Replacing It (the picked ring stays too)
            swatch.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(color) { Opacity = 0.8 };
            swatch.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(color) { Opacity = 0.6 };
            swatch.Resources["ButtonBorderBrushPointerOver"] = ring;
            swatch.Resources["ButtonBorderBrushPressed"] = ring;
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
    private void OnContrastChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (Editor is not null)
        {
            BuildColors();
        }
    });

    private void PaintSwatches()
    {
        foreach (var (swatch, id) in _swatches)
        {
            swatch.BorderThickness = new Thickness(id == Editor?.ColorId ? 2 : 0);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
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
            && !TimeZoneBox.IsDropDownOpen && !EventTypeBox.IsDropDownOpen && !ShowAsBox.IsDropDownOpen && !VisibilityBox.IsDropDownOpen)
        {
            e.Handled = true;
            _owner?.CancelEdit();
        }
    }

    // =========================================================================
    // GUESTS
    // =========================================================================

    // Only typing searches (not our own text changes, like the box emptying after a pick)
    private void OnGuestTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _suggestTimer.Stop();
        _suggestTimer.Start();
    }

    private void RefreshSuggestions()
    {
        if (_owner is { } owner && Editor is { } editor)
        {
            owner.Fire(editor.RefreshSuggestionsAsync, "contacts.suggest.failed");
        }
    }

    // A picked suggestion (click, or arrows then Enter) adds its address; Enter on typed text adds that. The pick is
    // found by reference in our own list (never cast back from WinRT). SuggestionChosen isn't used: arrowing through
    // the list raises it for every row passed.
    private void OnGuestQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
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

        // Enter in an empty box adds nothing (and isn't an invalid address)
        if (string.IsNullOrWhiteSpace(args.QueryText))
        {
            return;
        }

        // The submitted text itself (the box's two-way text can lag right after typing)
        editor.GuestInput = args.QueryText;
        editor.AddGuest();
    }

    private void OnAddGuestClick(object sender, RoutedEventArgs e) => Editor?.AddGuest();

    // Rooms come from local events, so typing lists them right away
    private void OnRoomTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && _owner is { } owner && Editor is { } editor)
        {
            owner.Fire(editor.RefreshRoomSuggestionsAsync, "rooms.suggest.failed");
        }
    }

    // A picked room is found by reference in our own list (never cast back from WinRT); typed text alone adds nothing
    private void OnRoomQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (Editor is { } editor && (args.ChosenSuggestion ?? editor.RoomSuggestions.FirstOrDefault()) is { } chosen
            && editor.RoomSuggestions.FirstOrDefault(s => ReferenceEquals(s, chosen)) is { } room)
        {
            editor.PickRoom(room);
        }
    }

    private void OnAllowContactsClick(object sender, RoutedEventArgs e)
    {
        if (_owner is { } owner && Editor is { } editor)
        {
            owner.Fire(() => owner.AllowContactsAsync(editor.ContactsAccountId), "contacts.allow.failed");
        }
    }

    // =========================================================================
    // TIME ZONE
    // =========================================================================

    // A pick from the list (or typed text matched to one) makes it the event's zone
    private void OnZoneChanged(object? sender, string id) => Editor?.PickZone(id);
    // =========================================================================
    // VIDEO CALL
    // =========================================================================

    // Focus moves to the button that takes the clicked one's place
    private void OnAddConferenceClick(object sender, RoutedEventArgs e)
    {
        Editor?.HasConference = true;
        DispatcherQueue.TryEnqueue(() => RemoveConferenceButton.Focus(FocusState.Programmatic));
    }

    private void OnRemoveConferenceClick(object sender, RoutedEventArgs e)
    {
        Editor?.HasConference = false;
        DispatcherQueue.TryEnqueue(() => AddConferenceButton.Focus(FocusState.Programmatic));
    }

    private void OnAddReminderClick(object sender, RoutedEventArgs e) => Editor?.AddReminder();

    // Reminder dropdowns live in a template, so their open state is tracked here (one opens at a time)
    private void OnReminderDropDownOpened(object? sender, object e) => _reminderDropDownOpen = true;

    private void OnReminderDropDownClosed(object? sender, object e) => _reminderDropDownOpen = false;

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save(sendUpdates: true);

    private void OnSaveQuietClick(object sender, RoutedEventArgs e) => Save(sendUpdates: false);

    private void OnCancelClick(object sender, RoutedEventArgs e) => _owner?.CancelEdit();

    private void Save(bool sendUpdates)
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
    // buttons, 4 margin, the 1 px divider, then 4 more before the first line
    private static readonly Thickness TextPadding = new(10, 5, 6, 6);
    private static readonly Thickness ToolbarTextPadding = new(10, 45, 6, 6);

    private List<(string Text, Uri Link)> _anchors = [];
    private bool _descriptionTouched;
    private bool _loadingDescription;

    // Loads the editor's description into the box; a description Leaf had to cut short is read-only with no toolbar
    private void LoadDescription()
    {
        var tooLong = Editor?.DescriptionTooLong == true;

        _loadingDescription = true;
        DescriptionBox.IsReadOnly = false;
        _anchors = RichDescription.Load(DescriptionBox, DescriptionHtml.Lines(Editor?.Description ?? ""));
        DescriptionBox.IsReadOnly = tooLong;
        DescriptionToolbar.Visibility = tooLong ? Visibility.Collapsed : Visibility.Visible;
        DescriptionBox.Padding = tooLong ? TextPadding : ToolbarTextPadding;
        _descriptionTouched = false;
        _loadingDescription = false;
        SyncToolbar();
        RecolorLinksSoon();
    }

    // Before saving: only a description the user changed is read back (an untouched one is never rewritten)
    private void CommitDescription()
    {
        if (!_descriptionTouched || Editor is not { DescriptionTooLong: false } editor)
        {
            return;
        }

        editor.Description = DescriptionHtml.Write(RichDescription.Read(DescriptionBox, _anchors));
    }

    private void OnDescriptionTextChanged(object sender, RoutedEventArgs e) => _descriptionTouched |= !_loadingDescription;

    // A key in the description, seen before the box: true when it's handled here. RichEdit's own Ctrl shortcuts (align,
    // all caps, sub/superscript, line spacing, other list styles) make formatting Leaf can't save, so only bold, italic,
    // underline, undo, redo, select all, copy, cut, and paste get through; Ctrl+Shift+L is the bulleted list. Tab moves
    // focus (in a list it would nest it). AltGr (Ctrl+Alt) still types.
    private bool DescriptionKey(VirtualKey key)
    {
        // A Read-Only Description Takes No Shortcuts (they would edit it)
        if (DescriptionBox.IsReadOnly)
        {
            return false;
        }

        var ctrl = KeyState.IsDown(VirtualKey.Control);
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
    private static bool IsCharacterKey(VirtualKey key) => (int)key is 0x20 or (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) or (>= 0x60 and <= 0x6F) or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF) or 0xE2;

    // Links take the new theme's link color
    private void OnThemeChanged(FrameworkElement sender, object args) => RecolorLinksSoon();

    // After the box has applied its theme, which recolors its text (links read back black in light theme otherwise): at
    // low priority, behind the box's own work for a load or a theme change
    private void RecolorLinksSoon() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
    {
        _loadingDescription = true;
        RichDescription.Recolor(DescriptionBox);
        _loadingDescription = false;

        // Ctrl+Z Never Takes The Color Back Off An Untouched Description's Links
        if (!_descriptionTouched)
        {
            DescriptionBox.Document.ClearUndoRedoHistory();
        }
    });

    private void OnBoldClick(object sender, RoutedEventArgs e) => Format(f => f.Bold = FormatEffect.Toggle);

    private void OnItalicClick(object sender, RoutedEventArgs e) => Format(f => f.Italic = FormatEffect.Toggle);

    private void OnUnderlineClick(object sender, RoutedEventArgs e) =>
        Format(f => f.Underline = RichDescription.IsUnderlined(f) ? UnderlineType.None : UnderlineType.Single);

    private void OnBulletsClick(object sender, RoutedEventArgs e) => List(MarkerType.Bullet);

    private void OnNumbersClick(object sender, RoutedEventArgs e) => List(MarkerType.Arabic);

    // Applies a character format to the selection, marks the description changed, and returns focus to the box
    private void Format(Action<ITextCharacterFormat> change)
    {
        change(DescriptionBox.Document.Selection.CharacterFormat);
        _descriptionTouched = true;
        DescriptionBox.Focus(FocusState.Programmatic);
        SyncToolbar();
    }

    // Toggles the selected paragraphs in or out of a list of this kind
    private void List(MarkerType kind)
    {
        var paragraph = DescriptionBox.Document.Selection.ParagraphFormat;
        RichDescription.SetList(paragraph, paragraph.ListType == kind ? MarkerType.None : kind);
        _descriptionTouched = true;
        DescriptionBox.Focus(FocusState.Programmatic);
        SyncToolbar();
    }

    // Typing at a link's edge is plain text, then the toolbar shows the caret's format
    private void OnDescriptionSelectionChanged(object sender, RoutedEventArgs e)
    {
        RichDescription.PlainInsertion(DescriptionBox);
        SyncToolbar();
    }

    // Toolbar toggles show the selection's format (a link's underline is only how it looks, so it doesn't count)
    private void SyncToolbar()
    {
        var format = DescriptionBox.Document.Selection.CharacterFormat;
        var list = DescriptionBox.Document.Selection.ParagraphFormat.ListType;

        BoldButton.IsChecked = format.Bold == FormatEffect.On;
        ItalicButton.IsChecked = format.Italic == FormatEffect.On;
        UnderlineButton.IsChecked = RichDescription.IsUnderlined(format);
        BulletsButton.IsChecked = list == MarkerType.Bullet;
        NumbersButton.IsChecked = list is not (MarkerType.None or MarkerType.Undefined or MarkerType.Bullet);
    }

    // Paste is always plain text: no pictures, objects, or foreign formatting (Review Focus 3)
    private void OnDescriptionPaste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        _owner?.Fire(PastePlainTextAsync, "editor.paste.failed");
    }

    // The text lands only in the editor it was pasted into (the panel may have moved on while the clipboard was read)
    private async Task PastePlainTextAsync()
    {
        var editor = Editor;
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
