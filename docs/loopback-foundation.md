# Loopback Foundation

This file describes the basic foundation of what Loopback is and what are its goals.

## Definition
Loopback is a locally-running service (via Docker, plus a host-side `claude` bridge) that synchronizes recordings from Plaud (plaud.ai) and offers one-click processing - transcription and summarization - which ends as a `JSON` file stored at a preconfigured location on local disk.

## User Flow
1. Every N minutes (configurable, minimum 1), recordings are synced from Plaud by a background job (metadata only, audio is not downloaded).
2. List with new recordings is refreshed.
3. User can listen to a track (audio is streamed on demand through the backend from Plaud's temporary URL), select the nature of processing (how to treat the recording), and click on the "Process" button, which triggers the process flow:
    1. Recording is sent for transcription to the Speechmatics API.
    2. Transcript is stored in the local DB, then the API for removing the transcription from Speechmatics is called (a later failure can be retried without paying for transcription again).
    3. Transcript is processed by `claude` (via the bridge) using the selected nature's prompts: `title.md` for the title, `summary.md` + `merge.md` for the extracted information (by default summary, key points, and action items - shaped by the nature's schema).
    4. `JSON` file (fixed envelope with title and metadata, plus the extracted information) is stored on the configurable local file system (using Docker volumes). The title is kept locally only, it is not written back to Plaud.
    5. Source recording is marked as deleted (tombstone) in the local DB so it is not re-synced again. Plaud remains intact (it has no known delete API endpoint).

## Processing
- Each recording has a status: `synced → queued → transcribing → summarizing → done | failed`.
- A failed recording shows the error and can be retried from the failed step.
- Clicking "Process" only sets the status to `queued` (with the selected nature); all work is done by background jobs (see below).

## Background Jobs
Both jobs are ASP.NET `BackgroundService`s inside the backend. All job state lives in SQLite, so a restart (`docker compose down/up`) resumes exactly where it stopped - no in-memory queues.
- **Sync job** - every N minutes (configurable): lists recordings from Plaud, inserts new ones / updates changed ones (`version_ms`), skips tombstoned ones.
- **Processing job** - every few seconds (configurable), takes recordings by status:
  - `queued` → fetches a fresh Plaud temporary audio URL, submits a Speechmatics batch job, stores the Speechmatics job id on the recording, sets `transcribing`.
  - `transcribing` → polls the stored Speechmatics job (one status call per tick, no blocking wait). When done: stores the transcript in SQLite, deletes the Speechmatics job, sets `summarizing`. When Speechmatics reports failure, or the job exceeds a configurable max age: `failed`.
  - `summarizing` → runs title + ensemble summary/merge via the Claude Bridge, validates against the schema, writes the `JSON` file, sets `done` and tombstones the recording.
  - Any error sets `failed` with the error message and the step it failed in; retry puts it back to that step (a stored transcript is never re-transcribed).
- Speechmatics specifics (verified with `spikes/speechmatics-probe.fsx`, 2026-10-01):
  - Submit `POST /v2/jobs` (multipart, `config` field only) with `fetch_data.url` = fresh Plaud presigned URL (valid 60 min) - Speechmatics fetches the audio itself, the backend never downloads it.
  - `transcription_config`: `{ "model": "melia-1", "language": "multi", "diarization": "speaker" }` (melia uses `model`, not `operating_point`, and no `auto` language). Mixed Czech/Slovak/English is handled in one transcript.
  - Speed: a 91 min recording finished in ~30 s (~$0.20) - a poll interval of ~10 s is plenty.
  - Transcript: `GET /v2/jobs/{id}/transcript?format=json-v2` → `results[]` of words/punctuation with `alternatives[0].{content, speaker, language, confidence}`; the backend merges them into speaker segments (91 min ≈ 230 segments, ~80k chars ≈ 20k tokens for Claude).
  - `DELETE /v2/jobs/{id}` → 200, then the job returns 404.
  - Diarization found 10 speakers (S1..S10) in that recording - may need `speaker_diarization_config` tuning (e.g. `max_speakers`) per nature.
- Speechmatics is called directly from the backend (submit → poll → fetch → delete as separate steps). There is no Speechmatics proxy - RiffadoDocker needed one only because Riffado expects a synchronous OpenAI-style call.
- Nature of processing = one subfolder of `prompts/` (e.g. `prompts/Default/`, `prompts/Recording/`); the folder name is what the UI dropdown shows. Every subfolder contains exactly 3 files:
  - `title.md` - system prompt for title generation (single run, plain text)
  - `summary.md` - system prompt for converting the transcript to information (shaped by the schema)
  - `merge.md` - system prompt for merging the ensemble passes together
- Optionally, a subfolder can also contain `schema.json` - the JSON Schema used for converting the transcript to information (summary and merge runs) for that nature. If it is absent, `prompts/default.schema.json` (summary, keyPoints, actionItems) is used.
- A subfolder missing any of the 3 prompt files is not offered in the UI (and is logged as a warning); so is one whose resolved schema is missing or not valid JSON Schema. Prompt and schema files are re-read on every run, so edits apply without restart.
- The backend validates the summary/merge result against the resolved schema, then writes the final `JSON` file as a fixed envelope with the result nested inside:
  `{ "title", "nature", "plaudFileId", "recordedAt", "durationSeconds", "processedAt", "content": { ...schema-shaped result... } }`
  The envelope is fixed; only `content` varies by nature.
- Ensemble: `passes` identical summary runs (`summary.md`) in parallel, then a merge run (`merge.md`), using the same model and schema. `passes: 1` disables it (`merge.md` unused); a failed merge falls back to the richest pass.

## Claude Bridge
- `claude` runs on the **host**, not in Docker (a container cannot use the host's `claude` binary or its subscription login).
- `claude-bridge.fsx` - a single F# script run with `dotnet fsi`, using only built-in .NET libraries (`HttpListener`, `Process`, `System.Text.Json`). The host needs the .NET SDK.
- The bridge is dumb and stateless: one request = one `claude` call. Prompts, schema, ensemble and validation live in the backend.
- Contract:
  - `POST /run` `{ system, prompt, model, effort, schema? }` → `{ result, structuredOutput, usage, durationMs }` (400 bad input, 401 bad token, 502 claude error, 504 timeout)
  - `GET /health` → claude version + whether `--json-schema` is supported
- Every call executes exactly:
  `claude -p --output-format json --exclude-dynamic-system-prompt-sections --tools "" --strict-mcp-config --disable-slash-commands --no-session-persistence --system-prompt <system> --model <model> --effort <effort> [--json-schema <schema>]`
  with the prompt on stdin (`--strict-mcp-config` + `--disable-slash-commands` keep the host's MCP servers and skills out of the context - without them a trivial call costs ~200x more; `--bare` is not an option, as it disables subscription login), cwd = neutral temp dir, configurable timeout (default 300 s, whole process tree killed on timeout).
- `model` is required (haiku | sonnet | opus | full id); `effort` is always passed explicitly, so the host's `~/.claude/settings.json` never influences results.
- Requests are handled concurrently (ensemble passes must run in parallel).
- Security: listens on `127.0.0.1:<port>` only and requires a shared-secret header. The backend calls `http://host.docker.internal:<port>` and overrides the `Host` header to `127.0.0.1:<port>` (`HttpListener` matches on it).
- Billing goes against the Claude subscription the host's `claude` is logged into.

## Essentials
- Everything must be easy to run and stop - via `loopback-up.cmd` and `loopback-down.cmd`:
  - `loopback-up.cmd` starts the bridge (`conhost.exe --headless dotnet fsi claude-bridge.fsx`) if not already running, registers it for logon autostart (HKCU `Run`), and runs `docker compose up -d`.
  - `loopback-down.cmd` runs `docker compose down`, removes the autostart entry, and stops the bridge. The port is owned by Windows' HTTP.sys (PID 4), not the bridge, so it is stopped by matching the `dotnet` process whose command line contains `claude-bridge.fsx` - not by "kill whatever listens on the port" (as RiffadoDocker does). Use `GET /health` to check whether it is running.
- All configuration should be local file-based (files at the same location as `docker-compose.yml`)
- For transcription, the Speechmatics API is used
- For summarization and other AI-based actions, the local `claude` process is used (via the Claude Bridge)

## Technical
- Frontend is React via Next.js (static export) with DaisyUI as UI library, served by the backend - one container
- Backend should be in ASP.NET with F# (using Giraffe for REST API)
- For DB we use SQLite (on a Docker volume), accessed via `Dapper.FSharp` (`Dapper.FSharp.SQLite` module over `Microsoft.Data.Sqlite`). The DB schema is created/migrated by plain SQL scripts run at startup, since Dapper.FSharp has no migrations.
- Local configuration files should be `JSON`:
  - `config.json` - the single configuration file, **including secrets** (simplicity over splitting into `.env`). It is gitignored; the committed template is `config.example.json` (placeholders only). The committed `.githooks/pre-commit` hook (enabled by `git config core.hooksPath .githooks`, which `loopback-up.cmd` sets) blocks committing `config.json`, `.env`, `spikes/fixtures/`, or any added line that looks like a JWT / API key. Contents: Plaud token, sync interval, Speechmatics key and settings, output path, and the `claude` section:
    `{ "bridgeUrl": "http://host.docker.internal:9100", "token": "...", "model": "sonnet", "effort": "high", "passes": 3, "timeoutSeconds": 300 }`
  - `prompts/` - `default.schema.json` plus one subfolder per nature, each with `title.md`, `summary.md`, `merge.md` and optional `schema.json`:
    ```
    prompts/
      default.schema.json
      Default/
        title.md
        summary.md
        merge.md
      Recording/
        title.md
        summary.md
        merge.md
        schema.json      (optional - overrides default.schema.json for this nature)
    ```
