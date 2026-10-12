-- Plaud voice memos are marks (a spoken note at a moment), not audio attachments: the memo is only
-- transcribed, its audio is not kept. Marks get a kind so they can be told apart.
-- highlight (device button) | note (typed in the Plaud app) | voice (voice memo, note = its transcript)
ALTER TABLE Marks ADD COLUMN Kind TEXT NOT NULL DEFAULT 'highlight';

-- Marks imported before kinds existed: highlights have Plaud's analysis state, text notes never do.
UPDATE Marks SET Kind = 'note'
WHERE Id IN (SELECT TargetId FROM PlaudMarkImports WHERE MarkKey LIKE '3:%');

-- Voice memos imported as audio attachments by 009: drop them and their import keys, and let the next
-- marks import fetch those recordings again (as voice memo marks). Their files go with the startup
-- cleanup of files without a row.
UPDATE Recordings SET PlaudMarksVersion = NULL
WHERE Id IN (SELECT RecordingId FROM Attachments WHERE Source = 'plaud' AND ContentType LIKE 'audio/%');

DELETE FROM PlaudMarkImports
WHERE TargetId IN (SELECT Id FROM Attachments WHERE Source = 'plaud' AND ContentType LIKE 'audio/%');

DELETE FROM Attachments WHERE Source = 'plaud' AND ContentType LIKE 'audio/%';
