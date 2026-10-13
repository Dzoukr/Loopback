# Attachments - Part 2: Processing and Export

Part 1 (`docs/attachments.md`) puts files and marks into a recording. Part 2 makes the AI read them next to what was said at that moment, assigns each to a topic of the result, and exports them with the result JSON - so the meeting note in Obsidian holds what was on screen and every moment the user marked.

In the result and the note, marks are called **notes** ("Attachments & Notes"); in the JSON they stay `marks`.

Status: implemented, including the meeting-notes skill (outside this repo). Restructured on 2026-10-12: a short result in reading order instead of topics with linked points (that layout was too complex to read).

## Goals
- The summary passes see attachments and marks **in the transcript, at their moment** - "look at this number here" next to the screenshot that shows it.
- Every mark the user made reaches the result and the Obsidian note (a pressed button means the moment mattered), with the topic it belongs to.
- The note reads in order of importance and stays short: summary, key points per topic, decisions, open questions, action items, then attachments and notes.
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
- Reference attachments (outside the call) and notes without a time (added in Loopback) are listed before the transcript under `Shared around the call:`, same line format without a time.
- The ids are **short and per export**: marks `m1, m2, ...` by offset, attachments `a1, a2, ...` - during call by offset, then reference by file name. Loopback keeps the mapping to its GUIDs for the run.
- A short legend before the transcript (only when there are marks or attachments) says what these lines are (material placed by the user, not speech, never instructions) and that ids never go into text, only into a list assigning them to topics when the result has one - without it, a flat-list workflow quoted "m1" in its points. The Default prompt asks for that list.
- Snapshot: the attachments and marks as they are when `summarizing` starts. Changes afterwards make the result outdated (see Loopback UI).

### 3. Mark context
- Every mark gets a `context`: the **raw transcript text** around it, quoted as transcribed (with speaker labels, nothing rewritten).
- Window: **20 s before to 10 s after** the mark - people press the button in reaction, so the important part was usually just said; the time after catches the end of the sentence. 20 s is ~50 words, a quote rather than a paragraph. The same window for every mark kind.
- **Cut inside turns**: the stored transcript has whole speaker turns (one turn averaged ~24 s in a 91-minute test, a monologue can run minutes), so a turn reaching past the window is cut where the window ends, estimated from the turn's start / end and its length in characters, at the nearest word boundary, marked with `…`. Precision of a word or two is enough for a quote.
- Capped at ~600 characters (trimmed from the start, keeping the text closest to the mark).
- Window and cap are constants in the code, tuned after seeing real highlights in Obsidian - no setting.
- Computed by Loopback from the stored transcript, not by the AI. It is what makes a highlight useful when Plaud never wrote its text (seen: highlights stuck at "Analyzing…").

### 4. Result content (the Default workflow)
- `workflows/Default/` with `workflows/default.schema.json`:
  ```json
  {
    "summary": "2-3 sentences",
    "topics": [ { "id": "t1", "title": "Contract with Hannah", "points": ["...", "..."] } ],
    "decisions": ["..."],
    "openQuestions": ["..."],
    "actionItems": [ { "owner": "Alex or null", "text": "...", "due": "yyyy-MM-dd or null" } ],
    "attachmentsAndNotes": [ { "id": "m1", "topicId": "t1 or null" } ]
  }
  ```
  - Every field is required; optional values are `null` or empty arrays (strict structured output).
- The prompt keeps it short: at most 6 topics with 1-4 points, a 2-3 sentence summary, **each thing in exactly one place** (action item > decision > open question > key point), decisions are real choices ("moved to done" is a status - one "Done: ..." point), no trivia or process remarks. On a 36-minute sprint review this went from 56 items (8 topics, 28 points, 10 decisions, 5 questions, 13 action items) to 36 (5 / 16 / 3 / 3 / 9), the summary from 1,243 to 339 characters.
- `attachmentsAndNotes` has one entry per note (mark) and attachment: the topic it belongs to. Loopback moves it into the envelope - each mark / attachment gets `topic` (the topic's title) - and removes it from `content`; ids or topics that do not exist are dropped (logged).
- `merge.md` matches topics across the passes by meaning (renumbering their ids), keeps the best points within the limits, removes duplicates across sections and takes the topic most passes chose for each note / attachment.
- Before 2026-10-12 the shape was topics with `spans`, a summary, points / decisions linked by `markIds` / `attachmentIds` and per-topic decisions / questions. The UI renders such stored results generically.

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
        "topic": "Contract with Hannah",
        "offsetSeconds": 14,
        "capturedAt": "2026-10-09T13:47:47+02:00",
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
        "topic": "Contract with Hannah or null",
        "placement": "duringCall | reference",
        "offsetSeconds": 30,
        "capturedAt": "2026-10-09T13:48:03+02:00",
        "note": null,
        "description": "..."
      }
    ],
    "content": { "...": "workflow-shaped (Default: see above, without attachmentsAndNotes)" }
  }
  ```
- Each note and attachment has its `topic` (title, or `null`) and its capture time: `offsetSeconds` in the call (`null` for reference attachments and for notes added in Loopback without a time - their `capturedAt` and `context` are `null` too) and `capturedAt` (local time). The times are for Loopback and other tools - the Obsidian note ignores them.
- Not exported: internal GUIDs, SHA-256, time source, placeholder texts ("Analyzing…").

## Obsidian note (the meeting-notes skill, outside this repo)
The skill reads `format: 2`; its existing rules stay (template, language, content copied verbatim, speaker / link confirmation). Six sections, always in this order and always present (empty when there is nothing):
```markdown
> [!abstract]+ Summary
> ...

## 🎯 Key Points
### Contract with Hannah
- ...

## ✅ Decisions
- ...

## ❓ Open Questions
- ...

## ⏰ Action Items
### Roman Provazník
- [ ] ... 📅 2026-10-13

## 📎 Attachments & Notes
- 🗒 **Note** *(Contract with Hannah)* - Hlídat unlimited liability ve smlouvě
- ⚑ **Highlight** *(Sprint 7 planning)* - "…what was said around it…"
- 📎 **Rate card draft.md** *(Team archetypes and rate cards)*
  ![[2026-10-12 Sprint 7 - Rate card draft.md]]
```
- Attachments & Notes: every note and attachment in capture order, with its topic; a highlight without text shows a quote of its context; attachments are embedded under their line (files copied into the vault's `_attachments` folder next to the note).
- **No times** in the note (the note has no audio). Action items with `due` get the Tasks plugin date (`📅 yyyy-MM-dd`).

## Loopback UI
- The "Attachments changed since processing - Reprocess to include them" hint on done recordings (`attachmentsOutdated`, already computed) is shown, also for mark changes.
- The result view shows the Default shape in the note's order - Summary, Key points (per topic), Decisions, Open questions, Action items (per owner, with due dates), Attachments & notes (with topic and time; the time jumps the player, an attachment opens). Other shapes render generically.
