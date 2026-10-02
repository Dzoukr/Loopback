# Loopback Foundation

This file describes the basic foundation of what Loopback is and what are its goals.

## Definition
Loopback is a locally-running service (via Docker, plus a host-side `claude` bridge) that synchronizes recordings from Plaud (plaud.ai) and offers one-click processing - transcription and summarization - which ends as a `JSON` file stored at a preconfigured location on local disk.

## User Flow
1. Every N minutes (configurable, minimum 1), recordings are synced from Plaud by a background job (metadata); right after, the audio job downloads each recording's audio into local storage once.
2. List with new recordings is refreshed.
3. User can listen to a track: a waveform shows the recording - click anywhere to play from there. Playback streams the locally stored audio (backend `GET /api/recordings/{id}/audio` with HTTP Range for seeking, proxied by the web app's `app/api/recordings/[id]/audio` route) - no Plaud call. If the audio is not stored yet, it is downloaded on that first request. Then the user selects the workflow (how to treat the recording), and click on the "Process" button, which triggers the process flow:
    1. Recording is sent for transcription to the Speechmatics API.
    2. Transcript is stored in the local DB, then the API for removing the transcription from Speechmatics is called (a later failure can be retried without paying for transcription again).
    3. Transcript is processed by `claude` (via the bridge) using the selected workflow's prompts: `summary.md` + `merge.md` turn the transcript into the extracted information (by default summary, key points, and action items - shaped by the workflow's schema); then `title.md` generates the title **from that result, not from the raw transcript**. The title run's input is the recording's local date/time (`Recorded: 2026-09-30 11:26 (UTC+02:00)`, from Plaud's `timezone`/`zonemins`), its duration and the result JSON. Workflow prompts are system prompts used as-is - there are no `{placeholders}`; the input always arrives as the message.
    4. `JSON` file `loopback-{plaudFileId}.json` (fixed envelope with title and metadata, plus the extracted information) is stored in the output folder (`./output`, a Docker bind mount - point it elsewhere in `docker-compose.yml`). The title is kept locally only, it is not written back to Plaud.
    5. The result JSON is also stored in SQLite, so the UI shows it (rendered generically from the workflow's schema: text as paragraphs, lists as bullets, leading `[Topic]` as badges) even after the file was moved or deleted from the output folder. Processed recordings stay playable (with waveform) as long as the file exists in Plaud (the local audio is removed together with the row).
    6. Source recording is marked as deleted (tombstone) in the local DB so it is not re-synced again. Plaud remains intact (it has no known delete API endpoint).

## Processing
- Each recording has a status: `synced → queued → transcribing → summarizing → done | failed`.
- **Delete** (every recording, trash icon, with confirmation): hides it in Loopback. Plaud has no known delete API, so the row becomes a `deleted` tombstone - the sync skips it, stored transcript and result are cleared, the result file in the output folder is kept - and is removed for good once the file is deleted in Plaud too. A running Speechmatics job is deleted; a background step already in progress re-checks the status before writing and drops its work.
- **Reprocess** (processed recordings with a stored transcript): runs the selected workflow again - summary passes, merge, title - on the stored transcript, without transcribing (no Speechmatics cost). Overwrites the result file and the stored result.
- A failed recording shows the error and can be retried from the failed step.
- Clicking "Process" only sets the status to `queued` (with the selected workflow); all work is done by background jobs (see below).

## Background Jobs
Both jobs are ASP.NET `BackgroundService`s inside the backend. All job state lives in SQLite, so a restart (`docker compose down/up`) resumes exactly where it stopped - no in-memory queues.
- **Sync job** - every N minutes (configurable, or on "Sync now"): lists recordings from Plaud, inserts new ones / updates changed ones (`version_ms`), skips tombstoned ones.
  - After each sync it reconciles deletions: a `done` or `synced` recording whose file is gone from Plaud - from both the main list and the trash (a trashed file can be restored) - is removed from SQLite. The tombstone is no longer needed once Plaud cannot return the file. Failed / in-progress recordings are kept (a failed one may hold a paid-for transcript), and result files in the output folder are never touched. If any Plaud listing call fails, nothing is deleted in that run.
- **Audio job** - right after sync, downloads each new recording's Ogg/Opus file once (fresh Plaud presigned URL) into `<data>/audio/{plaudFileId}.ogg` (the `loopback-data` volume, ~2 MB per 8 min), so playback, waveform and transcription need no Plaud connection afterwards. Then computes the waveform peaks (600 values in [0, 1], stored in SQLite) from the file, decoded with Concentus (pure .NET Opus, ~900x realtime: 91 min in ~6 s). Done server-side because browsers would have to decode the whole file to PCM (~1 GB for 90 min). A failed download / peaks run is retried after 30 min. The audio is deleted when the recording is deleted in Loopback, or when its row is removed after the file was deleted in Plaud.
- **Plaud session** (verified 2026-10-02): the backend logs in with `PLAUD_EMAIL` / `PLAUD_PASSWORD` itself, so it has a login session of its own - using web.plaud.ai in a browser (or logging out there) does not affect it. Nothing to do for the user after setup. Three layers, each renewed from the one below, cheapest first, all stored in SQLite (`PlaudConnection`, treat as sensitive):
  - workspace token (data calls) ← 30-day workspace refresh token (no user token needed);
  - 24 h user token (mints workspace tokens) ← 30-day `pld_urt` refresh cookie (no password; rotates on every use);
  - login session ← password login, only when everything above was rejected or expired.
  - Plaud can revoke a session at any time (`-419` / `-420`); the backend then refreshes, re-mints, or logs in again, and retries once.
  - Plaud throttles logins per hour and every login creates a new session, so after a refused login (wrong password, two-factor code required, ...) the next attempt waits 60 minutes (or a restart after fixing `.env`); the error is shown in the UI. A changed `PLAUD_EMAIL` starts a new session.
- **Processing job** - every few seconds (configurable), takes recordings by status:
  - `queued` → uploads the locally stored audio as a Speechmatics batch job (downloads it first if still missing), stores the Speechmatics job id on the recording, sets `transcribing`.
  - `transcribing` → polls the stored Speechmatics job (one status call per tick, no blocking wait). When done: stores the transcript in SQLite, deletes the Speechmatics job, sets `summarizing`. When Speechmatics reports failure, or the job exceeds a configurable max age: `failed`.
  - `summarizing` → runs title + ensemble summary/merge via the Claude Bridge, validates against the schema, writes the `JSON` file, sets `done` and tombstones the recording.
  - Any error sets `failed` with the error message and the step it failed in; retry puts it back to that step (a stored transcript is never re-transcribed).
- Speechmatics specifics (verified with `spikes/speechmatics-probe.fsx`, 2026-10-01):
  - Submit `POST /v2/jobs` (multipart: `config` + `data_file` with the local Ogg/Opus file) - the audio is uploaded with the job, Plaud is not involved.
  - `transcription_config`: `{ "model": "melia-1", "language": "multi", "diarization": "speaker" }` (melia uses `model`, not `operating_point`, and no `auto` language). Mixed Czech/Slovak/English is handled in one transcript.
  - Speed: a 91 min recording finished in ~30 s (~$0.20) - a poll interval of ~10 s is plenty.
  - Transcript: `GET /v2/jobs/{id}/transcript?format=json-v2` → `results[]` of words/punctuation with `alternatives[0].{content, speaker, language, confidence}`; the backend merges them into speaker segments (91 min ≈ 230 segments, ~80k chars ≈ 20k tokens for Claude).
  - `DELETE /v2/jobs/{id}` → 200, then the job returns 404.
  - Diarization found 10 speakers (S1..S10) in that recording - may need `speaker_diarization_config` tuning (e.g. `max_speakers`) per workflow.
- Speechmatics is called directly from the backend (submit → poll → fetch → delete as separate steps). There is no Speechmatics proxy - RiffadoDocker needed one only because Riffado expects a synchronous OpenAI-style call.
- A workflow = one subfolder of `workflows/` (e.g. `workflows/Default/`, `workflows/Recording/`); the folder name is what the UI dropdown shows. Every subfolder contains exactly 3 files:
  - `title.md` - system prompt for title generation (single run, plain text)
  - `summary.md` - system prompt for converting the transcript to information (shaped by the schema)
  - `merge.md` - system prompt for merging the ensemble passes together
- Optionally, a subfolder can also contain `schema.json` - the JSON Schema used for converting the transcript to information (summary and merge runs) for that workflow. If it is absent, `workflows/default.schema.json` (summary, keyPoints, actionItems) is used.
- A subfolder missing any of the 3 prompt files is not offered in the UI (and is logged as a warning); so is one whose resolved schema is missing or not valid JSON Schema. Prompt and schema files are re-read on every run, so edits apply without restart.
- The backend validates the summary/merge result against the resolved schema, then writes the final `JSON` file as a fixed envelope with the result nested inside:
  `{ "title", "workflow", "plaudFileId", "recordedAt", "durationSeconds", "processedAt", "content": { ...schema-shaped result... } }`
  The envelope is fixed; only `content` varies by workflow.
- Claude settings per workflow: `workflows/default.claude.json` holds `model` (haiku | sonnet | opus | full `claude-*` id), `effort` (low | medium | high | xhigh | max), `passes` (1-10) and `timeoutSeconds` (1-3600, per claude call) for all workflows (built-in defaults: sonnet, high, 3, 300). A workflow can override single keys with its own `claude.json` next to `title.md` (e.g. `{ "model": "opus" }` keeps the shared effort and passes). One model + effort is used for the title, the summary passes and the merge, so the merge is never weaker than the passes it merges. Unknown keys or invalid values hide the workflow from the UI with a logged warning (caught before anything is transcribed and paid for). Bridge infrastructure (`CLAUDE_BRIDGE_URL`, `CLAUDE_BRIDGE_TOKEN`, `MAX_CONCURRENCY`) stays in `.env`. `MAX_CONCURRENCY` caps parallel `claude` processes for the whole bridge; passes beyond it queue and the queueing counts against the timeout, so the backend logs a warning when a workflow's `passes` exceeds it (read from the bridge's `/health`).
- Ensemble: `passes` identical summary runs (`summary.md`) in parallel, then a merge run (`merge.md`), using the same model and schema. `passes: 1` disables it (`merge.md` unused); a failed merge falls back to the richest pass.

## Claude Bridge
- `claude` runs on the **host**, not in Docker (a container cannot use the host's `claude` binary or its subscription login).
- `claude-bridge.fsx` - a single F# script run with `dotnet fsi`, using only built-in .NET libraries (`HttpListener`, `Process`, `System.Text.Json`). The host needs the .NET SDK.
- The bridge is dumb and stateless: one request = one `claude` call. Prompts, schema, ensemble and validation live in the backend.
- Contract:
  - `POST /run` `{ system, prompt, model, effort, schema?, timeoutSeconds? }` → `{ result, structuredOutput, usage, durationMs }` (400 bad input, 401 bad token, 502 claude error, 504 timeout)
  - `GET /health` → claude version, whether `--json-schema` is supported, default/max timeout, `maxConcurrency`
- Every call executes exactly:
  `claude -p --output-format json --exclude-dynamic-system-prompt-sections --tools "" --strict-mcp-config --disable-slash-commands --no-session-persistence --system-prompt <system> --model <model> --effort <effort> [--json-schema <schema>]`
  with the prompt on stdin (`--strict-mcp-config` + `--disable-slash-commands` keep the host's MCP servers and skills out of the context - without them a trivial call costs ~200x more; `--bare` is not an option, as it disables subscription login), cwd = neutral temp dir, per-request timeout (`timeoutSeconds` from the workflow, default 300 s, capped at 3600 s; whole process tree killed on timeout -> 504).
- `model` is required (haiku | sonnet | opus | full id); `effort` is always passed explicitly, so the host's `~/.claude/settings.json` never influences results.
- Requests are handled concurrently (ensemble passes must run in parallel).
- Security: listens on `127.0.0.1:<port>` only and requires a shared-secret header. The backend calls `http://host.docker.internal:<port>` and overrides the `Host` header to `127.0.0.1:<port>` (`HttpListener` matches on it).
- Billing goes against the Claude subscription the host's `claude` is logged into.

## Essentials
- Everything must be easy to run and stop - via `loopback-up.cmd` and `loopback-down.cmd`:
  - `loopback-up.cmd` starts the bridge (`conhost.exe --headless dotnet fsi claude-bridge.fsx`) if not already running, registers it for logon autostart (HKCU `Run`), and runs `docker compose up -d`.
  - `loopback-down.cmd` runs `docker compose down`, removes the autostart entry, and stops the bridge. The port is owned by Windows' HTTP.sys (PID 4), not the bridge, so it is stopped by matching the `dotnet` process whose command line contains `claude-bridge.fsx` - not by "kill whatever listens on the port" (as RiffadoDocker does). Use `GET /health` to check whether it is running.
- All configuration is local and file-based, next to `docker-compose.yml`: `.env` for settings (incl. secrets) and `workflows/` for workflows
- For transcription, the Speechmatics API is used
- For summarization and other AI-based actions, the local `claude` process is used (via the Claude Bridge)

## Technical
- Two containers (`docker-compose.yml`): `web` (Next.js server, port 3000 - the only published port) and `server` (F# backend, reachable only from `web`).
- Frontend is React via Next.js with DaisyUI (Tailwind 4) and Font Awesome. Every backend call goes through **server actions** (BFF, as in Triple19) - components never call the backend; the typed client `src/lib/generated/api-client.ts` is generated from the backend's OpenAPI with `yarn generate:api` (server running in Development).
- Backend is ASP.NET with F# (Giraffe endpoint routing, OpenAPI via Giraffe.OpenApi), structured in **vertical slices** like Triple19: `Features/<Feature>/Domain.fs → Database.fs → Queries.fs → CommandHandler.fs → API.fs` (CQRS: queries vs. commands returning events), plus `*BackgroundService.fs` files. External services are clients in `Integrations/` (`Plaud.fs`, `Speechmatics.fs`, `ClaudeBridge.fs`).
- Paths come from environment variables set by `docker-compose.yml` (`LOOPBACK_WORKFLOWS`, `LOOPBACK_DATA`, `LOOPBACK_OUTPUT`), with defaults relative to `src/Server` for `dotnet run`, which also loads the repo's `.env` (`LOOPBACK_ENV_FILE`). Real environment variables win over `.env` (e.g. `CLAUDE_BRIDGE_URL=http://127.0.0.1:9100` for `dotnet run` outside Docker, where `host.docker.internal` resolves to the LAN IP the bridge does not listen on).
- For DB we use SQLite on the **named Docker volume** `loopback-data` (not a bind mount: SQLite's WAL locking does not work across Docker Desktop's VM boundary - writing the file from Windows while the server ran corrupted it once). Never open it from the host while the server runs; copy it out with `docker compose cp server:/data/loopback.db .`. Accessed accessed via `Dapper.FSharp` (`Dapper.FSharp.SQLite` module over `Microsoft.Data.Sqlite`). The DB schema is created/migrated by plain SQL scripts run at startup, since Dapper.FSharp has no migrations.
- Configuration is **one `.env` file** (distribution-friendly: the two images are configured purely by environment variables; Compose passes them via `env_file: .env`, `claude-bridge.fsx` and `loopback-up.cmd` read the same file). It holds the secrets and is gitignored; the committed template is `.env.example`. The committed `.githooks/pre-commit` hook (enabled by `git config core.hooksPath .githooks`, which `loopback-up.cmd` sets) blocks committing `.env`, `spikes/fixtures/`, or any added line that looks like a JWT / API key / `*_TOKEN=` / `*_KEY=` / `*_SECRET=` value.
  - Required: `PLAUD_EMAIL`, `PLAUD_PASSWORD`, `SPEECHMATICS_API_KEY`, `CLAUDE_BRIDGE_TOKEN`.
  - Optional (defaults in `.env.example` and `Configuration.fs`): `PLAUD_SYNC_INTERVAL_MINUTES`, `SPEECHMATICS_MODEL`, `SPEECHMATICS_LANGUAGE`, `SPEECHMATICS_MAX_JOB_MINUTES`, `CLAUDE_BRIDGE_URL`, `MAX_CONCURRENCY`, `PROCESSING_POLL_SECONDS`.
- Distribution: two images (`server`, `web`) plus a small folder - `docker-compose.yml` (switch `build:` to `image:`), `.env.example`, `workflows/`, `claude-bridge.fsx`, `loopback-up.cmd`, `loopback-down.cmd`. The host needs Docker Desktop, the .NET SDK and a logged-in `claude` CLI (the bridge cannot run in a container); the start scripts are Windows-only.
  - `workflows/` - `default.schema.json` and `default.claude.json` plus one subfolder per workflow, each with `title.md`, `summary.md`, `merge.md` and optional `schema.json` / `claude.json`:
    ```
    workflows/
      default.schema.json
      default.claude.json  (model, effort, passes, timeoutSeconds for all workflows)
      Default/
        title.md
        summary.md
        merge.md
      Recording/
        title.md
        summary.md
        merge.md
        schema.json          (optional - overrides default.schema.json for this workflow)
        claude.json          (optional - overrides single keys of default.claude.json)
    ```
