using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Sync;

/// <summary>What one outbox pass did: local data changed, conflicts found, and edits Google refused (undone).</summary>
public readonly record struct SendReport(bool Changed, int Conflicts, int Rejected);

/// <summary>
/// Sends an account's outbox to Google, oldest first.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Accepted: the entry is removed. Later entries for the same event are rebased onto the new ETag,
/// and Google's JSON replaces the local row once nothing else is waiting for that event. An answer without an
/// ETag is read back once; if that fails, later entries keep their old ETag (so they meet a safe <c>412</c>)
/// and nothing ever goes out without <c>If-Match</c> because of it.</item>
/// <item><c>412</c>, or a patch or move of an event Google deleted: a conflict. That event waits for the user;
/// the rest of the queue keeps going (spec 5.5).</item>
/// <item>A create answered <c>409</c>: an earlier try got through, so Google's copy is fetched and kept.</item>
/// <item>A delete of an event that's already gone: done.</item>
/// <item>Any other <c>4xx</c> (for example no permission), a corrupt stored payload, or a move whose destination
/// calendar is gone: it can never be sent, so the event goes back to Google's version (or the snapshot when
/// Google's can't be read) and the entry (and later ones for that event) are dropped and reported.</item>
/// <item>Network failure, <c>401</c>, <c>429</c>, a usage-limit <c>403</c>, or <c>5xx</c> (writes are never
/// auto-retried), including while reading Google's copy after a conflict or refusal: the entry stays pending
/// with its error recorded, the pass stops so the order holds, and the next sync tries again. The report of
/// what was already done is still returned.</item>
/// <item>An entry whose calendar is no longer in the account: dropped.</item>
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

            // Send
            string? googleJson = null;
            Exception? failure = null;
            try
            {
                if (!calendars.Contains(LocalCalendarOf(entry)))
                {
                    throw new UnsendableEntryException();
                }

                googleJson = await SendOneAsync(entry, ct);
            }
            catch (EventGoneException) when (entry.Operation is OutboxOperation.Delete or OutboxOperation.Rsvp)
            {
                // Already gone: done
            }
            catch (Exception ex) when (ex is PreconditionFailedException or EventGoneException || IsRejection(ex) || IsTemporary(ex, ct))
            {
                failure = ex;
            }

            if (failure is null)
            {
                await AcceptAsync(entry, googleJson, ct);
                changed = true;
                continue;
            }

            // Follow Up (reading Google's copy can fail too; that defers the entry, never the whole sync)
            try
            {
                if (failure is PreconditionFailedException or EventGoneException)
                {
                    var current = failure is EventGoneException ? null : await google.GetEventAsync(entry.AccountId, entry.CalendarId, entry.EventId, ct);
                    RecordConflict(entry, current);
                    conflicts++;
                }
                else if (IsRejection(failure))
                {
                    await RejectAsync(entry, ct);
                    log.Info("outbox.rejected", failure is GoogleApiException refused ? $"seq={entry.Seq} status={(int)refused.Status}" : $"seq={entry.Seq}");
                    rejected++;
                }
                else
                {
                    Defer(entry, failure);
                    break;
                }
            }
            catch (Exception ex) when (IsTemporary(ex, ct))
            {
                Defer(entry, ex);
                break;
            }

            waiting.Add(entry.EventId);
            changed = true;
        }

        return new SendReport(changed, conflicts, rejected);
    }

    /// <summary>The <c>etag</c> of Google's event JSON, or null (also when the JSON is invalid).</summary>
    internal static string? EtagOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("etag", out var etag) && etag.ValueKind == JsonValueKind.String
                ? etag.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
                catch (ArgumentException)
                {
                    // The stored payload has no ID (or isn't JSON): it can never be sent safely
                    throw new UnsendableEntryException();
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
        ResponseStatus response;
        string? note;
        try
        {
            var reply = JsonNode.Parse(entry.Payload ?? "{}") as JsonObject;
            response  = EventJson.ParseResponse((string?)reply?["responseStatus"]);
            note      = (string?)reply?["comment"];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Leaf's Own Stored Payload Is Corrupt: retrying can't fix it
            throw new UnsendableEntryException();
        }

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
    async Task AcceptAsync(OutboxEntry entry, string? googleJson, CancellationToken ct)
    {
        var calendarId = LocalCalendarOf(entry);
        var etag       = googleJson is { Length: > 0 } ? EtagOf(googleJson) : null;

        // Google's Answer Has No ETag: read it back, so later edits never go out without If-Match
        if (googleJson is { Length: > 0 } && etag is null)
        {
            try
            {
                googleJson = await google.GetEventAsync(entry.AccountId, calendarId, entry.EventId, ct);
                etag       = googleJson is null ? null : EtagOf(googleJson);
            }
            catch (Exception ex) when (IsTemporary(ex, ct))
            {
                // Later entries keep their old ETag and meet a safe 412
                log.Info("outbox.etag-missing", $"seq={entry.Seq}");
            }
        }

        using var conn = database.Open();
        using var tx   = conn.BeginTransaction();
        OutboxStore.Remove(conn, tx, entry.Seq);

        if (etag is not null)
        {
            // Later Edits Were Made On Top Of This One: they now go out against Google's new ETag
            OutboxStore.Rebase(conn, tx, entry.AccountId, calendarId, entry.EventId, entry.Seq, etag);

            if (OutboxStore.ForEvent(conn, tx, entry.AccountId, calendarId, entry.EventId).Count > 0)
            {
                EventStore.SetEtag(conn, tx, entry.AccountId, calendarId, entry.EventId, etag);
            }
            else
            {
                ApplyGoogleJson(conn, tx, entry, calendarId, googleJson!);
            }
        }
        else if (entry.Operation == OutboxOperation.Rsvp && googleJson is null)
        {
            // Replied to an event Google no longer has
            EventStore.Remove(conn, tx, entry.AccountId, calendarId, entry.EventId);
        }

        tx.Commit();
        log.Info("outbox.sent", $"seq={entry.Seq}");
    }

    // Google took the edit: an odd answer must not undo that, the next pull brings the event
    void ApplyGoogleJson(SqliteConnection conn, SqliteTransaction tx, OutboxEntry entry, string calendarId, string googleJson)
    {
        try
        {
            EventStore.ApplyJson(conn, tx, entry.AccountId, calendarId, googleJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            log.Info("outbox.sent.unreadable", $"seq={entry.Seq}");
        }
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
        string? googleJson = null;
        if (entry.Operation != OutboxOperation.Create)
        {
            try
            {
                googleJson = await google.GetEventAsync(entry.AccountId, entry.CalendarId, entry.EventId, ct);
            }
            catch (GoogleApiException ex) when (IsPermanent(ex))
            {
                // Can't Read Google's Copy Either: the snapshot is all there is
            }
        }

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
            ApplyGoogleJson(conn, tx, entry, entry.CalendarId, googleJson);
        }

        tx.Commit();
    }

    // Kept pending with the error; the next sync tries again
    void Defer(OutboxEntry entry, Exception ex)
    {
        var error = ex is GoogleApiException api ? $"status {(int)api.Status}" : "network";
        using var conn = database.Open();
        OutboxStore.RecordAttempt(conn, entry.Seq, error);
        log.Info("outbox.deferred", $"seq={entry.Seq} error={error}");
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

    static bool IsRejection(Exception ex) =>
        ex is UnsendableEntryException || (ex is GoogleApiException api && IsPermanent(api));

    // 4xx that retrying can't fix (not 401, 408, 429, or a rate or usage limit 403)
    static bool IsPermanent(GoogleApiException ex)
    {
        var status = (int)ex.Status;
        return status is >= 400 and < 500
            && ex.Status is not (HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            && ex.Reason is not ("rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded" or "dailyLimitExceeded");
    }

    static bool IsTemporary(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or GoogleApiException or JsonException or IOException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    // An entry Leaf itself can never send (corrupt payload, create without an ID, move to a calendar that's gone)
    sealed class UnsendableEntryException() : Exception("This outbox entry can't be sent.");
}
