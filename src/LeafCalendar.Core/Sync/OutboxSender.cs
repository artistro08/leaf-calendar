using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.Sync;

/// <summary>What one outbox pass did: local data changed, conflicts found, and edits Google refused (undone).</summary>
public readonly record struct SendReport(bool Changed, int Conflicts, int Rejected);

/// <summary>
/// Sends an account's outbox to Google, oldest first.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Accepted: the entry is removed. Later entries for the same event are rebased onto the new ETag,
/// and Google's JSON replaces the local row once nothing else is waiting for that event.</item>
/// <item><c>412</c>, or a patch or move of an event Google deleted: a conflict. That event waits for the user;
/// the rest of the queue keeps going (spec 5.5).</item>
/// <item>A create answered <c>409</c>: an earlier try got through, so Google's copy is fetched and kept.</item>
/// <item>A delete of an event that's already gone: done.</item>
/// <item>Any other <c>4xx</c> (for example no permission): Google will never take it, so the event goes back
/// to Google's version and the entry (and later ones for that event) are dropped and reported.</item>
/// <item>Network failure, <c>401</c>, <c>429</c>, or <c>5xx</c> (writes are never auto-retried): the entry stays
/// pending with its error recorded, the pass stops so the order holds, and the next sync tries again.</item>
/// <item>An entry whose calendar is no longer in the account: dropped (a move whose destination is gone is
/// undone like a refused edit).</item>
/// </list>
/// Held entries (the undo window) and entries behind a held or conflicted entry for the same event wait.
/// Only sequence numbers and statuses are logged.
/// </remarks>
public sealed class OutboxSender(GoogleCalendarClient google, LeafDatabase database, AppLog log, TimeProvider time)
{
    /// <summary>Sends what can be sent now.</summary>
    /// <exception cref="Auth.AccountNeedsSignInException">The account's refresh token was rejected (the outbox is kept).</exception>
    public async Task<SendReport> SendAsync(string accountId, CancellationToken ct)
    {
        IReadOnlyList<OutboxEntry> queue;
        HashSet<string> waiting;
        HashSet<string> calendars;
        using (var conn = database.Open())
        {
            queue     = OutboxStore.Pending(conn, accountId);
            waiting   = [.. ConflictStore.GetAll(conn).Where(c => c.Entry.AccountId == accountId).Select(c => c.Entry.EventId)];
            calendars = [.. CalendarStore.GetForAccount(conn, accountId).Select(c => c.Id)];
        }

        var changed   = false;
        var conflicts = 0;
        var rejected  = 0;
        var now       = time.GetUtcNow();

        foreach (var queued in queue)
        {
            // Re-Read: an earlier send may have rebased it, and undo may have removed it
            if (Reload(queued.Seq) is not { State: OutboxState.Pending } entry)
            {
                continue;
            }

            // Held, Or Behind A Held Or Conflicted Edit Of The Same Event
            if (waiting.Contains(entry.EventId) || entry.NotBefore > now)
            {
                waiting.Add(entry.EventId);
                continue;
            }

            // Calendar Removed From The Account: Google can't take it
            if (!calendars.Contains(entry.CalendarId))
            {
                Drop(entry.Seq);
                changed = true;
                continue;
            }

            string? googleJson;
            try
            {
                if (!calendars.Contains(LocalCalendarOf(entry)))
                {
                    throw new ArgumentException("The move's destination calendar is gone.");
                }

                googleJson = await SendOneAsync(entry, ct);
            }
            catch (EventGoneException) when (entry.Operation is OutboxOperation.Delete or OutboxOperation.Rsvp)
            {
                googleJson = null;
            }
            catch (Exception ex) when (ex is PreconditionFailedException or EventGoneException)
            {
                var current = ex is EventGoneException ? null : await google.GetEventAsync(entry.AccountId, entry.CalendarId, entry.EventId, ct);
                RecordConflict(entry, current);
                waiting.Add(entry.EventId);
                conflicts++;
                changed = true;
                continue;
            }
            catch (Exception ex) when (ex is ArgumentException || (ex is GoogleApiException api && IsPermanent(api)))
            {
                await RejectAsync(entry, ct);
                log.Info("outbox.rejected", ex is GoogleApiException refused ? $"seq={entry.Seq} status={(int)refused.Status}" : $"seq={entry.Seq}");
                waiting.Add(entry.EventId);
                rejected++;
                changed = true;
                continue;
            }
            catch (Exception ex) when (IsTemporary(ex, ct))
            {
                var error = ex is GoogleApiException api ? $"status {(int)api.Status}" : "network";
                using var conn = database.Open();
                OutboxStore.RecordAttempt(conn, entry.Seq, error);
                log.Info("outbox.deferred", $"seq={entry.Seq} error={error}");
                break;
            }

            Accept(entry, googleJson);
            changed = true;
        }

        return new SendReport(changed, conflicts, rejected);
    }

    /// <summary>The <c>etag</c> of Google's event JSON, or null.</summary>
    /// <exception cref="JsonException">The JSON is invalid.</exception>
    internal static string? EtagOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("etag", out var etag) && etag.ValueKind == JsonValueKind.String
            ? etag.GetString()
            : null;
    }

    /// <summary>
    /// The calendar the entry's event is stored under locally. A move is queued under its source calendar,
    /// but the editor already moved the row to the destination (<see cref="OutboxEntry.Payload"/>).
    /// </summary>
    internal static string LocalCalendarOf(OutboxEntry entry) =>
        entry.Operation == OutboxOperation.Move ? entry.Payload ?? entry.CalendarId : entry.CalendarId;

    async Task<string?> SendOneAsync(OutboxEntry entry, CancellationToken ct)
    {
        switch (entry.Operation)
        {
            case OutboxOperation.Create:
                try
                {
                    return await google.InsertEventAsync(entry.AccountId, entry.CalendarId, entry.Payload ?? "{}", entry.SendUpdates, ct);
                }
                catch (DuplicateEventException)
                {
                    // An earlier try got through before the connection dropped (the ID is Leaf's own)
                    return await google.GetEventAsync(entry.AccountId, entry.CalendarId, entry.EventId, ct) ?? throw new EventGoneException();
                }

            case OutboxOperation.Patch:
                return await google.PatchEventAsync(entry.AccountId, entry.CalendarId, entry.EventId, entry.Payload ?? "{}", entry.BaseEtag, entry.SendUpdates, ct);

            case OutboxOperation.Delete:
                await google.DeleteEventAsync(entry.AccountId, entry.CalendarId, entry.EventId, entry.BaseEtag, entry.SendUpdates, ct);
                return null;

            case OutboxOperation.Move:
                return await google.MoveEventAsync(entry.AccountId, entry.CalendarId, entry.EventId, LocalCalendarOf(entry), entry.SendUpdates, ct);

            default:
                return await SendReplyAsync(entry, ct);
        }
    }

    // A reply changes only your own answer, so it goes on Google's latest copy and never becomes a conflict
    async Task<string> SendReplyAsync(OutboxEntry entry, CancellationToken ct)
    {
        var reply    = JsonNode.Parse(entry.Payload ?? "{}") as JsonObject;
        var response = EventJson.ParseResponse((string?)reply?["responseStatus"]);
        var note     = (string?)reply?["comment"];

        for (var attempt = 0; ; attempt++)
        {
            var current = await google.GetEventAsync(entry.AccountId, entry.CalendarId, entry.EventId, ct) ?? throw new EventGoneException();
            var answer  = EventJson.AttendeesPatch(EventJson.WithResponse(current, response, note)).ToJsonString();
            try
            {
                return await google.PatchEventAsync(entry.AccountId, entry.CalendarId, entry.EventId, answer, EtagOf(current), entry.SendUpdates, ct);
            }
            catch (PreconditionFailedException) when (attempt == 0)
            {
                // Changed between the read and the write: read it again once
            }
        }
    }

    // Removes the entry and stores Google's result in one transaction, so undo can never see a sent entry as pending
    void Accept(OutboxEntry entry, string? googleJson)
    {
        var calendarId = LocalCalendarOf(entry);

        using var conn = database.Open();
        using var tx   = conn.BeginTransaction();
        OutboxStore.Remove(conn, tx, entry.Seq);

        if (googleJson is { Length: > 0 })
        {
            // Later Edits Were Made On Top Of This One: they now go out against Google's new ETag
            var etag = EtagOf(googleJson);
            OutboxStore.Rebase(conn, tx, entry.AccountId, calendarId, entry.EventId, entry.Seq, etag);

            if (OutboxStore.ForEvent(conn, tx, entry.AccountId, calendarId, entry.EventId).Count > 0)
            {
                EventStore.SetEtag(conn, tx, entry.AccountId, calendarId, entry.EventId, etag);
            }
            else
            {
                EventStore.ApplyJson(conn, tx, entry.AccountId, calendarId, googleJson);
            }
        }
        else if (entry.Operation == OutboxOperation.Rsvp)
        {
            // Replied to an event Google no longer has
            EventStore.Remove(conn, tx, entry.AccountId, calendarId, entry.EventId);
        }

        tx.Commit();
        log.Info("outbox.sent", $"seq={entry.Seq}");
    }

    void RecordConflict(OutboxEntry entry, string? googleJson)
    {
        using var conn = database.Open();
        var local = entry.Operation == OutboxOperation.Delete ? null : EventStore.Get(conn, entry.AccountId, LocalCalendarOf(entry), entry.EventId)?.RawJson;
        ConflictStore.Add(conn, null, entry.Seq, local, googleJson, time.GetUtcNow());
        log.Info("outbox.conflict", $"seq={entry.Seq}");
    }

    // Google will never accept this edit: put back what Google has, and drop this and later edits of the event
    async Task RejectAsync(OutboxEntry entry, CancellationToken ct)
    {
        var localCalendar = LocalCalendarOf(entry);
        var googleJson    = entry.Operation == OutboxOperation.Create ? null : await google.GetEventAsync(entry.AccountId, entry.CalendarId, entry.EventId, ct);

        using var conn = database.Open();
        using var tx   = conn.BeginTransaction();

        foreach (var later in OutboxStore.ForEvent(conn, tx, entry.AccountId, localCalendar, entry.EventId).Where(e => e.Seq > entry.Seq))
        {
            OutboxStore.Remove(conn, tx, later.Seq);
        }

        OutboxStore.Remove(conn, tx, entry.Seq);
        EventStore.Remove(conn, tx, entry.AccountId, localCalendar, entry.EventId);
        EventStore.Restore(conn, tx, entry.AccountId, entry.CalendarId, entry.EventId, entry.BeforeJson ?? "[]");
        if (googleJson is not null)
        {
            EventStore.ApplyJson(conn, tx, entry.AccountId, entry.CalendarId, googleJson);
        }

        tx.Commit();
    }

    // An entry whose calendar left the account (no foreign key keeps them in step)
    void Drop(long seq)
    {
        using var conn = database.Open();
        OutboxStore.Remove(conn, null, seq);
        log.Info("outbox.dropped", $"seq={seq}");
    }

    OutboxEntry? Reload(long seq)
    {
        using var conn = database.Open();
        return OutboxStore.Get(conn, null, seq);
    }

    // 4xx that retrying can't fix (not 401, 408, 429, or a rate-limit 403)
    static bool IsPermanent(GoogleApiException ex)
    {
        var status = (int)ex.Status;
        return status is >= 400 and < 500
            && ex.Status is not (HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            && ex.Reason is not ("rateLimitExceeded" or "userRateLimitExceeded");
    }

    static bool IsTemporary(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or GoogleApiException or JsonException or IOException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);
}
