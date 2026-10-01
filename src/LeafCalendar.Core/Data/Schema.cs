namespace LeafCalendar.Core.Data;

/// <summary>SQL schema migrations, applied in order by <see cref="LeafDatabase.Migrate"/>.</summary>
internal static class Schema
{
    /// <summary>Version 1: accounts, calendars, and events. Outbox and conflicts arrive in version 3.</summary>
    public const string V1 = """
        CREATE TABLE accounts (
            id           TEXT PRIMARY KEY,
            email        TEXT NOT NULL,
            display_name TEXT,
            picture      TEXT,
            status       TEXT NOT NULL DEFAULT 'ok'
        );

        CREATE TABLE calendars (
            account_id        TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            id                TEXT NOT NULL,
            summary           TEXT NOT NULL,
            summary_override  TEXT,
            time_zone         TEXT,
            background_color  TEXT,
            foreground_color  TEXT,
            access_role       TEXT NOT NULL,
            is_primary        INTEGER NOT NULL DEFAULT 0,
            hidden            INTEGER NOT NULL DEFAULT 0,
            sort_order        INTEGER NOT NULL DEFAULT 0,
            default_reminders TEXT,
            sync_token        TEXT,
            PRIMARY KEY (account_id, id)
        );

        CREATE TABLE events (
            account_id          TEXT NOT NULL,
            calendar_id         TEXT NOT NULL,
            id                  TEXT NOT NULL,
            ical_uid            TEXT,
            etag                TEXT,
            status              TEXT NOT NULL,
            start_utc           INTEGER,
            end_utc             INTEGER,
            is_all_day          INTEGER NOT NULL DEFAULT 0,
            start_time_zone     TEXT,
            is_recurring_master INTEGER NOT NULL DEFAULT 0,
            recurring_event_id  TEXT,
            original_start_utc  INTEGER,
            updated_utc         INTEGER,
            raw_json            TEXT NOT NULL,
            PRIMARY KEY (account_id, calendar_id, id),
            FOREIGN KEY (account_id, calendar_id) REFERENCES calendars(account_id, id) ON DELETE CASCADE
        );

        CREATE INDEX ix_events_range  ON events (account_id, calendar_id, start_utc, end_utc);
        CREATE INDEX ix_events_master ON events (account_id, calendar_id, recurring_event_id);
        """;

    /// <summary>
    /// Version 2: app settings, plus Leaf's own calendar display choices. <c>leaf_hidden</c> is null
    /// until Leaf decides (it then follows Google's "selected"), so later Google list refreshes never
    /// override a choice the user made in Leaf.
    /// </summary>
    public const string V2 = """
        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        ALTER TABLE calendars ADD COLUMN leaf_hidden INTEGER;
        ALTER TABLE calendars ADD COLUMN leaf_color  TEXT;
        """;

    /// <summary>
    /// Version 3: the outbox (edits waiting for Google, in order) and the conflicts Google reported.
    /// <c>send_updates</c> is 1 to email guests. <c>before_json</c> is an <see cref="EventStore.Snapshot"/>
    /// of the rows before the edit (for undo). <c>not_before</c> holds a delete back for the undo window.
    /// <c>last_error</c> keeps an HTTP status or Google reason only, never event content.
    /// </summary>
    public const string V3 = """
        CREATE TABLE outbox (
            seq          INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id   TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            calendar_id  TEXT NOT NULL,
            event_id     TEXT NOT NULL,
            operation    TEXT NOT NULL,
            payload      TEXT,
            base_etag    TEXT,
            send_updates INTEGER NOT NULL DEFAULT 0,
            before_json  TEXT,
            not_before   INTEGER,
            state        TEXT NOT NULL DEFAULT 'pending',
            attempts     INTEGER NOT NULL DEFAULT 0,
            last_error   TEXT
        );

        CREATE INDEX ix_outbox_account ON outbox (account_id, state, seq);
        CREATE INDEX ix_outbox_event   ON outbox (account_id, calendar_id, event_id);

        CREATE TABLE conflicts (
            outbox_seq   INTEGER PRIMARY KEY REFERENCES outbox(seq) ON DELETE CASCADE,
            local_json   TEXT,
            google_json  TEXT,
            detected_utc INTEGER NOT NULL
        );
        """;

    /// <summary>
    /// Version 4: <c>outbox.depends_on</c>, the entry another one must wait behind. A "this and following" split
    /// creates its new series only after the old series' end has reached Google, and never when the user keeps
    /// Google's version of the old series instead.
    /// </summary>
    public const string V4 = """
        ALTER TABLE outbox ADD COLUMN depends_on INTEGER;
        """;

    /// <summary>
    /// Version 5: every notification Leaf has shown, so a restart or a full resync never shows one twice. <c>key</c>
    /// names the alert (kind, event instance, reminder minutes; or kind, event, and Google's <c>sequence</c> for
    /// invites), <c>tag</c> is the short hash Windows knows the notification by, <c>event_end</c> says when the row may
    /// go, and <c>retracted</c> marks a "Join now" that was withdrawn.
    /// </summary>
    public const string V5 = """
        CREATE TABLE alert_ledger (
            key           TEXT PRIMARY KEY,
            kind          TEXT NOT NULL,
            tag           TEXT NOT NULL,
            event_end     INTEGER NOT NULL,
            delivered_utc INTEGER NOT NULL,
            retracted     INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX ix_alert_ledger_open ON alert_ledger (kind, retracted);
        """;

    /// <summary>
    /// Version 6: <c>accounts.hosted_domain</c>, Google's <c>hd</c> sign-in claim. Null until Leaf knows it (accounts
    /// that signed in before Milestone 5 are looked up once), empty for a personal account, else the Workspace domain.
    /// </summary>
    public const string V6 = """
        ALTER TABLE accounts ADD COLUMN hosted_domain TEXT;
        """;
}
