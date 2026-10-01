# Riffado ↔ Plaud Sync – Analysis

Source analysed: `../Riffado` (Next.js / TypeScript, Drizzle + Postgres) and `../RiffadoExtension` (the "Riffado Connector" Chrome MV3 extension, built output).
Goal: document exactly how Riffado talks to Plaud's cloud so Loopback can implement the same integration.

> Plaud has **no public API**. Everything below is the private API that `web.plaud.ai` uses, reverse-engineered by the Riffado project. Treat every shape as unstable and parse defensively.

---

## 1. Big picture

```
 Plaud device ──BT/WiFi──▶ Plaud cloud (api*.plaud.ai)  ◀── web.plaud.ai (official SPA)
                                   ▲
                                   │  HTTPS, Bearer JWT, browser-like headers
                                   │
                         Riffado server (poll-based pull)
                           ├─ list files      GET  /file/simple/web
                           ├─ get audio URL   GET  /file/temp-url/{id}  → presigned S3 → download
                           ├─ get AI content  GET  /file/detail/{id}   → inline JSON or presigned .json.gz
                           └─ (optional) push title back  PATCH /file/{id}
```

- Sync is **one-way pull**, Plaud → Riffado. The only write-back is renaming a file (`PATCH /file/{id}`) with an AI-generated title, behind a user setting.
- No webhooks/push from Plaud. Riffado **polls** (server worker every 5 min + a browser-tab poller).
- Change detection is per-file via `version_ms`; there is no "changes since" cursor.

---

## 2. Plaud API reference (as used by Riffado)

### 2.1 Hosts / regions

| Region | API base |
|---|---|
| Global (US, `aws:us-west-2`) | `https://api.plaud.ai` |
| EU Frankfurt (`aws:eu-central-1`) | `https://api-euc1.plaud.ai` |
| APAC Singapore (`aws:ap-southeast-1`) | `https://api-apse1.plaud.ai` |
| China mainland (separate account universe) | `https://api.plaud.cn` |

An account lives in exactly one region. Calling the wrong region returns empty lists or a redirect, not an error. Region discovery:

1. **OTP login:** `POST /auth/otp-send-code` on the global host answers `status: -302` with `data.domains.api` = the regional base. Follow it (Riffado caps at 3 hops).
2. **From the JWT:** the user token has a `region` claim (`aws:us-west-2` / `aws:eu-central-1` / `aws:ap-southeast-1`) → map to the table above (the Connector extension does this).
3. **From web.plaud.ai localStorage:** key `*:workspaceList` → `[0].domain`.

SSRF guard: only accept `https:` hosts `plaud.ai`, `*.plaud.ai`, `plaud.cn`, `*.plaud.cn` (`src/lib/plaud/servers.ts`).

### 2.2 Common request shape

```
Authorization: Bearer <token>
Content-Type: application/json
User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) ... Chrome/120 Safari/537.36
```

- A browser User-Agent is **required** (issue #132: requests without it fail).
- When routed via proxy, Riffado also adds full Chrome fingerprint headers: `origin: https://web.plaud.ai`, `referer: https://web.plaud.ai/`, `sec-ch-ua*`, `sec-fetch-*`, `accept-language`, etc. (`src/lib/plaud/fetch.ts`). The hosted instance uses rotating Webshare proxies because datacenter IPs get 403'd; self-host usually works direct.
- Responses are HTTP 200 with a business `status` field (`0` = OK). Always check `status`, not just HTTP code. Bodies can be non-JSON on errors (issue #142) → parse text first.

### 2.3 Auth endpoints

| Call | Auth | Body / params | Result |
|---|---|---|---|
| `POST /auth/otp-send-code` | none | `{ "username": "<email>" }` | `{ status: 0, token: "<otpToken>" }` or `{ status: -302, data.domains.api }` |
| `POST /auth/otp-login` | none | `{ "code": "123456", "token": "<otpToken>" }` | `access_token` (top level or `data.access_token`) = **User Token (UT)** |
| `GET /user/me` | UT | – | `email` or `data.email` (best-effort) |
| `GET /team-app/workspaces/list?need_personal_workspace=true` | UT | – | `data.workspaces[]` (`workspace_id`, `workspace_type`, `role`, `api_domain`, …) |
| `POST /user-app/auth/workspace/token/{workspaceId}` | UT | `{}` | `data.workspace_token` = **Workspace Token (WT)** (+ `expires_in`, `refresh_token`, …) |

### 2.4 Data endpoints (use the WT)

| Call | Purpose |
|---|---|
| `GET /device/list` | `data_devices[]: { sn, name, model, version_number }`. Used as the "is this token valid" probe. |
| `GET /file/simple/web?skip=&limit=&is_trash=0&sort_by=edit_time&is_desc=true` | Paged recording list: `data_file_total`, `data_file_list[]`. |
| `GET /file/temp-url/{fileId}?is_opus=0\|1` | `temp_url` (and `temp_url_opus`) – presigned download URL for audio. |
| `GET /file/detail/{fileId}` | Content manifest: transcript/summary items + inline content. |
| `PATCH /file/{fileId}` | `{ "filename": "..." }` – rename (only write Riffado does). |

Recording object (`data_file_list[]`, `src/types/plaud.ts`):

```ts
id: string              // stable Plaud file id → the sync key
filename, fullname, filetype, filesize (bytes), file_md5
start_time, end_time    // unix ms
duration                // ms
timezone, zonemins, scene, serial_number (device SN)
version, version_ms     // version_ms changes when the file is edited → change key
edit_time, edit_from
is_trash: boolean
is_trans: boolean       // Plaud has a transcript
is_summary: boolean     // Plaud has a summary
keywords[], filetag_id_list[], ori_ready
```

Audio download: presigned URLs (`resource.plaud.ai` / S3) need **no** auth header. The file is labelled `.mp3` but the bytes are usually **Ogg/Opus** (`OggS`, vendor `PALUD.AI`) – sniff magic bytes, don't trust the extension (issue #160, `src/lib/audio/sniff.ts`). Riffado calls with `is_opus=0` and stores whatever it sniffs.

### 2.5 Plaud-native transcript & summary (`/file/detail/{id}`)

```jsonc
{ "status": 0,
  "data": {
    "file_id": "...", "file_name": "...", "duration": ..., "start_time": ..., "scene": ...,
    "content_list": [
      { "data_id": "source_transaction:...", "data_type": "transaction",   "task_status": 1, "data_link": "https://...trans_result.json.gz" },
      { "data_id": "auto_sum:...",           "data_type": "auto_sum_note", "task_status": 1, "data_link": "https://..." },
      { "data_id": "sum_multi:...",          "data_type": "sum_multi_note", ... },
      { "data_type": "outline", ... }
    ],
    "pre_download_content_list": [
      { "data_id": "auto_sum:...", "data_content": "<JSON string>" }   // inline copy, no S3 round-trip
    ]
  } }
```

Selection rules (`src/lib/plaud/content.ts`):
- `data_type` strings drift → match on type **or** `data_id` prefix.
- Transcript = first `transaction`/`transcript` or `source_transaction:*`.
- Summary = prefer `auto_sum_note` / `auto_sum:*`; fall back to `sum_multi_note` etc.
- Item is ready only if `task_status === 1` **and** `data_link` present.
- Prefer inline `pre_download_content_list[data_id].data_content`; else fetch `data_link` (no auth, may be gzip – check `1f 8b` magic and gunzip). 403 on a link = expired presign, skip the item.
- Transcript body: array (or `{segments|transcript|data: [...]}`) of `{ start_time, end_time, speaker, content|text }`. Riffado flattens to `Speaker: text` lines. Shape is marked "unverified" in the code.
- Summary body: string or object with `ai_content | summary | content`, optional `key_points`, `action_items`.

---

## 3. Tokens – the part that bites

Plaud uses two JWTs:

| | User Token (UT) | Workspace Token (WT) |
|---|---|---|
| Lifetime | ~300 days (`exp` claim) | ~24 h |
| Claims | `exp`, `iat`, `sub`, `region`, … | additionally `ut_ref`, `wid`, `wtype` |
| Obtained by | OTP login, or copied from web.plaud.ai | `POST /user-app/auth/workspace/token/{wsId}` using the UT |
| Used for | workspace list, minting WT, `/user/me` | all `/file/*` and `/device/*` calls |

Rules Riffado follows:
- **Store only the UT** (encrypted AES-256-GCM). Mint a fresh WT at the start of every sync run – in memory only (`PlaudClient.ensureWorkspaceToken`).
- Reject a pasted WT (`isPlaudWorkspaceToken`: has `ut_ref` or `wid`) – it validates fine against `/device/list` but dies within a day and can't be refreshed. Users grab it by mistake from the Network tab.
- Cache `workspace_id` on the connection. Mint with cached id; on 4xx (stale) re-list workspaces, pick `workspace_type === "0"` (personal) or the first one, and retry.
- If WT minting fails entirely, fall back to calling data endpoints with the UT (works on some accounts; regional servers return empty lists without WT).
- There is **no refresh flow** for the UT (a `refresh_token` column was added then dropped – migrations 0012/0014). When the UT expires, the user reconnects.
- 401 anywhere → `PLAUD_INVALID_TOKEN` → set `plaud_connections.invalidated_at`, return `needsReconnect: true`, show a reconnect banner. Cleared automatically on the next successful sync (self-heals transient 401s) (issue #225).

### 3.1 Three ways to connect

1. **Email OTP** (`/api/plaud/auth/send-code` → `/api/plaud/auth/verify`). Riffado just proxies; OTP code and otpToken are not stored.
   ⚠️ Accounts created with *Continue with Google/Apple* are a **different identity** on Plaud's side – OTP with the same email logs into an empty shadow account (issue #65).
2. **Paste token** (`/api/plaud/auth/connect-token`). User copies the UT from web.plaud.ai: localStorage key `pld_tokenstr` (not the `Authorization` header of `/file/*` requests, which is the WT). Strip `Bearer `, check 3 JWT segments, reject WT, reject expired `exp`, user picks region.
3. **Riffado Connector extension** (`RiffadoExtension/`). Opens `https://web.plaud.ai/`, user logs in with any method (incl. Google/Apple), then:
   - new version: polls the cookie **`pld_ut`** on `web.plaud.ai` (`chrome.cookies`), every 750 ms for up to 90 s;
   - older content script: reads localStorage `pld_tokenstr`, else the longest JWT-looking `pld_*` value (skipping `*:workspaceList`, `*:frillSsoToken`);
   - derives `apiBase` from the `*:workspaceList` domain or the JWT `region` claim;
   - verifies with `GET /team-app/workspaces/list` (`status === 0`) and hands `{accessToken, apiBase, region}` to the Riffado tab via a content-script bridge, which posts it to `connect-token` with `source: "connector"`.

All three end in `persistPlaudConnection` (`src/lib/plaud/persist-connection.ts`): discover workspace → validate via `/device/list` → in one transaction under a Postgres advisory lock (`pg_advisory_xact_lock(hashtextextended('plaud_connect:'||userId,0))`) upsert the connection row (clearing `invalidated_at`) and upsert devices by `(userId, serialNumber)`.

---

## 4. The sync algorithm (`src/lib/sync/sync-recordings.ts`)

### 4.1 Triggers

| Trigger | Where | Details |
|---|---|---|
| Server background worker | `src/lib/sync/worker.ts`, started from `instrumentation.ts` | `setInterval` every `BACKGROUND_SYNC_INTERVAL_MS` (default 5 min), first tick after 30 s. Claims ≤ 20 users whose `last_sync` is NULL or older than 4 min, oldest first; syncs them sequentially. Non-reentrant (`running` flag). |
| Browser poller | `src/hooks/use-auto-sync.ts` | Calls `POST /api/plaud/sync` every 5 min (min 1 min), on mount, and on tab-visible if > interval/2 elapsed. |
| Manual button | `POST /api/plaud/sync` | Rate-limited per user (`PLAUD_SYNC_RATE_LIMIT_PER_MINUTE`, default 10). |

Concurrency: an in-process `Map<userId, Promise>` coalesces concurrent syncs for the same user (second caller awaits the first and gets `inProgress: true`). Cross-process safety relies on the rate limit + the 4-minute staleness window – there is no distributed lock for sync itself.

### 4.2 Run steps

```
runSyncRecordingsForUser(userId)
 1. load plaud_connections row          → none: skipped "no_connection"
 2. load settings + user                → suspended / hosted lockout: skip
 3. client = PlaudClient(decrypt(UT), apiBase, cachedWorkspaceId)   // WT minted lazily on first call
 4. for page in 0..19  (PAGE_SIZE=50, MAX_PAGES=20 → max 1000 files per run)
      list = GET /file/simple/web?skip=page*50&limit=50&is_trash=0&sort_by=edit_time&is_desc=true
      if empty → stop
      process in batches of 5 in parallel (Promise.allSettled) → processRecording()
      stop conditions:
        - storage cap hit
        - page shorter than 50 (last page)
        - 2 consecutive pages with nothing new/updated/importable
          (and, if content import is on, no older recordings still missing Plaud content)
 5. update connection: last_sync=now, invalidated_at=NULL, workspace_id if newly resolved
 6. notifications (email / Bark) for new recordings
 7. importPlaudContent(candidates)  – sequential, gap-fill only
 8. auto-transcribe with user's own provider (skip ones that got a Plaud transcript in 'plaud_only' mode)
 on 401 anywhere: invalidated_at=now, needsReconnect=true
```

Sorting by `edit_time DESC` + early stop after two "quiet" pages is what makes routine syncs cheap: new and recently edited files are always at the front.

### 4.3 Per-file logic (`processRecording`)

```
existing = recordings where (userId, plaudFileId = rec.id)
versionKey = String(rec.version_ms)

if existing && existing.plaudVersion == versionKey:
    → unchanged; maybe emit an import candidate if Plaud content is still missing locally
    → skipped
if existing.deletedAt:            → skipped   (tombstone: user deleted it locally, never resurrect)
if !existing && over storage cap: → skipped   (checked BEFORE downloading, using rec.filesize)

audio   = GET /file/temp-url/{id}?is_opus=0 → GET temp_url
type    = sniff magic bytes (ogg/opus usually)
key     = existing.storagePath or "{userId}/{plaudFileId}.{ext}" (suffix " (2)" etc. on collision)
upload to storage (local FS or S3)

row = { deviceSn: serial_number, plaudFileId: id, filename (encrypted), duration, startTime, endTime,
        filesize, fileMd5, storagePath, downloadedAt, plaudVersion: versionKey,
        timezone, zonemins, scene, isTrash }

if existing: in tx SELECT ... FOR UPDATE; if tombstoned meanwhile → delete uploaded blob, skip
             else UPDATE → webhook "recording.updated"
else:        INSERT → webhook "recording.synced", queue for auto-transcribe
```

Key points:
- **Identity** = `(userId, plaudFileId)` with a unique constraint (issue #79: file ids must be scoped per user).
- **Change detection** = `version_ms` string. Any change (rename in Plaud, edit) re-downloads the audio. `file_md5` is stored but not used for dedup.
- **Deletes are local-only tombstones** (`deleted_at`); the row stays so the next sync skips that `plaudFileId`.
- **Plaud-side deletions/trash are not propagated.** Sync only lists `is_trash=0`, so a file trashed in Plaud simply stops appearing; Riffado keeps its copy. No reconciliation pass exists.
- Errors are per-file (collected into `errors[]`), sync continues. Filenames in errors never go to telemetry.

### 4.4 Plaud content import (`importPlaudContent`)

Opt-in (`importPlaudContent` setting). Candidates = new/updated/unchanged files with `is_trans || is_summary`, not trashed, where the local DB lacks a `source='plaud'` transcription or any summary. Processed **sequentially** (gentle on the 24 h WT); a 401 stops the whole pass, other errors skip one item. Never overwrites existing data. `transcriptMode`: `plaud_only` (don't spend own AI credits if Plaud already transcribed) or `keep_both`.

### 4.5 Retry / resilience (`PlaudClient.request`)

- 429: honour `Retry-After`, else exponential backoff 1 s/2 s/4 s, max 3 retries → `PLAUD_RATE_LIMITED`.
- 5xx: same backoff, 3 retries → `PLAUD_UPSTREAM_ERROR` (502).
- Network `TypeError`: same backoff.
- 401 → `PLAUD_INVALID_TOKEN` (no retry). Other 4xx → `PLAUD_API_ERROR` with Plaud's `msg`.
- Proxy mode: rotate proxy once on 403/407/network error.

---

## 5. Data model (Riffado, Postgres)

```
plaud_connections  id, user_id, bearer_token(enc UT), api_base, plaud_email, workspace_id,
                   invalidated_at, last_sync, created_at, updated_at
plaud_devices      id, user_id, serial_number, name, model, version_number   UNIQUE(user_id, serial_number)
recordings         id, user_id, device_sn, plaud_file_id, filename(enc), duration, start_time, end_time,
                   filesize, file_md5, storage_type, storage_path, downloaded_at, plaud_version,
                   timezone, zonemins, scene, is_trash, waveform_peaks, deleted_at, ...
                   UNIQUE(user_id, plaud_file_id)
transcriptions     recording_id, user_id, text(enc), source ('plaud' | own provider), provider, model, ...
ai_enhancements    recording_id, user_id, summary, key_points, action_items, source, ...
```

---

## 6. Recommendations for Loopback

**Minimum viable sync** (single user, self-hosted):

1. Get a **UT** once: easiest is paste of `pld_tokenstr` (localStorage) / `pld_ut` (cookie) from web.plaud.ai; OTP flow only works for email-registered accounts. Reject tokens with `wid`/`ut_ref` claims. Read `region` claim → API base.
2. Persist: UT (encrypted), `apiBase`, `workspaceId`, `lastSync`, `invalidatedAt`.
3. Each sync run: list workspaces (once, cache id) → mint WT → page `/file/simple/web` sorted by `edit_time desc`, 50 per page → for each file compare `version_ms` to stored → download via `/file/temp-url` → sniff format → store → upsert row keyed by Plaud `id`.
4. Stop paging after the last page or two pages with no changes.
5. Optionally pull `/file/detail/{id}` for files with `is_trans`/`is_summary` to get Plaud's own transcript/summary.
6. On 401: mark connection as needing reconnect; don't delete anything.

**Things to copy as-is**
- Browser `User-Agent` on every call; check business `status`, not only HTTP status; parse body as text first.
- UT/WT split; never persist the WT.
- `version_ms` as the change key; `(owner, plaudFileId)` unique key; local tombstones for user deletes.
- Magic-byte sniffing of audio (expect Ogg/Opus named `.mp3`).
- Gzip detection on content links; prefer inline `pre_download_content_list`.
- Region handling via `-302` / JWT `region` claim.

**Gaps in Riffado worth doing better**
- No propagation of Plaud-side deletes/trash – consider a periodic full reconciliation (list all ids incl. `is_trash=1`) if Loopback needs mirror semantics.
- Hard cap of 1000 files per run (20×50) – first import of a large account takes several runs; fine, but make it resumable/explicit.
- Any metadata-only change (e.g. rename) re-downloads the whole audio; comparing `file_md5` / `filesize` first would avoid that.
- Sync coalescing is in-process only; if Loopback runs multiple workers, use a DB lock (e.g. advisory lock like the connect path) around a user's sync.
- No UT refresh; plan UX for re-connecting roughly every ~300 days (read `exp` and warn ahead of time).
- Transcript segment shape is unverified – capture a real `trans_result.json.gz` early and write fixtures.

**Key source files** (in `../Riffado/src`)

| File | What |
|---|---|
| `lib/plaud/client.ts` | HTTP client: endpoints, retries, WT handling |
| `lib/plaud/workspace.ts` | Workspace list + WT minting |
| `lib/plaud/auth.ts` | OTP, JWT claim decoding, WT detection |
| `lib/plaud/servers.ts` | Regions, UA, host validation |
| `lib/plaud/fetch.ts`, `proxy.ts` | Browser headers, proxy rotation |
| `lib/plaud/content.ts` | `/file/detail` parsing |
| `lib/plaud/persist-connection.ts` | Connect flow persistence |
| `lib/sync/sync-recordings.ts` | The sync engine |
| `lib/sync/worker.ts` | Background scheduler |
| `lib/audio/sniff.ts` | Audio format detection |
| `types/plaud.ts` | Response types |
| `app/api/plaud/**` | HTTP routes (connect, sync, connection) |
| `../RiffadoExtension/assets/background.ts-*.js`, `content-plaud.ts-*.js` | Token capture from web.plaud.ai |
