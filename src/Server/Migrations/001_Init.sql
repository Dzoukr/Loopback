-- Timestamps are unix milliseconds (INTEGER), text columns are UTF-8.

-- Single row (Id = 1): the Plaud session kept alive by the background sync.
CREATE TABLE PlaudConnection (
    Id                      INTEGER PRIMARY KEY,
    ApiBase                 TEXT    NOT NULL,
    WorkspaceId             TEXT    NOT NULL,
    WorkspaceToken          TEXT    NOT NULL,
    WorkspaceTokenExpiresAt INTEGER NOT NULL,
    -- Empty when the session was started from a pasted workspace token (cannot be refreshed).
    RefreshToken            TEXT    NOT NULL,
    RefreshExpiresAt        INTEGER NOT NULL,
    -- SHA-256 of the PLAUD_TOKEN the session was started from; a changed token restarts the session.
    BootstrapTokenHash      TEXT    NOT NULL,
    LastSyncAt              INTEGER NULL,
    LastError               TEXT    NULL,
    UpdatedAt               INTEGER NOT NULL
);

CREATE TABLE Recordings (
    -- Plaud file id.
    Id                  TEXT    PRIMARY KEY,
    Filename            TEXT    NOT NULL,
    StartTime           INTEGER NOT NULL,
    DurationMs          INTEGER NOT NULL,
    Filesize            INTEGER NOT NULL,
    -- Plaud `version_ms`: changes whenever the recording is edited in Plaud.
    VersionMs           TEXT    NOT NULL,
    -- synced | queued | transcribing | summarizing | done | failed
    Status              TEXT    NOT NULL,
    Nature              TEXT    NULL,
    Title               TEXT    NULL,
    Error               TEXT    NULL,
    -- Step a failed recording returns to on retry.
    FailedStep          TEXT    NULL,
    SpeechmaticsJobId   TEXT    NULL,
    -- Speaker segments as JSON: [{ speaker, start, end, text }].
    Transcript          TEXT    NULL,
    OutputFile          TEXT    NULL,
    StepStartedAt       INTEGER NULL,
    CreatedAt           INTEGER NOT NULL,
    UpdatedAt           INTEGER NOT NULL,
    -- Tombstone: set once processed, so the sync never re-imports the recording.
    DeletedAt           INTEGER NULL
);

CREATE INDEX IX_Recordings_Status ON Recordings (Status);
