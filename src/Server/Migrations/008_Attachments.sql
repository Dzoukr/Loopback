-- Files attached to a recording (screenshots, documents, ...), see docs/attachments.md.
-- The file itself is stored in <data>/attachments/{RecordingId}/{Id}.bin.
CREATE TABLE Attachments (
    -- GUID ("N" format).
    Id              TEXT    PRIMARY KEY,
    RecordingId     TEXT    NOT NULL,
    -- Original file name incl. extension (display only, never part of a path).
    FileName        TEXT    NOT NULL,
    -- Detected MIME type, e.g. image/png, application/pdf, text/plain, application/octet-stream.
    ContentType     TEXT    NOT NULL,
    -- image | pdf | text | other
    Kind            TEXT    NOT NULL,
    SizeBytes       INTEGER NOT NULL,
    Sha256          TEXT    NOT NULL,
    -- NULL = unknown, or moved to Reference by the user.
    CapturedAt      INTEGER NULL,
    -- filename | exif | modified | manual | none
    TimeSource      TEXT    NOT NULL,
    -- File's last-modified time sent by the browser, kept so the time can be detected again.
    FileModifiedAt  INTEGER NULL,
    Note            TEXT    NULL,
    CreatedAt       INTEGER NOT NULL,
    UpdatedAt       INTEGER NOT NULL
);

CREATE INDEX IX_Attachments_RecordingId ON Attachments (RecordingId);
CREATE UNIQUE INDEX UX_Attachments_RecordingId_Sha256 ON Attachments (RecordingId, Sha256);

-- Last add / edit / delete of an attachment, compared with the result's processedAt
-- ("attachments changed since processing"). Not part of RecordingRow on purpose: whole-row
-- updates from a long-running step would overwrite a change made meanwhile.
ALTER TABLE Recordings ADD COLUMN AttachmentsChangedAt INTEGER NULL;
