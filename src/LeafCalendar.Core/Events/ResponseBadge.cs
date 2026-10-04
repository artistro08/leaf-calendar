namespace LeafCalendar.Core.Events;

/// <summary>The color family a reply is shown in.</summary>
public enum ResponseTone
{
    /// <summary>Hasn't answered (neutral).</summary>
    Neutral,

    /// <summary>Going (success green).</summary>
    Positive,

    /// <summary>Maybe (caution amber).</summary>
    Caution,

    /// <summary>Not going (critical red).</summary>
    Critical,
}

/// <summary>Your reply to an invitation as the details panel's badge shows it: its words and its color.</summary>
public sealed record ResponseBadge(string Label, ResponseTone Tone)
{
    /// <summary>The badge for <paramref name="response"/> ("Going", "Maybe", "Not going", or "Not answered").</summary>
    public static ResponseBadge For(ResponseStatus response) => response switch
    {
        ResponseStatus.Accepted => new("Going", ResponseTone.Positive),
        ResponseStatus.Tentative => new("Maybe", ResponseTone.Caution),
        ResponseStatus.Declined => new("Not going", ResponseTone.Critical),
        _ => new("Not answered", ResponseTone.Neutral),
    };

    /// <summary>The whole line Narrator reads: "Your response: Going".</summary>
    public string Spoken => $"Your response: {Label}";
}
