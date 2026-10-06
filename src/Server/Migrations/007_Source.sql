-- Where a recording comes from: plaud (synced) | upload (an audio file dropped into the UI).
-- Uploads have no Plaud file, so the sync's deletion reconciliation must never touch them.
ALTER TABLE Recordings ADD COLUMN Source TEXT NOT NULL DEFAULT 'plaud';

-- Result envelope: `plaudFileId` became `sourceName` + `sourceId` (result files already in the
-- output folder keep the old key).
UPDATE Recordings
SET Result = json_set(json_remove(Result, '$.plaudFileId'), '$.sourceName', 'plaud', '$.sourceId', json_extract(Result, '$.plaudFileId'))
WHERE Result IS NOT NULL AND json_valid(Result) AND json_type(Result, '$.plaudFileId') IS NOT NULL;
