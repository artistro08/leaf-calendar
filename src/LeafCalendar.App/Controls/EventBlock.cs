using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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

    readonly Border _card = new() { CornerRadius = new CornerRadius(4) };
    readonly Rectangle _accent = new() { Width = 3, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2) };
    readonly TextBlock _title = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2 };
    readonly TextBlock _time = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly FontIcon _icon = new() { FontSize = 11, Margin = new Thickness(0, 1, 4, 0), Visibility = Visibility.Collapsed };
    CalendarOccurrence? _occurrence;
    Action<CalendarOccurrence>? _select;

    /// <summary>Builds the card.</summary>
    public EventBlock()
    {
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
    }

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
