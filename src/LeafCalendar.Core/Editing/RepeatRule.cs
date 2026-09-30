using System.Globalization;

namespace LeafCalendar.Core.Editing;

/// <summary>How often a simple rule repeats.</summary>
public enum RepeatFrequency
{
    /// <summary>Every N days.</summary>
    Daily,

    /// <summary>Every N weeks, optionally on chosen weekdays.</summary>
    Weekly,

    /// <summary>Every N months on the start's day of the month.</summary>
    Monthly,

    /// <summary>Every N years on the start's date.</summary>
    Yearly,
}

/// <summary>
/// The repeat rules Leaf's editor can show and write: FREQ, INTERVAL, plain weekdays for weekly rules,
/// and an end (UNTIL or COUNT). Anything richer (ordinal weekdays, BYSETPOS, BYMONTH, and so on) parses
/// as null, and the editor keeps such a rule untouched.
/// </summary>
/// <seealso href="https://datatracker.ietf.org/doc/html/rfc5545#section-3.3.10"/>
public sealed record RepeatRule(RepeatFrequency Frequency, int Interval = 1, IReadOnlyList<DayOfWeek>? Weekdays = null, DateOnly? Until = null, int? Count = null)
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>Reads an <c>RRULE:</c> line. <see cref="Until"/> is the last local date in <paramref name="zone"/>.</summary>
    public static RepeatRule? Parse(string line, TimeZoneInfo zone)
    {
        if (!line.StartsWith("RRULE:", StringComparison.Ordinal))
        {
            return null;
        }

        RepeatFrequency? frequency = null;
        var interval = 1;
        var weekdays = new List<DayOfWeek>();
        DateOnly? until = null;
        int? count = null;

        foreach (var part in line["RRULE:".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair  = part.Split('=', 2);
            var value = pair.Length == 2 ? pair[1] : "";
            switch (pair[0])
            {
                case "FREQ":
                    frequency = value switch
                    {
                        "DAILY"   => RepeatFrequency.Daily,
                        "WEEKLY"  => RepeatFrequency.Weekly,
                        "MONTHLY" => RepeatFrequency.Monthly,
                        "YEARLY"  => RepeatFrequency.Yearly,
                        _         => null,
                    };
                    if (frequency is null)
                    {
                        return null;
                    }

                    break;
                case "INTERVAL":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out interval) || interval < 1)
                    {
                        return null;
                    }

                    break;
                case "BYDAY":
                    foreach (var code in value.Split(','))
                    {
                        var index = Array.IndexOf(DayCodes, code);
                        if (index < 0)
                        {
                            return null;
                        }

                        weekdays.Add((DayOfWeek)index);
                    }

                    break;
                case "UNTIL":
                    until = ParseUntil(value, zone);
                    if (until is null)
                    {
                        return null;
                    }

                    break;
                case "COUNT":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out var c) || c < 1)
                    {
                        return null;
                    }

                    count = c;
                    break;
                case "WKST":
                    break;
                default:
                    return null;
            }
        }

        // Weekdays Only Mean Something For Weekly Rules
        if (frequency is not { } f || (weekdays.Count > 0 && f != RepeatFrequency.Weekly))
        {
            return null;
        }

        return new RepeatRule(f, interval, weekdays.Count > 0 ? weekdays : null, until, count);
    }

    /// <summary>The <c>RRULE:</c> line. A timed rule's UNTIL is the end of that local date, in UTC (Google's form).</summary>
    public string ToRRule(bool isAllDay, TimeZoneInfo zone)
    {
        var parts = new List<string>
        {
            "FREQ=" + Frequency switch
            {
                RepeatFrequency.Daily   => "DAILY",
                RepeatFrequency.Weekly  => "WEEKLY",
                RepeatFrequency.Monthly => "MONTHLY",
                _                       => "YEARLY",
            },
        };

        if (Interval > 1)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"INTERVAL={Interval}"));
        }

        if (Frequency == RepeatFrequency.Weekly && Weekdays is { Count: > 0 } days)
        {
            parts.Add("BYDAY=" + string.Join(",", days.Select(d => DayCodes[(int)d])));
        }

        if (Count is { } count)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"COUNT={count}"));
        }
        else if (Until is { } until)
        {
            parts.Add("UNTIL=" + (isAllDay ? until.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : EndOfDayUtc(until, zone)));
        }

        return "RRULE:" + string.Join(";", parts);
    }

    /// <summary>Plain-English summary, such as "Every 2 weeks on Mon, Wed, until Dec 31, 2026".</summary>
    public string Describe()
    {
        var unit = Frequency switch
        {
            RepeatFrequency.Daily   => ("Every day", "days"),
            RepeatFrequency.Weekly  => ("Weekly", "weeks"),
            RepeatFrequency.Monthly => ("Monthly", "months"),
            _                       => ("Yearly", "years"),
        };

        var text = Interval > 1 ? string.Create(English, $"Every {Interval} {unit.Item2}") : unit.Item1;
        if (Weekdays is { Count: > 0 } days)
        {
            text += " on " + string.Join(", ", days.Select(d => English.DateTimeFormat.GetAbbreviatedDayName(d)));
        }

        if (Count is { } count)
        {
            text += string.Create(English, $", {count} times");
        }
        else if (Until is { } until)
        {
            text += ", until " + until.ToString("MMM d, yyyy", English);
        }

        return text;
    }

    static string EndOfDayUtc(DateOnly day, TimeZoneInfo zone)
    {
        var nextMidnight = zone.IsInvalidTime(day.AddDays(1).ToDateTime(TimeOnly.MinValue))
            ? day.AddDays(1).ToDateTime(TimeOnly.MinValue).AddHours(1)
            : day.AddDays(1).ToDateTime(TimeOnly.MinValue);

        return TimeZoneInfo.ConvertTimeToUtc(nextMidnight, zone).AddSeconds(-1).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }

    static DateOnly? ParseUntil(string value, TimeZoneInfo zone)
    {
        if (DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
        }

        return DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }
}
