# Loopback Foundation

This file describes the basic foundation of what Loopback is and what are its goals.

## Definition
Loopback is a locally-running service (via Docker, plus a host-side `claude` bridge) that synchronizes recordings from Plaud (plaud.ai) and offers one-click processing - transcription and summarization - which ends as a `JSON` file stored at a preconfigured location on local disk.

## User Flow
1. Every N minutes (configurable, minimum 1), recordings are synced from Plaud in the background (metadata only, audio is not downloaded).
2. List with new recordings is refreshed.
3. User can listen to a track (audio is streamed on demand through the backend from Plaud's temporary URL), select the nature of processing (how to treat the recording), and click on the "Process" button, which triggers the process flow:
    1. Recording is sent for transcription to the Speechmatics API.
    2. Transcript is stored in the local DB, then the API for removing the transcription from Speechmatics is called (a later failure can be retried without paying for transcription again).
    3. Transcript is processed by `claude` (via the bridge) using the selected nature's prompts: `title.md` for the title, `summary.md` + `merge.md` for the extracted information (by default summary, key points, and action items - shaped by the nature's schema).
    4. `JSON` file (fixed envelope with title and metadata, plus the extracted information) is stored on the configurable local file system (using Docker volumes). The title is kept locally only, it is not written back to Plaud.
    5. Source recording is marked as deleted (tombstone) in the local DB so it is not re-synced again. Plaud remains intact (it has no known delete API endpoint).

## Processing
- Each recording has a status: `synced → transcribing → summarizing → done | failed`.
- A failed recording shows the error and can be retried from the failed step.
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
- For DB we use SQLite (on a Docker volume)
- Local configuration files should be `JSON`:
  - `config.json` - Plaud token, sync interval, Speechmatics key and settings, output path, and the `claude` section:
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
