namespace LeafCalendar.Core.Sunsama;

/// <summary>What a saved Sunsama row is.</summary>
public enum SunsamaBlockKind
{
    /// <summary>One of Sunsama's automatic placements; drawn like an unanswered invitation.</summary>
    Projected,

    /// <summary>A timebox on a non-Google calendar (Sunsama's own); drawn like a regular event.</summary>
    Timebox,

    /// <summary>A task already shown as a Google event in Leaf Calendar; draws nothing, adds its tracked total to that event.</summary>
    Matched,
}

/// <summary>
/// One Sunsama task time as Leaf Calendar saves and shows it (spec 2026-10-16, section 5.2). Text is untrusted and shown plain.
/// </summary>
/// <param name="Day">The local day (in the Sunsama zone) it was fetched for; each day's rows are replaced together.</param>
/// <param name="TaskId">Sunsama's task ID.</param>
/// <param name="Title">The task title.</param>
/// <param name="Channel">The task's channel ("" when none).</param>
/// <param name="Start">Start instant. For <see cref="SunsamaBlockKind.Matched"/>, the Google occurrence's start.</param>
/// <param name="End">End instant. For <see cref="SunsamaBlockKind.Matched"/>, the Google occurrence's end.</param>
/// <param name="EstimateMinutes">The planned time, or null.</param>
/// <param name="TrackedMinutes">The task's whole tracked total, or null when none.</param>
/// <param name="Completed">The task is done.</param>
/// <param name="Kind">Projected, timebox or matched.</param>
/// <param name="MatchedAccountId">The matched Google occurrence's account; set only for <see cref="SunsamaBlockKind.Matched"/>.</param>
/// <param name="MatchedCalendarId">The matched Google occurrence's calendar; set only for <see cref="SunsamaBlockKind.Matched"/>.</param>
/// <param name="MatchedEventId">The matched Google occurrence's event ID; set only for <see cref="SunsamaBlockKind.Matched"/>.</param>
public sealed record SunsamaBlock(
    DateOnly Day,
    string TaskId,
    string Title,
    string Channel,
    DateTimeOffset Start,
    DateTimeOffset End,
    int? EstimateMinutes,
    int? TrackedMinutes,
    bool Completed,
    SunsamaBlockKind Kind,
    string? MatchedAccountId,
    string? MatchedCalendarId,
    string? MatchedEventId);
