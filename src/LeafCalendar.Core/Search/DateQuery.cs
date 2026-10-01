using System.Globalization;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Search;

/// <summary>Reads a typed date in the command menu ("tomorrow", "next fri", "oct 12", "10/12", "in 2 weeks").</summary>
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
    /// Understands today, tomorrow, yesterday; "in N days" or "in N weeks" (N up to 3660); a weekday, full or 3-letter
    /// (the soonest one on or after today; "next" adds a week); ISO <c>yyyy-MM-dd</c>; <c>M/d</c> and <c>M/d/yyyy</c>;
    /// and month names before or after the day, with an optional year. A month and day without a year mean this year,
    /// unless that's more than 2 months ago, which means next year. Impossible dates ("feb 30") are refused.
    /// </remarks>
    public static bool TryParse(string? text, DateOnly today, out DateOnly date)
    {
        date = default;

        // Length Guard
        var input = (text ?? "").Trim().ToLowerInvariant();
        if (input.Length is < 1 or > 40)
        {
            return false;
        }

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

        // In N Days Or Weeks
        if (InDays().Match(input) is { Success: true } inDays)
        {
            var count = int.Parse(inDays.Groups[1].Value, CultureInfo.InvariantCulture);
            var days  = inDays.Groups[2].Value.StartsWith('w') ? count * 7 : count;
            if (count is < 1 or > 3660 || today.DayNumber + days > DateOnly.MaxValue.DayNumber)
            {
                return false;
            }

            date = today.AddDays(days);
            return true;
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

    [GeneratedRegex(@"^in (\d{1,4}) (days?|weeks?)$")]
    private static partial Regex InDays();
}
