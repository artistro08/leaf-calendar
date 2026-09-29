namespace LeafCalendar.Core.Data;

/// <summary>SQL schema migrations, applied in order by <see cref="LeafDatabase.Migrate"/>.</summary>
internal static class Schema
{
    /// <summary>Version 1: accounts, calendars, and events. Outbox and conflicts arrive in Milestone 3.</summary>
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
}
