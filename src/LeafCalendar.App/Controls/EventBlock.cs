using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.Text;

namespace LeafCalendar.App.Controls;

/// <summary>
/// One timed event card: a tinted fill, a 3 px accent bar in the event's color, the title, and
/// (when tall enough) the time. Declined events show as an outline with struck-through text, and
/// unanswered or tentative events as an outline. Focus time, out of office, and birthdays get an icon.
/// </summary>
public sealed partial class EventBlock : Grid
{
    // Segoe Fluent Icons; if a glyph renders empty on this Windows build, swap it for E787 (Calendar)
    const string FocusGlyph    = ""; // Stopwatch
    const string AwayGlyph     = ""; // Airplane
    const string BirthdayGlyph = ""; // Giftbox

    // Bottom strip that resizes instead of moving (only on cards tall enough to have one)
    const double ResizeZone = 6;
    static InputCursor? _resizeCursor;
    readonly TimeGridView? _owner;
    bool _inResizeZone;

    readonly Border _card = new() { CornerRadius = new CornerRadius(4) };
    readonly Rectangle _accent = new() { Width = 3, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2) };
    readonly TextBlock _title = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2 };
    readonly TextBlock _time = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly FontIcon _icon = new() { FontSize = 11, Margin = new Thickness(0, 1, 4, 0), Visibility = Visibility.Collapsed };
    CalendarOccurrence? _occurrence;
    Action<CalendarOccurrence>? _select;

    /// <summary>Builds the card; <paramref name="owner"/> (the time grid) runs its drags and edits.</summary>
    public EventBlock(TimeGridView? owner = null)
    {
        _owner = owner;
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(_icon);
        titleRow.Children.Add(_title);

        var text = new StackPanel { Margin = new Thickness(9, 3, 4, 2) };
        text.Children.Add(titleRow);
        text.Children.Add(_time);

        var inner = new Grid();
        inner.Children.Add(_accent);
        inner.Children.Add(text);

        _card.Child = inner;
        Children.Add(_card);

        Tapped += (_, e) =>
        {
            if (_occurrence is { } o)
            {
                _select?.Invoke(o);
            }

            e.Handled = true;
        };

        // Drag To Move, Bottom Edge To Resize, Double-Click To Edit
        PointerPressed += OnPointerPressed;
        PointerMoved   += (_, e) =>
        {
            // Swap The Cursor Only When The Pointer Crosses Into Or Out Of The Resize Strip
            var inZone = IsResizeZone(e.GetCurrentPoint(this).Position.Y);
            if (inZone == _inResizeZone)
            {
                return;
            }

            _inResizeZone   = inZone;
            ProtectedCursor = inZone ? _resizeCursor ??= InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth) : null;
        };
        DoubleTapped   += (_, e) =>
        {
            if (_occurrence is { } o && _owner is { } owner)
            {
                owner.ViewModel.Select(o);
                owner.ViewModel.BeginEdit();
            }

            e.Handled = true;
        };

        // Right-Click Menu, And The Event Under The Mouse (X toggles it)
        RightTapped += (_, e) =>
        {
            if (_occurrence is { } o && _owner is { } owner)
            {
                EventContextMenu.Show(this, e.GetPosition(this), owner.ViewModel, o);
            }

            e.Handled = true;
        };
        PointerEntered += (_, _) => _owner?.ViewModel.PointerEvent = _occurrence;
        PointerExited  += (_, _) => _owner?.ViewModel.PointerEvent = null;
    }

    // The grid decides whether the press becomes a drag (it waits for the pointer to move a few pixels)
    void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (_owner is null || _occurrence is not { } o || !point.Properties.IsLeftButtonPressed || e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            return;
        }

        _owner.BeginEventDrag(o, e, resize: IsResizeZone(point.Position.Y));
    }

    bool IsResizeZone(double y) => HoldsEnd && ActualHeight >= ResizeZone * 3 && y >= ActualHeight - ResizeZone;

    /// <summary>True when this card shows the event's real end (an overnight event only resizes from its last day).</summary>
    public bool HoldsEnd { get; set; } = true;

    /// <summary>The automation ID tests use: <c>Event_{id}_{UTC yyyyMMddHHmm}</c>.</summary>
    public static string AutomationIdFor(CalendarOccurrence o) =>
        string.Create(CultureInfo.InvariantCulture, $"Event_{o.EventId}_{o.Start.UtcDateTime:yyyyMMddHHmm}");

    /// <summary>Shows <paramref name="occurrence"/>.</summary>
    public void Bind(CalendarOccurrence occurrence, EventPalette palette, string timeText, bool selected, bool compact, Action<CalendarOccurrence> select)
    {
        _occurrence = occurrence;
        _select     = select;

        var declined = occurrence.SelfResponse == ResponseStatus.Declined;
        var outlined = declined || occurrence.SelfResponse is ResponseStatus.NeedsAction or ResponseStatus.Tentative;
        var accent   = LeafBrushes.FromHex(palette.Accent);

        // Card
        _card.Background      = declined ? LeafBrushes.Transparent : outlined ? LeafBrushes.FromHex("#33" + palette.Fill[1..]) : LeafBrushes.FromHex(palette.Fill);
        _card.BorderBrush     = accent;
        _card.BorderThickness = new Thickness(selected ? 2 : outlined ? 1 : 0);
        _accent.Fill          = accent;
        _accent.Visibility    = declined ? Visibility.Collapsed : Visibility.Visible;

        // Text
        var textBrush = outlined ? null : LeafBrushes.FromHex(palette.Text);
        _title.Text            = occurrence.Title;
        _title.TextDecorations = declined ? TextDecorations.Strikethrough : TextDecorations.None;
        _title.MaxLines        = compact ? 1 : 2;
        _time.Text             = timeText;
        _time.Visibility       = compact ? Visibility.Collapsed : Visibility.Visible;
        if (textBrush is null)
        {
            _title.ClearValue(TextBlock.ForegroundProperty);
            _time.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            _title.Foreground = textBrush;
            _time.Foreground  = LeafBrushes.FromHex(palette.SecondaryText);
        }

        // Kind Icon
        (_icon.Glyph, _icon.Visibility) = occurrence.Kind switch
        {
            EventKind.FocusTime   => (FocusGlyph, Visibility.Visible),
            EventKind.OutOfOffice => (AwayGlyph, Visibility.Visible),
            EventKind.Birthday    => (BirthdayGlyph, Visibility.Visible),
            _                     => ("", Visibility.Collapsed),
        };
        _icon.Foreground = textBrush ?? accent;

        AutomationProperties.SetName(this, $"{occurrence.Title}, {timeText}");
        AutomationProperties.SetAutomationId(this, AutomationIdFor(occurrence));
    }
}
