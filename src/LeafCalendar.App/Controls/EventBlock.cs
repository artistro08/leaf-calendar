using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.Text;

namespace LeafCalendar.App.Controls;

/// <summary>
/// One timed event card: a tinted fill, a 3 px accent bar in the event's color (a bar per calendar when the same event
/// is on several shown calendars), the title, and
/// (when tall enough) the time. Declined events show as an outline with struck-through text, and
/// unanswered or tentative events as an outline. A selected card is filled with the event's color at full strength.
/// Focus time, out of office, and birthdays get an icon. A Sunsama task's tracked time is a striped texture over the card
/// from the top (in a contrast theme, a highlight edge line where it ends).
/// </summary>
public sealed partial class EventBlock : Grid
{
    // Segoe Fluent Icons; if a glyph renders empty on this Windows build, swap it for E787 (Calendar)
    private const string FocusGlyph = ""; // Stopwatch
    private const string AwayGlyph = ""; // Airplane
    private const string BirthdayGlyph = ""; // Giftbox

    // Bottom strip that resizes instead of moving (only on cards tall enough to have one)
    private const double ResizeZone = 6;
    private static InputCursor? s_resizeCursor;
    private readonly TimeGridView? _owner;
    private bool _inResizeZone;

    // Accent bars: 3 px wide, 2 px from the card's edges and from each other (one per calendar the event is on)
    private const double StripeWidth = 3;
    private const double StripeGap = 2;

    private readonly Border _card = new() { CornerRadius = new CornerRadius(4) };
    private readonly StackPanel _accents = new() { Orientation = Orientation.Horizontal, Spacing = StripeGap, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(StripeGap) };
    private readonly StackPanel _text = new() { Margin = new Thickness(9, 3, 4, 2) };
    private readonly TextBlock _title = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2 };
    private readonly TextBlock _time = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly FontIcon _icon = new() { FontSize = 11, Margin = new Thickness(0, 1, 4, 0), Visibility = Visibility.Collapsed };
    private readonly ToolTip _tip = new();

    // Tracked Time: the top row's share of the card, under the text
    private readonly Grid _trackedArea = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _tracked = new();
    private CalendarOccurrence? _occurrence;
    private Action<CalendarOccurrence>? _select;
    private string _timeText = "";

    // The card's give while a read-only event is dragged (ElasticNudge); created here and never read back
    private readonly TranslateTransform _pull = new();

    /// <summary>Builds the card; <paramref name="owner"/> (the time grid) runs its drags and edits.</summary>
    public EventBlock(TimeGridView? owner = null)
    {
        _owner = owner;
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(_icon);
        titleRow.Children.Add(_title);

        _text.Children.Add(titleRow);
        _text.Children.Add(_time);

        _trackedArea.RowDefinitions.Add(new RowDefinition());
        _trackedArea.RowDefinitions.Add(new RowDefinition());
        _trackedArea.Children.Add(_tracked);

        var inner = new Grid();
        inner.Children.Add(_trackedArea);
        inner.Children.Add(_accents);
        inner.Children.Add(_text);

        _card.Child = inner;
        RenderTransform = _pull;
        Children.Add(_card);
        if (owner is not null)
        {
            ToolTipService.SetToolTip(this, _tip);
        }

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
        PointerMoved += (_, e) =>
        {
            // Swap The Cursor Only When The Pointer Crosses Into Or Out Of The Resize Strip
            var inZone = IsResizeZone(e.GetCurrentPoint(this).Position.Y);
            if (inZone == _inResizeZone)
            {
                return;
            }

            _inResizeZone = inZone;
            ProtectedCursor = inZone ? s_resizeCursor ??= InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth) : null;
        };
        DoubleTapped += (_, e) =>
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
        PointerEntered += (_, _) =>
        {
            _owner?.ViewModel.PointerEvent = _occurrence;

            // Hover Tooltip: the title as the pointer arrives, so a pass over the grid costs nothing
            _tip.Content = _occurrence?.Title;
        };

        // The Title, Time, And Location Once The Tooltip Opens (the location is a database lookup)
        _tip.Opened += (_, _) =>
        {
            if (_occurrence is { } o && _owner is { } owner)
            {
                _tip.Content = owner.ViewModel.HoverText(o, _timeText);
            }
        };
        PointerExited += (_, _) => _owner?.ViewModel.PointerEvent = null;
    }

    // Tracked Time From The Top: stripes over the card's color, or a highlight edge line in a contrast theme (the name says it too)
    private void BindTracked(double fill, string accent)
    {
        if (fill <= 0)
        {
            _trackedArea.Visibility = Visibility.Collapsed;
            return;
        }

        fill = Math.Min(fill, 1);
        _trackedArea.RowDefinitions[0].Height = new GridLength(fill, GridUnitType.Star);
        _trackedArea.RowDefinitions[1].Height = new GridLength(1 - fill, GridUnitType.Star);
        _trackedArea.Visibility = Visibility.Visible;
        var full = fill >= 1;
        _tracked.CornerRadius = new CornerRadius(3, 3, full ? 3 : 0, full ? 3 : 0);
        if (LeafBrushes.HighContrast)
        {
            _tracked.Background = LeafBrushes.Transparent;
            _tracked.BorderBrush = LeafBrushes.TrackedEdge;
            _tracked.BorderThickness = new Thickness(0, 0, 0, 2);
        }
        else
        {
            _tracked.Background = LeafBrushes.TrackedStripes(accent);
            _tracked.BorderThickness = new Thickness(0);
        }
    }

    // One bar per color, and the text moved clear of them (a lone bar leaves the usual 9 px)
    private void BindStripes(IReadOnlyList<string> colors)
    {
        while (_accents.Children.Count < colors.Count)
        {
            _accents.Children.Add(new Rectangle { Width = StripeWidth, RadiusX = StripeWidth / 2, RadiusY = StripeWidth / 2 });
        }

        for (var i = 0; i < _accents.Children.Count; i++)
        {
            var bar = (Rectangle)_accents.Children[i];
            bar.Visibility = i < colors.Count ? Visibility.Visible : Visibility.Collapsed;
            if (i < colors.Count)
            {
                bar.Fill = LeafBrushes.FromHex(colors[i]);
            }
        }

        var left = StripeGap + colors.Count * StripeWidth + (colors.Count - 1) * StripeGap + 4;
        _text.Margin = new Thickness(left, 3, 4, 2);
    }

    // The grid decides whether the press becomes a drag (it waits for the pointer to move a few pixels)
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (_owner is null || _occurrence is not { } o || !point.Properties.IsLeftButtonPressed || e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            return;
        }

        _owner.BeginEventDrag(o, e, IsResizeZone(point.Position.Y), this, _pull);
    }

    private bool IsResizeZone(double y) => HoldsEnd && ActualHeight >= ResizeZone * 3 && y >= ActualHeight - ResizeZone;

    /// <summary>True when this card shows the event's real end (an overnight event only resizes from its last day).</summary>
    public bool HoldsEnd { get; set; } = true;

    /// <summary>The automation ID tests use: <c>Event_{id}_{UTC yyyyMMddHHmm}</c>.</summary>
    public static string AutomationIdFor(CalendarOccurrence o) =>
        string.Create(CultureInfo.InvariantCulture, $"Event_{o.EventId}_{o.Start.UtcDateTime:yyyyMMddHHmm}");

    /// <summary>
    /// Shows <paramref name="occurrence"/>, faded when <paramref name="past"/>. An event shown on several calendars passes
    /// each one's bar color in <paramref name="stripes"/> (its own first) and gets a bar per calendar; otherwise the card
    /// has its one accent bar. <paramref name="trackedFill"/> (0 to 1) is how much of the card Sunsama's tracked texture
    /// covers, and <paramref name="spokenSuffix"/> follows the time in the tooltip and spoken name (a task's channel and
    /// tracked total).
    /// </summary>
    public void Bind(CalendarOccurrence occurrence, EventPalette palette, string timeText, bool selected, bool compact, Action<CalendarOccurrence> select, bool past, IReadOnlyList<string>? stripes = null, double trackedFill = 0, string spokenSuffix = "")
    {
        _occurrence = occurrence;
        _select = select;
        // Spoken And Hovered With AM/PM (the card itself shows the grid form, which its place on the grid disambiguates)
        _timeText = (_owner is { } owner ? TimeLabels.Range(occurrence.Start, occurrence.End, owner.ViewModel.Zone, owner.ViewModel.Settings.Use24HourTime) : timeText) + spokenSuffix;
        BindTracked(trackedFill, palette.Accent);

        // A Selected Card Is Always Solid (its palette is the accent at full strength), Even When Unanswered Or Declined
        var declined = occurrence.SelfResponse == ResponseStatus.Declined;
        var outlined = !selected && (declined || occurrence.SelfResponse is ResponseStatus.NeedsAction or ResponseStatus.Tentative);
        var accent = LeafBrushes.FromHex(palette.Accent);

        // Card
        _card.Background = !outlined ? LeafBrushes.FromHex(palette.Fill) : declined ? LeafBrushes.Transparent : LeafBrushes.FromHex("#33" + palette.Fill[1..]);
        _card.BorderBrush = accent;
        _card.BorderThickness = LeafBrushes.CardBorder(selected, outlined);
        _accents.Visibility = declined && !selected ? Visibility.Collapsed : Visibility.Visible;
        BindStripes(stripes is { Count: > 1 } ? stripes : [palette.Accent]);

        // Text
        var textBrush = outlined ? null : LeafBrushes.FromHex(palette.Text);
        _title.Text = occurrence.Title;
        _title.TextDecorations = declined ? TextDecorations.Strikethrough : TextDecorations.None;
        _title.MaxLines = compact ? 1 : 2;
        _time.Text = timeText;
        _time.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (textBrush is null)
        {
            _title.ClearValue(TextBlock.ForegroundProperty);
            _time.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            _title.Foreground = textBrush;
            _time.Foreground = LeafBrushes.FromHex(palette.SecondaryText);
        }

        // Kind Icon
        (_icon.Glyph, _icon.Visibility) = occurrence.Kind switch
        {
            EventKind.FocusTime => (FocusGlyph, Visibility.Visible),
            EventKind.OutOfOffice => (AwayGlyph, Visibility.Visible),
            EventKind.Birthday => (BirthdayGlyph, Visibility.Visible),
            _ => ("", Visibility.Collapsed),
        };
        _icon.Foreground = textBrush ?? accent;

        // Past Events Fade (still readable)
        // (past cards arrive with a faded palette; their text stays full strength). Selection is published too, for UI tests
        AutomationProperties.SetItemStatus(this, string.Join(';', new[] { past ? "Past" : "", selected ? "Selected" : "" }.Where(s => s.Length > 0)));

        AutomationProperties.SetName(this, _owner?.ViewModel.CardName(occurrence, _timeText) ?? $"{occurrence.Title}, {_timeText}");
        AutomationProperties.SetAutomationId(this, AutomationIdFor(occurrence));
    }
}
