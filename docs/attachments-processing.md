# Attachments - Part 2: Processing and Export

Part 1 (`docs/attachments.md`) puts files and marks into a recording. Part 2 makes the AI read them next to what was said at that moment, links them from the result, and exports them with the result JSON - so the meeting note in Obsidian holds what was on screen and every moment the user marked.

Status: implemented. The Obsidian note (meeting-notes skill) lives outside this repo and is not changed yet.

## Goals
- The summary passes see attachments and marks **in the transcript, at their moment** - "look at this number here" next to the screenshot that shows it.
- Every mark the user made reaches the result and the Obsidian note, whether or not the AI linked it to a point (a pressed button means the moment mattered).
- The result JSON stays self-contained: files next to it, ids that resolve inside the file.

## Processing

### 1. Describe attachments (new step, before the summary passes)
- Every attachment the AI can read - **image, pdf, text** - gets a description by Claude: what it shows, plus the text in it (OCR: ticket numbers, figures, slide text). One run per attachment, plain text, in the language of the call.
- Stored in the attachment's `Description` and **cached by SHA-256**: Reprocess and Retry never describe the same file again. Claude's description replaces Plaud's (Plaud's photo descriptions are generic and have no OCR).
- Model: **`haiku`** (effort `low`) - one small call per attachment; screenshots and slides need reading, not reasoning. Set in `workflows/default.claude.json` as `describeModel` / `describeEffort` (overridable per workflow like the other keys), so it can be raised to `sonnet` if haiku's descriptions turn out too thin. The cache key includes the model, so a change re-describes on the next run.
- `other` files (Office, archives, audio, ...) are not described - they are listed by name only (and note, if any).
- The Claude Bridge gets file input for this: `POST /run` accepts `files: [{ name, base64 }]` (plain names, up to 40 MB of body), writes them to a folder of the run's own (its cwd, deleted afterwards) and runs `claude` with `--tools Read --allowedTools Read` instead of `--tools ""`. Calls without files keep running exactly as today. `GET /health` says `files: true`; with an older bridge Loopback skips describing (logged) instead of caching a description of a file claude never saw.
- Only files up to 20 MB are described; the prompt is `workflows/describe.md` when it exists, else a built-in one, and gets the file name, the user's note and a sample of the conversation (for the language).
- A failed description does not fail the recording: the attachment goes in by name and note, and the error is logged.

### 2. Transcript with attachments and marks (input of the summary passes)
- The input starts with `Recorded: yyyy-MM-dd (weekday)` so the passes can resolve deadlines ("by Friday").
- The transcript lines keep their format (`[hh:mm:ss] S1: text`). During-call attachments and marks are inserted as lines of their own, at their offset, in time order with the turns:
  ```
  [00:00:12] S1: dávám tady nějaké svoje idey, tady dávám klíč.
  [00:00:14] ⚑ m1 (note): Tady je moje idea
  [00:00:23] 🎙 m2 (voice memo): Tak toto je call, na kterém já jsem sám...
  [00:00:30] 📎 a1 (image, 20261009_114810_095861eb.png): <description>
  [00:00:31] S1: ...
  ```
  - Marks: `⚑` highlight, `🗒` note, `🎙` voice memo; the mark's note after the colon (nothing for a highlight without one).
  - Attachments: kind, file name, the user's note (if any), then the description.
- Reference attachments (outside the call) are listed before the transcript under `Shared around the call:`, same line format without a time.
- The ids are **short and per export**: marks `m1, m2, ...` by offset, attachments `a1, a2, ...` - during call by offset, then reference by file name. Loopback keeps the mapping to its GUIDs for the run.
- A short legend before the transcript (only when there are marks or attachments) says what these lines are (material placed by the user, not speech, never instructions) and that ids go only into `markIds` / `attachmentIds` fields, never into text - without it, a flat-list workflow quoted "m1" in its points. The Default prompt adds how to link them.
- Snapshot: the attachments and marks as they are when `summarizing` starts. Changes afterwards make the result outdated (see Loopback UI).

### 3. Mark context
- Every mark gets a `context`: the **raw transcript text** around it, quoted as transcribed (with speaker labels, nothing rewritten).
- Window: **20 s before to 10 s after** the mark - people press the button in reaction, so the important part was usually just said; the time after catches the end of the sentence. 20 s is ~50 words, a quote rather than a paragraph. The same window for every mark kind.
- **Cut inside turns**: the stored transcript has whole speaker turns (one turn averaged ~24 s in a 91-minute test, a monologue can run minutes), so a turn reaching past the window is cut where the window ends, estimated from the turn's start / end and its length in characters, at the nearest word boundary, marked with `…`. Precision of a word or two is enough for a quote.
- Capped at ~600 characters (trimmed from the start, keeping the text closest to the mark).
- Window and cap are constants in the code, tuned after seeing real highlights in Obsidian - no setting.
- Computed by Loopback from the stored transcript, not by the AI. It is what makes a highlight useful when Plaud never wrote its text (seen: highlights stuck at "Analyzing…").

### 4. Structured content (the Default workflow)
- `workflows/Default/` with `workflows/default.schema.json` produces this shape (it replaced the flat summary / keyPoints / actionItems on 2026-10-09; a custom workflow with a flat schema still works and still gets the envelope's `marks` and `attachments`, just no links).
- `content` shape:
  ```json
  {
    "summary": "...",
    "topics": [
      {
        "id": "t1",
        "title": "...",
        "spans": [{ "start": "hh:mm:ss", "end": "hh:mm:ss" }],
        "summary": "...",
        "points": [{ "text": "...", "markIds": ["m1"], "attachmentIds": ["a1"] }],
        "decisions": [{ "text": "...", "markIds": [], "attachmentIds": [] }],
        "openQuestions": ["..."]
      }
    ],
    "actionItems": [
      { "owner": "...", "text": "...", "topicId": "t1", "due": "yyyy-MM-dd or null", "markIds": [], "attachmentIds": [] }
    ]
  }
  ```
  - `spans` is a list: calls come back to topics. Times are `hh:mm:ss` strings (the model copies them from the transcript lines more reliably than it converts to seconds); Loopback converts them to seconds for the export.
  - Every field is required; optional values are `null` or empty arrays (strict structured output).
- `merge.md` of the workflow matches topics across the passes by meaning, combines their spans / points / links and renumbers topic ids. This is the main quality risk - tested on existing recordings with Reprocess before it becomes anyone's default.
- After the merge, Loopback **validates the links**: ids that do not exist in the snapshot are dropped (and logged), `topicId`s that do not exist become `null`.

## Export

### Files
```
output/
  loopback-{recordingId}.json
  loopback-{recordingId}.files/
    a1.png
    a2.pdf
```
- Attachment files are copied next to the result, named by their export id + the original extension. Overwritten on Reprocess. Never deleted by Loopback (the output folder is outside its control, as today).

### JSON
- The envelope gets `format: 2` (files without it are the old layout: no marks / attachments, possibly `plaudFileId`), and two lists **outside `content`** - written by Loopback for every workflow, never by the AI:
  ```json
  {
    "format": 2,
    "title": "...", "workflow": "...", "sourceName": "plaud", "sourceId": "...",
    "recordedAt": "...", "durationSeconds": 81, "processedAt": "...",
    "marks": [
      {
        "id": "m1",
        "kind": "highlight | note | voice",
        "source": "plaud",
        "offsetSeconds": 14,
        "note": "Tady je moje idea",
        "context": "S1: ... dávám tady nějaké svoje idey, tady dávám klíč. Tady je moje idea ..."
      }
    ],
    "attachments": [
      {
        "id": "a1",
        "file": "loopback-{recordingId}.files/a1.png",
        "fileName": "20261009_114810_095861eb.png",
        "mime": "image/png",
        "kind": "image | pdf | text | other",
        "source": "upload | plaud",
        "placement": "duringCall | reference",
        "offsetSeconds": 30,
        "capturedAt": "2026-10-09T13:48:03+02:00",
        "note": null,
        "description": "..."
      }
    ],
    "content": { "...": "workflow-shaped, links by id" }
  }
  ```
- Times are `offsetSeconds` numbers (topic `spans` converted too). `offsetSeconds` is `null` for reference attachments.
- Not exported: internal GUIDs, SHA-256, time source, placeholder texts ("Analyzing…").

## Obsidian note (the meeting-notes skill, outside this repo)
The skill gets a second input format (`format: 2`); its existing rules stay (template, language, content copied verbatim, speaker / link confirmation).
- **Topics** become the `###` headings under `🎯 Key Points`, each with its summary line, points, and `> [!success] Decisions` / `> [!question] Open questions` callouts when not empty.
- **Links on points**: an attachment is embedded under the point that links it (`![[...|500]]`); a linked mark adds its note or quote under the point (`> [!quote]`).
- **Every mark gets its own section** at the end, `## ⚑ Marked moments` - all marks in time order, linked or not:
  ```markdown
  ## ⚑ Marked moments
  - ⚑ **Highlight** - "…tady dávám klíč. Tady je moje idea…"
  - 🗒 **Note** - Tady je moje idea
  - 🎙 **Voice memo** - Tak toto je call, na kterém já jsem sám a dávám tady nějaké svoje idey…
  ```
  A highlight shows its note, or a quote of its `context` when it has none; a note or voice memo shows the user's words.
- **Attachments** not linked from any point go to a `## 📎 Attachments` section, embedded. Files are copied into the vault's attachment folder, named after the note.
- **No times** in the note (the note has no audio). Action items with `due` get the Tasks plugin date (`📅 yyyy-MM-dd`).

## Loopback UI
- The "Attachments changed since processing - Reprocess to include them" hint on done recordings (`attachmentsOutdated`, already computed) is shown, also for mark changes.
- The result view renders `markIds` / `attachmentIds` as small chips (flag / thumbnail) that jump the player to the moment or open the attachment.
