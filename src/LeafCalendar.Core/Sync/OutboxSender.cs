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
/// <item>Network failure, <c>401</c> (also a failed token refresh), <c>429</c>, a usage-limit or account-wide
/// <c>403</c>, a <c>4xx</c> without Google's reason, or <c>5xx</c> (writes are never auto-retried), including while
/// reading Google's copy after a conflict or refusal: the entry stays pending with its error recorded and waits out
/// its backoff (<see cref="OutboxStore.Backoff"/>) before the next try. A <c>5xx</c> or unreadable answer holds only
/// that event; anything else stops the pass so the order holds, and keeps stopping each pass until its backoff ends.
/// The report of what was already done is still returned.</item>
/// <item>A move answered "not found" whose event is already in its destination: an earlier try got through.</item>
/// <item>An entry whose calendar is no longer in the account, conflicted or not: dropped.</item>
/// </list>
/// Held entries (the undo window or a backoff), entries behind a held or conflicted entry for the same event, and entries
/// whose <see cref="OutboxEntry.DependsOn"/> is still in the outbox wait. A refused entry drops its dependents.
/// Only sequence numbers and statuses are logged.
/// </remarks>
public sealed class OutboxSender(GoogleCalendarClient google, LeafDatabase database, AppLog log, TimeProvider time)
{
    /// <summary>Sends what can be sent now.</summary>
    /// <exception cref="Auth.AccountNeedsSignInException">The account's refresh token was rejected (the outbox is kept).</exception>
    public async Task<SendReport> SendAsync(string accountId, CancellationToken ct)
    {
        IReadOnlyList<OutboxEntry> queue;
        List<OutboxEntry> conflicted;
        HashSet<string> calendars;
        using (var conn = database.Open())
        {
            queue = OutboxStore.Pending(conn, accountId);
            conflicted = [.. ConflictStore.GetAll(conn).Select(c => c.Entry).Where(e => e.AccountId == accountId)];
            calendars = [.. CalendarStore.GetForAccount(conn, accountId).Select(c => c.Id)];
        }

        HashSet<string> waiting = [.. conflicted.Where(e => calendars.Contains(e.CalendarId)).Select(e => e.EventId)];

        var changed = false;
        var conflicts = 0;
        var rejected = 0;
        var now = time.GetUtcNow();

        // Conflicts Of A Calendar That Left The Account: nothing is left to decide
        foreach (var orphan in conflicted.Where(e => !calendars.Contains(e.CalendarId)))
        {
            Drop(orphan);
            changed = true;
        }

        foreach (var queued in queue)
        {
            // Re-Read: an earlier send may have rebased it, and undo may have removed it
            if (Reload(queued.Seq) is not { State: OutboxState.Pending } entry)
            {
                continue;
            }

            // Held, Backing Off After A Failed Try, Behind A Held Or Conflicted Edit Of The Same Event, Or Waiting For The Entry It Depends On
            if (waiting.Contains(entry.EventId) || entry.NotBefore > now || entry.RetryAfter > now || (entry.DependsOn is { } dependsOn && Reload(dependsOn) is not null))
            {
                // Backing Off After A Failure That Stopped The Pass (the network, a rate limit, the account): it still does, so the order holds
                if (entry.RetryAfter > now && StopsThePass(entry.LastError))
                {
                    break;
                }

                waiting.Add(entry.EventId);
                continue;
            }

            // Calendar Removed From The Account: Google can't take it
            if (!calendars.Contains(entry.CalendarId))
            {
                Drop(entry);
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
                    // A Move Google Can't Find In Its Source May Already Be In Its Destination
                    var lostMove = failure is EventGoneException && entry.Operation == OutboxOperation.Move;
                    string? current = null;
                    try
                    {
                        current = failure is PreconditionFailedException || lostMove
                            ? await google.GetEventAsync(entry.AccountId, lostMove ? LocalCalendarOf(entry) : entry.CalendarId, entry.EventId, ct)
                            : null;
                    }
                    catch (GoogleApiException ex) when (IsPermanent(ex))
                    {
                        // Google's Copy Can't Be Read: the user still decides, without it
                    }

                    // Lost Response: Google already applied this edit, so it counts as sent
                    if ((failure is PreconditionFailedException && entry.Operation == OutboxOperation.Patch && Matches(entry.Payload, current)) || (lostMove && current is not null))
                    {
                        await AcceptAsync(entry, current, ct);
                        changed = true;
                        continue;
                    }

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
                    if (StopsThePass(failure))
                    {
                        break;
                    }

                    waiting.Add(entry.EventId);
                    continue;
                }
            }
            catch (Exception ex) when (IsTemporary(ex, ct))
            {
                Defer(entry, ex);
                if (StopsThePass(ex))
                {
                    break;
                }

                waiting.Add(entry.EventId);
                continue;
            }

            waiting.Add(entry.EventId);
            changed = true;
        }

        return new SendReport(changed, conflicts, rejected);
    }

    /// <summary>True when Google's event already holds every field the patch sets (an edit whose answer was lost).</summary>
    internal static bool Matches(string? payload, string? googleJson)
    {
        if (payload is null || googleJson is null)
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject patch || patch.Count == 0 || JsonNode.Parse(googleJson) is not JsonObject current)
            {
                return false;
            }

            return Holds(patch, current);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The way a patch lands: an object sets only its own members (null clears one), so Google's copy may carry more
    // (a filled-in conference, a guest's answer); an array's items line up one for one; anything else must be equal
    private static bool Holds(JsonNode? patch, JsonNode? current) => patch switch
    {
        null => current is null,
        JsonObject set => current is JsonObject has && set.All(p => Holds(p.Value, has[p.Key])),
        JsonArray list => current is JsonArray items && list.Count == items.Count && list.Zip(items).All(p => Holds(p.First, p.Second)),
        _ => JsonNode.DeepEquals(patch, current),
    };

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

    private async Task<string?> SendOneAsync(OutboxEntry entry, CancellationToken ct)
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
    private async Task<string> SendReplyAsync(OutboxEntry entry, CancellationToken ct)
    {
        ResponseStatus response;
        string? note;
        try
        {
            var reply = JsonNode.Parse(entry.Payload ?? "{}") as JsonObject;
            response = EventJson.ParseResponse((string?)reply?["responseStatus"]);
            note = (string?)reply?["comment"];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Leaf's Own Stored Payload Is Corrupt: retrying can't fix it
            throw new UnsendableEntryException();
        }

        for (var attempt = 0; ; attempt++)
        {
            var current = await google.GetEventAsync(entry.AccountId, entry.CalendarId, entry.EventId, ct) ?? throw new EventGoneException();
            var answer = EventJson.AttendeesPatch(EventJson.WithResponse(current, response, note)).ToJsonString();
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
    private async Task AcceptAsync(OutboxEntry entry, string? googleJson, CancellationToken ct)
    {
        var calendarId = LocalCalendarOf(entry);
        var etag = googleJson is { Length: > 0 } ? EtagOf(googleJson) : null;

        // Google's Answer Has No ETag: read it back, so later edits never go out without If-Match
        if (googleJson is { Length: > 0 } && etag is null)
        {
            try
            {
                googleJson = await google.GetEventAsync(entry.AccountId, calendarId, entry.EventId, ct);
                etag = googleJson is null ? null : EtagOf(googleJson);
            }
            catch (Exception ex) when (IsTemporary(ex, ct))
            {
                // Later entries keep their old ETag and meet a safe 412
                log.Info("outbox.etag-missing", $"seq={entry.Seq}");
            }
        }

        using var conn = database.Open();
        using var tx = conn.BeginTransaction();
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
    private void ApplyGoogleJson(SqliteConnection conn, SqliteTransaction tx, OutboxEntry entry, string calendarId, string googleJson)
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

    private void RecordConflict(OutboxEntry entry, string? googleJson)
    {
        using var conn = database.Open();
        var local = entry.Operation == OutboxOperation.Delete ? null : EventStore.Get(conn, entry.AccountId, LocalCalendarOf(entry), entry.EventId)?.RawJson;
        ConflictStore.Add(conn, null, entry.Seq, local, googleJson, time.GetUtcNow());
        log.Info("outbox.conflict", $"seq={entry.Seq}");
    }

    // Google will never accept this edit: put back what Google has, and drop this and later edits of the event
    private async Task RejectAsync(OutboxEntry entry, CancellationToken ct)
    {
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
        using var tx = conn.BeginTransaction();

        // Later Edits Go Too (a later move may have taken the local row to another calendar)
        var localCalendar = OutboxStore.DropLater(conn, tx, entry);

        OutboxStore.Remove(conn, tx, entry.Seq);
        OutboxStore.DropDependents(conn, tx, entry.Seq);
        EventStore.Remove(conn, tx, entry.AccountId, localCalendar, entry.EventId);
        EventStore.Restore(conn, tx, entry.AccountId, entry.CalendarId, entry.EventId, entry.BeforeJson ?? "[]");
        if (googleJson is not null)
        {
            ApplyGoogleJson(conn, tx, entry, entry.CalendarId, googleJson);
        }

        tx.Commit();
    }

    // Kept pending with the error; a sync after its backoff tries again
    private void Defer(OutboxEntry entry, Exception ex)
    {
        var error = ErrorOf(ex);
        using var conn = database.Open();
        OutboxStore.RecordAttempt(conn, entry.Seq, error, time.GetUtcNow());
        log.Info("outbox.deferred", $"seq={entry.Seq} error={error}");
    }

    // An entry whose calendar left the account (no foreign key keeps them in step), with what waits behind it
    private void Drop(OutboxEntry entry)
    {
        using var conn = database.Open();
        using var tx = conn.BeginTransaction();
        OutboxStore.Remove(conn, tx, entry.Seq);
        OutboxStore.DropDependents(conn, tx, entry.Seq);

        // A Move Out Of It Left A Row Google Will Never Have (edits made on it there stay, and meet Google as conflicts)
        var localCalendar = LocalCalendarOf(entry);
        if (localCalendar != entry.CalendarId && OutboxStore.ForEvent(conn, tx, entry.AccountId, localCalendar, entry.EventId).Count == 0)
        {
            EventStore.Remove(conn, tx, entry.AccountId, localCalendar, entry.EventId);
        }

        tx.Commit();
        log.Info("outbox.dropped", $"seq={entry.Seq}");
    }

    private OutboxEntry? Reload(long seq)
    {
        using var conn = database.Open();
        return OutboxStore.Get(conn, null, seq);
    }

    private static bool IsRejection(Exception ex) =>
        ex is UnsendableEntryException || (ex is GoogleApiException api && IsPermanent(api));

    // 4xx where Google refused this event: not 401, 408, 429, a rate or usage limit, a refusal of the whole account or
    // project (the API switched off, a missing scope, an admin policy), or an answer without Google's reason (a proxy's page)
    private static bool IsPermanent(GoogleApiException ex)
    {
        var status = (int)ex.Status;
        return status is >= 400 and < 500
            && ex.Status is not (HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            && ex.Reason is not (null or "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded" or "dailyLimitExceeded"
                or "accessNotConfigured" or "insufficientPermissions" or "domainPolicy" or "authError");
    }

    // What last_error keeps for a failed try: a status or reason only, never event content
    private static string ErrorOf(Exception ex) => ex switch
    {
        GoogleApiException api => $"status {(int)api.Status}",
        JsonException => "unreadable",
        _ => "network",
    };

    // Only Google failing on this one entry (a 5xx, an answer Leaf can't read) lets the rest of the queue go on
    private static bool StopsThePass(Exception ex) => StopsThePass(ErrorOf(ex));

    // The same, from the error a backed-off entry recorded
    private static bool StopsThePass(string? error) =>
        error is not (null or "unreadable") && !error.StartsWith("status 5", StringComparison.Ordinal);

    private static bool IsTemporary(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or GoogleApiException or JsonException or IOException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    // An entry Leaf itself can never send (corrupt payload, create without an ID, move to a calendar that's gone)
    private sealed class UnsendableEntryException() : Exception("This outbox entry can't be sent.");
}
