-- Notes added in Loopback (docs/attachments.md, Notes added in Loopback): marks with Source = 'upload',
-- Kind = 'note', at a moment of the call or without a time (about the call as a whole) - so OffsetMs
-- becomes nullable. SQLite cannot drop NOT NULL, so the table is rebuilt.

CREATE TABLE Marks_New (
    -- GUID ("N" format).
    Id          TEXT    PRIMARY KEY,
    RecordingId TEXT    NOT NULL,
    -- Milliseconds from the recording start; NULL = no time (a note about the call as a whole).
    OffsetMs    INTEGER NULL,
    Note        TEXT    NULL,
    -- plaud (imported from a Plaud mark) | upload (added in Loopback)
    Source      TEXT    NOT NULL,
    -- 1 while Plaud is still analyzing the highlight (its text comes later).
    Pending     INTEGER NOT NULL DEFAULT 0,
    -- 1 once the user edited the note in Loopback - Plaud's late text never overwrites it.
    UserEdited  INTEGER NOT NULL DEFAULT 0,
    CreatedAt   INTEGER NOT NULL,
    UpdatedAt   INTEGER NOT NULL,
    -- highlight | note | voice
    Kind        TEXT    NOT NULL DEFAULT 'highlight'
);

INSERT INTO Marks_New (Id, RecordingId, OffsetMs, Note, Source, Pending, UserEdited, CreatedAt, UpdatedAt, Kind)
SELECT Id, RecordingId, OffsetMs, Note, Source, Pending, UserEdited, CreatedAt, UpdatedAt, Kind FROM Marks;

DROP TABLE Marks;
ALTER TABLE Marks_New RENAME TO Marks;

CREATE INDEX IX_Marks_RecordingId ON Marks (RecordingId);
