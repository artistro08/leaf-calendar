using System.Globalization;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Search;

/// <summary>Reads a typed date in the command menu ("tomorrow", "next fri", "nov 5th", "10/12", "10 weeks", "3 days ago").</summary>
public static partial class DateQuery
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // Month and day without a year, then with one
    static readonly string[] MonthDay     = ["MMM d", "MMMM d", "d MMM", "d MMMM", "M/d"];
    static readonly string[] MonthDayYear = ["MMM d yyyy", "MMMM d yyyy", "d MMM yyyy", "d MMMM yyyy", "M/d/yyyy", "yyyy-MM-dd"];

    /// <summary>
    /// Reads <paramref name="text"/> as a date relative to <paramref name="today"/>; false when it isn't one.
    /// </summary>
    /// <remarks>
    /// Understands today, tomorrow, yesterday; a count of days, weeks, months, or years ahead ("3 days", "10 weeks",
    /// "in 2 weeks", "10 weeks from now", "a month"; up to 3660) or back ("3 days ago"); next or last week, month, or
    /// year; a weekday, full or 3-letter (the soonest one on or after today; "next" adds a week); ISO
    /// <c>yyyy-MM-dd</c>; <c>M/d</c> and <c>M/d/yyyy</c>; and month names before or after the day ("nov 5th",
    /// "5 nov", "Nov 5th, 2027"), with an optional year. A month and day without a year mean this year, unless that's
    /// more than 2 months ago, which means next year. Impossible dates ("feb 30") are refused.
    /// </remarks>
    public static bool TryParse(string? text, DateOnly today, out DateOnly date)
    {
        date = default;

        // Length Guard, Then One Space Between Words, No Commas, And "5th" Read As "5"
        var input = (text ?? "").Trim().ToLowerInvariant();
        if (input.Length is < 1 or > 40)
        {
            return false;
        }

        input = Spaces().Replace(Ordinal().Replace(input.Replace(',', ' ').Replace('.', ' '), "$1"), " ").Trim();

        // Longer Short Names Than .NET's Three Letters ("sept 8", "tues", "thurs"), And Numbers In Words ("two weeks")
        input = LongerShortNames().Replace(input, m => m.Value[..3]);
        input = NumberWords().Replace(input, m => NumberOf(m.Value).ToString(CultureInfo.InvariantCulture));

        // Relative Words
        switch (input)
        {
            case "today":
                date = today;
                return true;

            case "tomorrow":
                date = today.AddDays(1);
                return true;

            case "yesterday":
                date = today.AddDays(-1);
                return true;
        }

        // Next Or Last Week, Month, Or Year
        if (NextLast().Match(input) is { Success: true } nextLast)
        {
            return TryShift(today, nextLast.Groups[1].Value == "next" ? 1 : -1, nextLast.Groups[2].Value, out date);
        }

        // A Count Of Days, Weeks, Months, Or Years Ahead ("3 days", "in 3 days", "3 days from now") Or Back ("3 days ago")
        if (Count().Match(input) is { Success: true } counted)
        {
            var number = counted.Groups[1].Value;
            var count  = number is "a" or "an" ? 1 : int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (count is < 1 or > 3660)
            {
                return false;
            }

            return TryShift(today, counted.Groups[3].Success ? -count : count, counted.Groups[2].Value, out date);
        }

        // Weekday, Optionally "next"
        var next    = input.StartsWith("next ", StringComparison.Ordinal);
        var weekday = next ? input[5..].Trim() : input;
        if (WeekdayOf(weekday) is { } day)
        {
            date = today.AddDays(((int)day - (int)today.DayOfWeek + 7) % 7 + (next ? 7 : 0));
            return true;
        }

        // With A Year
        if (DateOnly.TryParseExact(input, MonthDayYear, English, DateTimeStyles.None, out date))
        {
            return true;
        }

        // Without A Year: This Year, Or Next Year When That's More Than 2 Months Ago
        var withYear = input + (input.Contains('/', StringComparison.Ordinal) ? "/" : " ") + today.Year.ToString(CultureInfo.InvariantCulture);
        string[] formats = [.. MonthDay.Select(f => f + (f.Contains('/', StringComparison.Ordinal) ? "/yyyy" : " yyyy"))];
        if (!DateOnly.TryParseExact(withYear, formats, English, DateTimeStyles.None, out date))
        {
            date = default;
            return false;
        }

        if (date < today.AddMonths(-2) && date.Year < DateOnly.MaxValue.Year)
        {
            date = date.AddYears(1);
        }

        return true;
    }

    // Today moved by a count of a unit ("day", "weeks", "month", "year"), within the calendar's range
    static bool TryShift(DateOnly today, int count, string unit, out DateOnly date)
    {
        date = default;
        try
        {
            date = unit[0] switch
            {
                'd' => today.AddDays(count),
                'w' => today.AddDays(count * 7),
                'm' => today.AddMonths(count),
                _   => today.AddYears(count),
            };
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>"Mon, Oct 12", plus ", 2027" when the year isn't today's.</summary>
    public static string Label(DateOnly date, DateOnly today) =>
        date.ToString(date.Year == today.Year ? "ddd, MMM d" : "ddd, MMM d, yyyy", English);

    /// <summary>A full or 3-letter English weekday name.</summary>
    static DayOfWeek? WeekdayOf(string text)
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var name = English.DateTimeFormat.GetDayName(day).ToLowerInvariant();
            if (text == name || text == name[..3])
            {
                return day;
            }
        }

        return null;
    }

    // ASCII digits only (\d also matches other scripts' digits)
    [GeneratedRegex(@"^(?:in )?(a|an|[0-9]{1,4}) (days?|weeks?|months?|years?)(?: (?:from (?:now|today)|ahead)|( ago))?$")]
    private static partial Regex Count();

    [GeneratedRegex(@"^(next|last) (week|month|year)$")]
    private static partial Regex NextLast();

    // "5th", "1st", "22nd", "3rd" after a day number
    [GeneratedRegex(@"\b([0-9]{1,2})(?:st|nd|rd|th)\b")]
    private static partial Regex Ordinal();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // A month or weekday written with more than its first three letters but not in full
    [GeneratedRegex(@"\b(sept|tues|thur|thurs|weds)\b")]
    private static partial Regex LongerShortNames();

    // One to twenty, and the tens to ninety, as words
    [GeneratedRegex(@"\b(one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)\b")]
    private static partial Regex NumberWords();

    static readonly string[] Units = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty"];
    static readonly string[] Tens  = ["thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    static int NumberOf(string word) =>
        Array.IndexOf(Units, word) is var unit && unit >= 0 ? unit + 1 : (Array.IndexOf(Tens, word) + 3) * 10;
}
