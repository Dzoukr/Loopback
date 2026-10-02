-- The result file's JSON (envelope incl. content), kept for the UI - the output folder may be
-- an inbox whose files get moved or deleted by other tools.
ALTER TABLE Recordings ADD COLUMN Result TEXT NULL;
