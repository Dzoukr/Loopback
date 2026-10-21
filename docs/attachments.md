# Attachments

Files that belong to a call - a screenshot taken during it, a deck shared before it, a document discussed in it - attached to the recording in Loopback. Each attachment is anchored to the moment of the call it was captured in, so it can be shown on the waveform and in the transcript, and later (Part 2) read by the AI next to what was said at that moment.

The specification has two parts:
- **Part 1 - Attachments in Loopback** (this document): attaching, storing, placing in time, showing, editing and deleting - plus importing the marks made in Plaud during the call (Plaud Marks).
- **Part 2 - Processing and export** (`docs/attachments-processing.md`): describing attachments with the AI, interleaving them and the marks into the transcript for the summary passes, linking them from topics / points / action items, the structured `content` schema, the exported `marks` + `attachments` + files next to the result JSON, and the Obsidian note.

Part 1 does not change processing or the result JSON. Attachments added in Part 1 are picked up once Part 2 exists (Reprocess).

Status: Part 1 is implemented, including Plaud Marks.

## Concepts
- **Attachment** - one file attached to one recording. Any file type is accepted; the type decides only how it is previewed (and in Part 2 whether the AI can read it).
- **Capture time** - when the file came into existence (a screenshot was taken, a photo shot). Detected automatically, editable by the user.
- **Placement** - derived from the capture time and the recording's start + duration, never stored:
  - **During call** - the capture time falls inside the recording (with 60 s tolerance on both ends, clamped to the recording). Has an **offset** (seconds from the recording start).
  - **Reference** - the capture time is outside the recording, or unknown. Belongs to the call as a whole (a deck sent beforehand, a pasted image).
  Placement is derived at read time, so a Plaud update of the recording's start time (`version_ms` change) moves the attachments with it.
- **Mark** - a moment in the call without a file: a press of the Plaud device button (highlight), a text note typed in the Plaud app, or a voice memo recorded in it (the note is its transcript). Has a kind, an offset and an optional note. Marks come from Plaud (see Plaud Marks) or are notes added in Loopback (see Notes added in Loopback).

## User Flow
1. The user drops files onto a recording card (or uses the paperclip button on the card, or pastes from the clipboard while the card is active).
2. Each file uploads right away - no dialog. The card shows a tile per file with upload progress.
3. The backend stores the file, detects its type and capture time, and returns the attachment. The tile shows its placement: a time badge (`00:23:40`), `Reference`, or `Reference - time unknown`.
4. During-call attachments appear as markers on the waveform and inline in the transcript at their offset.
5. The user can fix the time (or move it to Reference), add a note, open / download, or delete the attachment.

## Attaching
- **Drop on a card** attaches the files to that recording. The card shows a highlighted ring and "Drop to attach to <title>" while files are dragged over it.
- **Drop anywhere else** keeps today's behavior: audio files open the upload dialog for new recordings. Non-audio files dropped outside a card are not ignored silently - the page says to drop them on a recording to attach them.
  - While files are dragged, the page shows a hint bar at the bottom (drop anywhere = new recording, drop on a recording = attach) and every card gets a dashed outline. The cards stay visible - the former full-page overlay would hide the drop targets.
  - An audio file dropped on a card is an attachment (e.g. a voice memo), not a new recording.
- **Paperclip button** on the card opens the file picker (multiple files).
- **Paste** (Ctrl+V) attaches clipboard files / images to the active card - the last card the user clicked or that holds focus (thin ring). Not while typing into a field. Clipboard images have no capture time (see Capture Time), so they land in Reference with "time unknown" and the user places them.
- Attaching is allowed in every status except `deleted` (including `queued` / `transcribing` / `summarizing` - see Lifecycle).
- Limits (constants in the backend, not `.env`): 50 MB per file, 100 attachments per recording. A file with the same SHA-256 as an existing attachment of the same recording is not stored again - the existing one is returned and the tile says "Already attached".

## Capture Time
Detected by the backend from these sources, first match wins:
1. **`filename`** - a date and time in the original file name. The most exact source: the name is set when the file is created and survives copying, downloading and EXIF stripping. Matched generically - a date `yyyy-MM-dd` / `yyyyMMdd`, then a separator (space, `_`, `-`, `T`, ` at `), then a time `HH mm ss` whose parts are separated by any of `_ - . :` or nothing; text before and after is ignored. Examples:
   - Teams / snipping tools: `2026-10-09 11_52_44-Teams.png`
   - Windows: `Screenshot 2026-10-09 143512.png`
   - macOS: `Screenshot 2026-10-09 at 14.35.12.png`
   - Android: `Screenshot_20261009-143512.png`, `Screenshot_20261009_143512_Teams.jpg`
   - Camera / generic: `IMG_20261009_143512.jpg`, `PXL_20261009_143512123.jpg`, `20261009_143512.jpg`
   - ShareX / Greenshot style: `2026-10-09_14-35-12.png`
2. **`exif`** - EXIF `DateTimeOriginal` (+ `OffsetTimeOriginal` when present) of JPEG / HEIC / PNG / WebP images (photos from a phone keep it, unlike the file time, which often becomes the transfer time).
3. **`modified`** - the file's last-modified time, sent by the browser (`File.lastModified`). Saved screenshots keep it exactly; for downloaded or copied files it is the download / copy time. Not used for clipboard pastes when it is within 10 s of the paste (a pasted screenshot gets the paste time); a file copied in Explorer and pasted keeps its real time.
4. **`none`** - no usable time: the attachment is Reference with "time unknown".
- **`manual`** - the user set the time (or moved it to Reference); never overwritten by detection.
- File-name and EXIF times are local times without an offset (unless EXIF has one). They are interpreted in the recording's UTC offset (`UtcOffsetMinutes`, the browser's offset for uploads) - the screenshot was taken where the call happened.
- A detected time that is not plausible (before 2000, or more than 1 day in the future) is skipped and the next source is tried.

## Previews
- Previewed by type, detected from the file's first bytes (not the extension or the browser's MIME type):
  - **image** - PNG, JPEG, GIF, WebP: thumbnail in the tile, full size in a lightbox.
  - **pdf** - PDF icon tile, opens in a new browser tab.
  - **text** - `.txt` / `.md` / `.csv` / `.json` (valid UTF-8): icon tile, opens as plain text in a new tab.
  - **other** - everything else (HEIC, SVG, Office documents, archives, audio, ...): icon tile, download only.
- No thumbnails are generated - the browser scales the original (`loading="lazy"`). HEIC is not displayable in browsers and is shown as `other` (its EXIF time is still used).
- SVG and HTML are always `other` and served as downloads, never inline (they can carry scripts).

## UI
- **Card - Attachments section** (shown when the recording has attachments or uploads, collapsible like the transcript, opens on a new drop): a grid of tiles, sorted During call by offset, then Reference by name. Tile: thumbnail or type icon, file name, placement badge (a during-call badge jumps the player there), size, note (if any). Clicking the tile opens it; on hover: Edit, Download, Delete.
- **Header** - a `📎 3` badge in the card's meta row (opens the section) and a paperclip button next to Delete (file picker).
- **Edit / preview dialogs** are rendered into `document.body` (portal): the card's `backdrop-filter` would otherwise confine a fixed modal to the card.
- **Waveform markers** - a small pin above the waveform at each During-call offset. Hover shows the thumbnail and file name; click seeks the player to that time and highlights the tile. Pins closer than ~1 % of the width merge into one pin with a count.
- **Transcript** - During-call attachments appear inline after the last segment starting at or before their offset, as a compact row (thumbnail + name + time). Clicking it opens the preview.
- **Edit** - one dialog for time and note. It shows when the file was captured and where that time came from, then:
  - **At** a time within the recording (`hh:mm:ss`, validated against the duration),
  - **Use playback position** - sets the time to where the player currently is (the natural way to place a pasted screenshot: play to the moment, click),
  - **Reference - the call as a whole**,
  - **Detect the time again from the file** - drops the manual time and re-runs detection,
  - **Note** (up to 1000 characters, blank removes it).
- **Out-of-date result hint** - on a `done` recording whose attachments changed after the result was produced: "Attachments changed since processing - Reprocess to include them." The backend already returns `attachmentsOutdated`; the UI shows the hint once Part 2 exists.

## Lifecycle
- Attachments live as long as the recording row:
  - **Delete** (and **Delete all processed**) removes the recording's attachments - rows and files - together with its audio. The tombstone keeps no attachments (it keeps no transcript or result either).
  - The sync's deletion reconciliation (recording gone from Plaud) removes the attachments together with the row and audio.
  - Deleting a recording removes everything Loopback stores for it - no attachment outlives its recording. Only files already exported to the output folder by Part 2 stay, as result files do today (the output folder is outside Loopback's control).
- **Processing** never waits for or locks attachments. Which attachments a run includes is decided by Part 2 (snapshot when `summarizing` starts).
- **Retry / Reprocess** see the attachments as they are at that moment.
- **Startup cleanup** - attachment folders / files without a row are deleted (a crash between writing the file and inserting the row).

## Storage
- Files: `<data>/attachments/{recordingId}/{attachmentId}.bin` on the `loopback-data` volume. The original name is never part of a path; the extension is not needed (the type is stored).
- SQLite, new table (migration `008_Attachments.sql`):
  ```sql
  CREATE TABLE Attachments (
      -- Version 7 GUID ("N" format, sorts by creation time).
      Id              TEXT    PRIMARY KEY,
      RecordingId     TEXT    NOT NULL,
      -- Original file name incl. extension (display, export name in Part 2).
      FileName        TEXT    NOT NULL,
      -- Detected MIME type, e.g. image/png, application/pdf, text/plain, application/octet-stream.
      ContentType     TEXT    NOT NULL,
      -- image | pdf | text | other
      Kind            TEXT    NOT NULL,
      SizeBytes       INTEGER NOT NULL,
      Sha256          TEXT    NOT NULL,
      -- Unix ms; NULL = unknown or moved to Reference by the user.
      CapturedAt      INTEGER NULL,
      -- exif | filename | modified | manual | none
      TimeSource      TEXT    NOT NULL,
      -- Unix ms sent by the browser, kept so "Reset to detected" can re-run detection.
      FileModifiedAt  INTEGER NULL,
      Note            TEXT    NULL,
      CreatedAt       INTEGER NOT NULL,
      UpdatedAt       INTEGER NOT NULL
  );
  CREATE INDEX IX_Attachments_RecordingId ON Attachments (RecordingId);
  CREATE UNIQUE INDEX UX_Attachments_RecordingId_Sha256 ON Attachments (RecordingId, Sha256);

  -- Last add / edit / delete of an attachment, compared with the result's processedAt
  -- for the "Attachments changed since processing" hint.
  ALTER TABLE Recordings ADD COLUMN AttachmentsChangedAt INTEGER NULL;
  ```
- "Moved to Reference" by the user = `CapturedAt NULL`, `TimeSource manual`.

## Backend
- New slice under `Features/Recordings` (attachments belong to a recording, they share its lifecycle): `Attachments/AttachmentStore.fs` (files, like `Audio/AudioStore.fs`), `Attachments/CaptureTime.fs` (detection - pure functions, unit-tested on the file-name patterns), `Attachments/FileKind.fs` (first-bytes detection).
- EXIF via the `MetadataExtractor` NuGet package (pure .NET, reads JPEG / HEIC / PNG / WebP).
- Domain additions:
  - Query: `Recording.Attachments : Attachment list` - `{ Id; FileName; ContentType; Kind; SizeBytes; CapturedAt; TimeSource; Note; Placement (DuringCall offsetSeconds | Reference); CreatedAt }`. Part of `getRecordings`, so the polling UI gets them without extra calls (metadata only, no file content).
  - Query: `GetAttachmentFile (recordingId, attachmentId) : AttachmentFile option` - `{ Path; FileName; ContentType; Kind }`.
  - `Recording.AttachmentsOutdated : bool` - `done` and `AttachmentsChangedAt` > the result's `processedAt`.
  - Commands: `AddAttachment { RecordingId; File (temp path); FileName; FileModifiedAt }`, `SetAttachmentTime { AttachmentId; Time: AtOffset of seconds | AsReference | Detected }`, `SetAttachmentNote { AttachmentId; Note }`, `DeleteAttachment { AttachmentId }`.
  - Events: `AttachmentAdded`, `AttachmentAlreadyExists`, `AttachmentUpdated`, `AttachmentDeleted`. Rejections raise `AttachmentRejected` (400), unknown ids `KeyNotFoundException` (404).
  - `AtOffset` is stored as an absolute `CapturedAt` (recording start + offset), `TimeSource manual`.
  - `AttachmentsChangedAt` is set by its own `UPDATE` and is not part of `RecordingRow`: steps that update the whole row (e.g. a long summarizing run) would overwrite a change made meanwhile.
- API (under `/api/recordings`):
  - `POST /{id}/attachments` - multipart, **one file per request** (the browser sends up to 3 in parallel per card, each with its own progress and error). Fields: `file`, `lastModified` (unix ms, omitted for clipboard pastes). Returns `{ attachmentId, alreadyExisted }`. 400 (plain-text reason) for an empty or too large file, the count limit or a deleted recording, 404 for an unknown one. The browser checks the 50 MB limit before sending - the server cuts an oversized body off. Not in the generated client - the web app streams it through `app/api/recordings/[id]/attachments/route.ts` (as uploads do).
  - `GET /{id}/attachments/{attachmentId}` - the file. `Content-Type` from the stored type, `X-Content-Type-Options: nosniff`, `Content-Disposition: inline` for image / pdf / text and `attachment` for other (`?download=1` forces `attachment`). Not in the generated client - proxied by `app/api/recordings/[id]/attachments/[attachmentId]/route.ts`.
  - `POST /attachments/time` (`{ attachmentId, mode: offset | reference | detected, offsetSeconds }` - `offsetSeconds` is used with `offset` only), `POST /attachments/note` (`{ attachmentId, note }`) and `POST /attachments/delete` (`{ attachmentId }`) - JSON, in the generated client, called via server actions.

## Plaud Marks
Plaud lets the user mark moments during a recording. They are imported during sync, as attachments (files) and marks (moments without a file). Uploading files in Loopback works as before - both sources end up side by side.

### What Plaud has (verified 2026-10-09, `spikes/fixtures/marks-*.json`)
- The recording list flags recordings with marks: `is_markmemo: true`.
- `GET /file/detail/{id}` -> `content_list[]` has an item `data_type: "mark_memo"` (`task_status` 1 = ready). Its content - a JSON array of marks as a string - is inline in `pre_download_content_list[]` (same `data_id`), or behind the item's `data_link` (presigned, maybe gzip).
- A mark: `mark_type`, `mark_id`, `mark_content`, `picture_link`, `timestamp` (ms from the recording start). The physical device's format adds `mark_status` (3 = analyzed, 0 = still analyzing - `mark_content` is then the placeholder "Highlighted. Analyzing…"), `is_user_edited`, `is_user_deleted`, `picture_full_url`.
- Files (`picture_link` = storage path) are downloadable via `download_path_mapping[path]` - presigned S3 URLs valid 15 minutes, no auth.
- Mark types:

| `mark_type` | Made by | Content | File | In Loopback |
|---|---|---|---|---|
| 1 | Plaud app: voice memo | Plaud's transcript of the memo | Ogg | mark (kind `voice`), note = Speechmatics' transcript; the audio is not kept |
| 2 | Plaud app: photo | Plaud's AI description of the image | PNG / JPEG | image attachment |
| 3 | Plaud app: text note | the user's text | - | mark (kind `note`) with that note |
| 4 | device button: highlight | Plaud's AI summary of the moment (filled in later) | - | mark (kind `highlight`) with that note |

- The highlight analysis runs in Plaud's background and does not change the recording's `version_ms`.

### Import
- Runs in its own background job after sync (`PlaudMarksBackgroundService`), for Plaud recordings that are not deleted and are flagged `is_markmemo` (stored by the sync as `Recordings.HasPlaudMarks`). A recording is fetched when:
  - its marks were never imported, or its `version_ms` changed since (marks added / edited in Plaud), or
  - some of its highlights were still analyzing - fetched again every 5 minutes, for up to 24 hours after the recording started.
- Plaud items are imported once and then belong to Loopback: editable and deletable there, and a deleted one is not imported again. Every imported Plaud mark is remembered in `PlaudMarkImports` (key = `mark_id`; a text note without one gets `type:timestamp:hash(content)`).
  - The one later update: a highlight imported while still analyzing gets its text once Plaud has it - unless the user edited its note in Loopback meanwhile.
  - Marks with `is_user_deleted` are skipped; unknown `mark_type`s are skipped and logged.
- **Photos** are downloaded right away (the links expire after 15 minutes) and stored as attachments with `Source = plaud`, the file name from the storage path (e.g. `20261009_114810_095861eb.png`), and the capture time **reported by Plaud** (recording start + `timestamp`, time source `plaud`). "Detect the time again" returns to that time.
  - Plaud's text goes to the attachment's new **description** (machine-made, not editable; the note stays the user's).
- **Voice memos** are a spoken note, so they become marks: the Ogg is downloaded to a temp file, transcribed by Speechmatics (same model / language as recordings; a few cents per hour of memos) and deleted - the transcript is the mark's note. If the transcription fails, Plaud's transcript is used and the failure is logged. (Plaud's own transcripts of Czech memos were unusable.)
- **Marks** (types 1, 3 and 4) are stored in the new `Marks` table: kind (`voice` | `note` | `highlight`), offset, note (empty while a highlight is analyzing), `Pending` (still analyzing), `UserEdited`.
- Placement of Plaud items never needs detection - the offset comes from Plaud. A timestamp beyond the recording is clamped to its end.
- A Plaud or download failure skips that recording until the next run; it never blocks the sync, the audio or the processing.

### UI
- **Marks** are pins of their own color on the waveform and rows in the transcript at their time, with an icon per kind (flag = highlight, sticky note = note, microphone = voice memo), and are listed at the top of the card's section, which becomes **Marks & attachments**. A mark row: time (jumps the player there), note or "Analyzing in Plaud…", Edit note, Delete.
- Plaud attachments show a small `Plaud` badge and their description under the note.
- Audio attachments (uploaded ones - Plaud voice memos are marks) play right in their tile (the file route forwards `Range`, so they can seek).
- The card header shows the mark count (flag) next to the attachment count.
- Deleting a recording removes its marks and import keys too (as attachments).

### Storage
- Migration `009_PlaudMarks.sql`:
  - `Attachments`: `Source` (`upload` | `plaud`), `Description`, `ReportedCapturedAt` (capture time from Plaud, used by "detect again").
  - `Marks`: `Id`, `RecordingId`, `OffsetMs`, `Note`, `Source` (`plaud`), `Pending`, `UserEdited`, `CreatedAt`, `UpdatedAt`.
  - `PlaudMarkImports`: (`RecordingId`, `MarkKey`) -> `TargetId` (attachment or mark id), `Pending`, `ImportedAt`.
  - `Recordings`: `HasPlaudMarks`, `PlaudMarksVersion` (`version_ms` of the last import), `PlaudMarksCheckedAt` - set by their own `UPDATE`s, not part of `RecordingRow`.
- Migration `010_VoiceMemoMarks.sql`: `Marks.Kind`; voice memos imported as audio attachments by the first version are removed (rows, import keys, files) and their recordings imported again.

## Notes added in Loopback
- A **note** button (sticky note) next to the paperclip in the card header opens an "Add note" dialog: the note text, and its time.
- The time is the player's position once the user put the player somewhere (clicked / dragged the waveform, played, jumped to a time or a pin); otherwise the note starts **without a time** - about the call as a whole. The dialog can change either (`hh:mm:ss`, Use playback position, No time).
- Stored as a mark: kind `note`, `Source = upload`, `UserEdited = 1`, `OffsetMs` NULL when without a time (migration `012_UserNotes.sql` makes `Marks.OffsetMs` nullable - a table rebuild). The note is required (up to 1000 characters).
- Timed notes behave like Plaud marks (waveform pin, transcript row, marks list); notes without a time are listed after the timed ones with a "No time" badge, and are not on the waveform or in the transcript.
- API: `POST /marks/add` (`{ recordingId, atTime, offsetSeconds, note }` - `offsetSeconds` is used with `atTime` only) -> `{ markId }`. Edit note / delete as for Plaud marks.
- Processing: a note without a time is listed with the reference attachments under `Shared around the call:`; in the export its `offsetSeconds`, `capturedAt` and `context` are `null` (docs/attachments-processing.md).

## Out of Scope (Part 1)
- AI description / OCR of attachments, attachments in the summary input, links from the result content, the exported files and `attachments` in the result JSON - all Part 2.
- Dragging tiles onto transcript segments to place them (Edit time + Use playback position covers it).
- Attaching to a recording before it exists in Loopback (e.g. screenshots taken before the Plaud sync) - they can be attached once it syncs; their capture time places them correctly.
- URL attachments and chat-log import.
- Thumbnail generation, HEIC conversion.
- Writing anything back to Plaud.
