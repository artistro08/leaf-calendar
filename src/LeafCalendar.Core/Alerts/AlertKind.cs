namespace LeafCalendar.Core.Alerts;

/// <summary>The notifications Leaf schedules and remembers (spec 8.4).</summary>
public enum AlertKind
{
    /// <summary>A reminder at one of the event's reminder times.</summary>
    Reminder,

    /// <summary>The persistent "Join now" at a meeting's start.</summary>
    JoinNow,

    /// <summary>A new or updated invitation.</summary>
    Invite,
}
