-- Marks made in Plaud during the call, imported during sync (docs/attachments.md, Plaud Marks).

-- upload (dropped / pasted in Loopback) | plaud (imported from a Plaud mark)
ALTER TABLE Attachments ADD COLUMN Source TEXT NOT NULL DEFAULT 'upload';
-- Machine-made text about the file: Plaud's photo description, a voice memo's transcript. Not editable (Note is the user's).
ALTER TABLE Attachments ADD COLUMN Description TEXT NULL;
-- Capture time reported by the source (Plaud: recording start + mark timestamp); "detect again" returns to it.
ALTER TABLE Attachments ADD COLUMN ReportedCapturedAt INTEGER NULL;

-- A moment in the call without a file: a Plaud device highlight or a Plaud text note.
CREATE TABLE Marks (
    -- GUID ("N" format).
    Id          TEXT    PRIMARY KEY,
    RecordingId TEXT    NOT NULL,
    -- Milliseconds from the recording start.
    OffsetMs    INTEGER NOT NULL,
    Note        TEXT    NULL,
    -- plaud
    Source      TEXT    NOT NULL,
    -- 1 while Plaud is still analyzing the highlight (its text comes later).
    Pending     INTEGER NOT NULL DEFAULT 0,
    -- 1 once the user edited the note in Loopback - Plaud's late text never overwrites it.
    UserEdited  INTEGER NOT NULL DEFAULT 0,
    CreatedAt   INTEGER NOT NULL,
    UpdatedAt   INTEGER NOT NULL
);

CREATE INDEX IX_Marks_RecordingId ON Marks (RecordingId);

-- Every Plaud mark imported once, so one deleted in Loopback is not imported again.
CREATE TABLE PlaudMarkImports (
    RecordingId TEXT    NOT NULL,
    -- Plaud mark_id, or type:timestamp:hash(content) for marks without one.
    MarkKey     TEXT    NOT NULL,
    -- The attachment or mark it became.
    TargetId    TEXT    NOT NULL,
    -- 1 while the mark's text is still being analyzed in Plaud.
    Pending     INTEGER NOT NULL DEFAULT 0,
    ImportedAt  INTEGER NOT NULL,
    PRIMARY KEY (RecordingId, MarkKey)
);

-- Set by their own UPDATEs (not part of RecordingRow, like AttachmentsChangedAt):
-- the Plaud list's is_markmemo, the version_ms of the last import, and when marks were last fetched.
ALTER TABLE Recordings ADD COLUMN HasPlaudMarks INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Recordings ADD COLUMN PlaudMarksVersion TEXT NULL;
ALTER TABLE Recordings ADD COLUMN PlaudMarksCheckedAt INTEGER NULL;
