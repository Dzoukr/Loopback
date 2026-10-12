-- Attachments described by Claude before the summary passes (docs/attachments-processing.md).

-- Descriptions by file content and model: the same file is never described twice by the same model
-- (Reprocess, Retry, or attached to another recording).
CREATE TABLE AttachmentDescriptions (
    Sha256      TEXT    NOT NULL,
    Model       TEXT    NOT NULL,
    Description TEXT    NOT NULL,
    CreatedAt   INTEGER NOT NULL,
    PRIMARY KEY (Sha256, Model)
);

-- The model that wrote Attachments.Description; NULL = not described by Claude (none, or Plaud's text).
ALTER TABLE Attachments ADD COLUMN DescriptionModel TEXT NULL;
