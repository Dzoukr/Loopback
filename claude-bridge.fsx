// claude-bridge.fsx
// =============================================================================
// Host-side HTTP bridge that lets the Loopback backend (running in Docker) use
// the host's `claude` CLI. A container cannot run the host's claude binary or
// use its subscription login, so this runs on the host and the backend calls it
// at http://host.docker.internal:<port>. See docs/loopback-foundation.md,
// section "Claude Bridge".
//
// Dumb and stateless: one request = one `claude -p` call. Prompts, schemas,
// ensemble and validation live in the backend.
//
//   Run (windowless):  conhost.exe --headless dotnet fsi claude-bridge.fsx
//   Run (debug):       dotnet fsi claude-bridge.fsx
//
// Settings come from .env next to this script (override the path with LOOPBACK_ENV_FILE);
// real environment variables win over the file:
//   CLAUDE_BRIDGE_URL       port is taken from it        (default 9100)
//   CLAUDE_BRIDGE_TOKEN     shared secret, X-Bridge-Token (required)
//   MAX_CONCURRENCY         max parallel claude processes (default 4)
// The timeout is per request (`timeoutSeconds`, set per workflow in default.claude.json /
// claude.json): default 300 s, capped at 3600 s so a typo cannot leave claude running for hours.
//   CLAUDE_BIN              claude binary                 (default: `claude` on PATH)
//
// Endpoints:
//   GET  /health  -> { status, claudeVersion, jsonSchema, ... }      (no token)
//   POST /run     { system, prompt, model, effort, schema?, timeoutSeconds? }
//                 -> { result, structuredOutput, usage, costUsd, durationMs }
//                 400 bad input, 401 bad token, 502 claude error, 504 timeout
// =============================================================================

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

let utf8 = UTF8Encoding(false)

// --------------------------------------------------------------------------- //
// Logging (console + claude-bridge.log; the console is invisible when headless)
// --------------------------------------------------------------------------- //
let logPath = Path.Combine(__SOURCE_DIRECTORY__, "claude-bridge.log")
let logLock = obj ()

let log fmt =
    Printf.kprintf (fun (msg: string) ->
        let line = sprintf "%s %s" (DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss") msg
        lock logLock (fun () ->
            Console.WriteLine line
            try
                let fi = FileInfo logPath
                if fi.Exists && fi.Length > 5_000_000L then File.Move(logPath, logPath + ".1", true)
                File.AppendAllText(logPath, line + Environment.NewLine, utf8)
            with _ -> ())) fmt

// --------------------------------------------------------------------------- //
// Config
// --------------------------------------------------------------------------- //
type Config =
    { Port: int
      Token: string
      MaxConcurrency: int
      ClaudeBin: string
      WorkDir: string }

/// KEY=VALUE lines of the .env file (same format the backend and Docker Compose read).
let readDotEnv (path: string) =
    if not (File.Exists path) then Map.empty
    else
        File.ReadAllLines path
        |> Array.map _.Trim()
        |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#") && l.Contains "=")
        |> Array.map (fun l ->
            let i = l.IndexOf '='
            let value = l.Substring(i + 1).Trim()
            let value =
                if value.Length >= 2 && ((value.StartsWith "\"" && value.EndsWith "\"") || (value.StartsWith "'" && value.EndsWith "'"))
                then value.Substring(1, value.Length - 2)
                else value
            l.Substring(0, i).Trim(), value)
        |> Map.ofArray

let loadConfig () =
    let path =
        match Environment.GetEnvironmentVariable "LOOPBACK_ENV_FILE" with
        | null | "" -> Path.Combine(__SOURCE_DIRECTORY__, ".env")
        | p -> p
    let dotEnv = readDotEnv path
    // Real environment variables win over .env.
    let str (name: string) =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> dotEnv |> Map.tryFind name |> Option.filter (String.IsNullOrWhiteSpace >> not)
        | v -> Some v
    let num (name: string) =
        str name |> Option.bind (fun v -> match Int32.TryParse v with | true, n -> Some n | _ -> None)
    let token =
        match str "CLAUDE_BRIDGE_TOKEN" with
        | Some t -> t
        | None -> failwithf "CLAUDE_BRIDGE_TOKEN is required (shared secret for X-Bridge-Token) - set it in %s" path
    let workDir = Path.Combine(Path.GetTempPath(), "loopback-claude")
    Directory.CreateDirectory workDir |> ignore
    { Port = str "CLAUDE_BRIDGE_URL" |> Option.map (fun u -> Uri(u).Port) |> Option.defaultValue 9100
      Token = token
      MaxConcurrency = num "MAX_CONCURRENCY" |> Option.defaultValue 4 |> max 1
      ClaudeBin = str "CLAUDE_BIN" |> Option.defaultValue "claude"
      // A neutral, empty cwd keeps any project CLAUDE.md out of the prompt.
      WorkDir = workDir }

let cfg =
    try loadConfig ()
    with ex ->
        log "claude-bridge cannot start: %s" ex.Message
        exit 1

// --------------------------------------------------------------------------- //
// claude binary probe (once at startup)
// --------------------------------------------------------------------------- //
let runSimple (args: string list) =
    try
        let psi =
            ProcessStartInfo(cfg.ClaudeBin, UseShellExecute = false, CreateNoWindow = true,
                             RedirectStandardOutput = true, RedirectStandardError = true,
                             WorkingDirectory = cfg.WorkDir)
        args |> List.iter psi.ArgumentList.Add
        use p = Process.Start psi
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        if p.WaitForExit 30_000 then Some(out.Result + err.Result)
        else
            (try p.Kill true with _ -> ())
            None
    with _ -> None

let claudeVersion = runSimple [ "--version" ] |> Option.map (fun s -> s.Trim())
let schemaSupported = runSimple [ "--help" ] |> Option.exists (fun s -> s.Contains "--json-schema")

// --------------------------------------------------------------------------- //
// Request validation
// --------------------------------------------------------------------------- //
type RunRequest =
    { System: string
      Prompt: string
      Model: string
      Effort: string
      Schema: string option
      TimeoutSeconds: int }

let defaultTimeoutSeconds = 300
let maxTimeoutSeconds = 3600

let aliases = set [ "haiku"; "sonnet"; "opus" ]
let fullModelId = Regex(@"^claude-[a-z0-9][a-z0-9.\-]*$")
let efforts = set [ "low"; "medium"; "high"; "xhigh"; "max" ]

// System prompt and schema travel as command-line arguments; Windows caps the
// whole command line at 32767 chars. The prompt itself goes via stdin (no limit).
let maxArgChars = 30_000

let getStr (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | _ -> null

let compactJson (raw: string) =
    match JsonNode.Parse raw with
    | null -> Error "schema must not be null"
    | n when n.GetValueKind() <> JsonValueKind.Object -> Error "schema must be a JSON object"
    | n -> Ok(n.ToJsonString())

let parseRun (body: string) : Result<RunRequest, string> =
    try
        use doc = JsonDocument.Parse body
        let root = doc.RootElement
        if root.ValueKind <> JsonValueKind.Object then
            Error "body must be a JSON object"
        else
            let system, prompt = getStr root "system", getStr root "prompt"
            let model, effort = getStr root "model", getStr root "effort"
            let timeout =
                match root.TryGetProperty "timeoutSeconds" with
                | false, _ -> Ok defaultTimeoutSeconds
                | true, v when v.ValueKind = JsonValueKind.Null -> Ok defaultTimeoutSeconds
                | true, v when v.ValueKind = JsonValueKind.Number && v.TryGetInt32() |> fst ->
                    let t = v.GetInt32()
                    if t >= 1 && t <= maxTimeoutSeconds then Ok t
                    else Error $"timeoutSeconds must be 1 - {maxTimeoutSeconds} (got {t})"
                | true, _ -> Error "timeoutSeconds must be a whole number of seconds"
            let schema =
                match root.TryGetProperty "schema" with
                | false, _ -> Ok None
                | true, v when v.ValueKind = JsonValueKind.Null -> Ok None
                | true, v when v.ValueKind = JsonValueKind.Object -> compactJson (v.GetRawText()) |> Result.map Some
                | true, v when v.ValueKind = JsonValueKind.String ->
                    (try compactJson (v.GetString()) with ex -> Error $"schema is not valid JSON: {ex.Message}")
                    |> Result.map Some
                | true, _ -> Error "schema must be a JSON object (or a string containing one)"
            match schema with
            | Error e -> Error e
            | _ when Result.isError timeout -> Error(match timeout with Error e -> e | Ok _ -> "")
            | _ when String.IsNullOrWhiteSpace system -> Error "system is required"
            | _ when String.IsNullOrWhiteSpace prompt -> Error "prompt is required"
            | _ when isNull model || not (aliases.Contains model || fullModelId.IsMatch model) ->
                Error $"model must be haiku | sonnet | opus or a full id like claude-sonnet-5-5 (got '{model}')"
            | _ when isNull effort || not (efforts.Contains effort) ->
                Error $"""effort must be one of {String.Join(" | ", efforts)} (got '{effort}')"""
            | Ok(Some _) when not schemaSupported -> Error "this claude binary does not support --json-schema"
            | Ok s when system.Length + (defaultArg s "").Length > maxArgChars ->
                Error $"system + schema exceed {maxArgChars} chars (Windows command-line limit); move content into prompt"
            | Ok s ->
                Ok { System = system; Prompt = prompt; Model = model; Effort = effort; Schema = s
                     TimeoutSeconds = match timeout with Ok t -> t | Error _ -> defaultTimeoutSeconds }
    with ex ->
        Error $"invalid JSON body: {ex.Message}"

// --------------------------------------------------------------------------- //
// Running claude
// --------------------------------------------------------------------------- //
type RunOutcome =
    | Completed of JsonElement // claude's --output-format json object
    | Failed of status: int * message: string

let truncate (s: string) = if s.Length > 500 then s.Substring(0, 500) + "..." else s

let runClaude (r: RunRequest) : Task<RunOutcome> =
    task {
        let psi =
            ProcessStartInfo(cfg.ClaudeBin, UseShellExecute = false, CreateNoWindow = true,
                             RedirectStandardInput = true, RedirectStandardOutput = true,
                             RedirectStandardError = true, WorkingDirectory = cfg.WorkDir,
                             StandardInputEncoding = utf8, StandardOutputEncoding = utf8,
                             StandardErrorEncoding = utf8)
        [ "-p"
          "--output-format"; "json"
          "--exclude-dynamic-system-prompt-sections"
          "--tools"; "" // no tools: cheaper, and nothing can hang on a permission prompt
          // Keep the host's MCP servers (incl. claude.ai connectors) and skills out of
          // the context: with them a one-line call loaded ~110k tokens ($0.22 on haiku),
          // without them ~800 tokens ($0.001).
          "--strict-mcp-config"
          "--disable-slash-commands"
          "--no-session-persistence" // don't fill the host's session history with bridge runs
          "--system-prompt"; r.System
          "--model"; r.Model
          "--effort"; r.Effort ] // always explicit: ~/.claude/settings.json never leaks in
        |> List.iter psi.ArgumentList.Add
        r.Schema |> Option.iter (fun s -> psi.ArgumentList.Add "--json-schema"; psi.ArgumentList.Add s)

        use p = Process.Start psi
        // Start draining both pipes before writing stdin, so neither side can block.
        let stdout = p.StandardOutput.ReadToEndAsync()
        let stderr = p.StandardError.ReadToEndAsync()
        try
            do! p.StandardInput.WriteAsync r.Prompt
            p.StandardInput.Close()
        with :? IOException -> () // claude exited early; its stderr says why

        use cts = new CancellationTokenSource(TimeSpan.FromSeconds(float r.TimeoutSeconds))
        let! exited =
            task {
                try
                    do! p.WaitForExitAsync cts.Token
                    return true
                with :? OperationCanceledException ->
                    return false
            }

        if not exited then
            try p.Kill true with _ -> () // whole tree, so no orphaned node/claude children
            return Failed(504, $"claude timed out after {r.TimeoutSeconds}s")
        else
            let! out = stdout
            let! err = stderr
            let out = out.Trim()
            if out = "" then
                return Failed(502, $"claude exited {p.ExitCode}: {truncate (err.Trim())}")
            else
                try
                    use doc = JsonDocument.Parse out
                    let root = doc.RootElement.Clone()
                    match root.TryGetProperty "is_error" with
                    | true, v when v.ValueKind = JsonValueKind.True ->
                        let msg =
                            match root.TryGetProperty "result" with
                            | true, m -> m.ToString()
                            | _ -> out
                        return Failed(502, $"claude reported an error: {truncate msg}")
                    | _ -> return Completed root
                with :? JsonException ->
                    return Failed(502, $"claude returned non-JSON output (exit {p.ExitCode}): {truncate out}")
    }

// --------------------------------------------------------------------------- //
// HTTP
// --------------------------------------------------------------------------- //
let toNode (e: JsonElement) = JsonNode.Parse(e.GetRawText())

let prop (root: JsonElement) (name: string) =
    match root.TryGetProperty name with
    | true, v -> toNode v
    | _ -> null

let send (ctx: HttpListenerContext) (status: int) (body: JsonNode) =
    task {
        let bytes = utf8.GetBytes(body.ToJsonString())
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.ContentLength64 <- int64 bytes.Length
        do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
        ctx.Response.Close()
    }

let sendError ctx status (msg: string) =
    let o = JsonObject()
    o["error"] <- JsonValue.Create msg
    send ctx status o

let tokenOk (req: HttpListenerRequest) =
    match req.Headers["X-Bridge-Token"] with
    | null -> false
    | t -> CryptographicOperations.FixedTimeEquals(ReadOnlySpan(utf8.GetBytes t), ReadOnlySpan(utf8.GetBytes cfg.Token))

let health () =
    let o = JsonObject()
    o["status"] <- JsonValue.Create "ok"
    o["claudeVersion"] <- JsonValue.Create(Option.toObj claudeVersion)
    o["jsonSchema"] <- JsonValue.Create schemaSupported
    o["defaultTimeoutSeconds"] <- JsonValue.Create defaultTimeoutSeconds
    o["maxTimeoutSeconds"] <- JsonValue.Create maxTimeoutSeconds
    o["maxConcurrency"] <- JsonValue.Create cfg.MaxConcurrency
    o

let gate = new SemaphoreSlim(cfg.MaxConcurrency)
let maxBodyBytes = 20L * 1024L * 1024L

let handleRun (ctx: HttpListenerContext) =
    task {
        let req = ctx.Request
        if not (tokenOk req) then
            do! sendError ctx 401 "missing or invalid X-Bridge-Token"
        elif req.ContentLength64 > maxBodyBytes then
            do! sendError ctx 413 "request body too large"
        else
            use reader = new StreamReader(req.InputStream, utf8)
            let! body = reader.ReadToEndAsync()
            match parseRun body with
            | Error msg ->
                log "run rejected (400): %s" msg
                do! sendError ctx 400 msg
            | Ok r ->
                do! gate.WaitAsync()
                let sw = Stopwatch.StartNew()
                try
                    match! runClaude r with
                    | Failed(status, msg) ->
                        log "run failed (%d) model=%s effort=%s after %dms: %s" status r.Model r.Effort sw.ElapsedMilliseconds msg
                        do! sendError ctx status msg
                    | Completed root ->
                        let o = JsonObject()
                        o["result"] <- prop root "result"
                        o["structuredOutput"] <- prop root "structured_output"
                        o["usage"] <- prop root "usage"
                        o["costUsd"] <- prop root "total_cost_usd"
                        o["durationMs"] <- JsonValue.Create sw.ElapsedMilliseconds
                        let tokens =
                            match root.TryGetProperty "usage" with
                            | true, u ->
                                let n (k: string) =
                                    match u.TryGetProperty k with
                                    | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt64()
                                    | _ -> 0L
                                sprintf "%d in / %d out" (n "input_tokens") (n "output_tokens")
                            | _ -> "usage n/a"
                        log "run ok model=%s effort=%s schema=%b prompt=%d chars -> %dms, %s"
                            r.Model r.Effort r.Schema.IsSome r.Prompt.Length sw.ElapsedMilliseconds tokens
                        do! send ctx 200 o
                finally
                    gate.Release() |> ignore
    }

let handle (ctx: HttpListenerContext) : Task =
    task {
        try
            match ctx.Request.HttpMethod, ctx.Request.Url.AbsolutePath with
            | "GET", "/health" -> do! send ctx 200 (health ())
            | "POST", "/run" -> do! handleRun ctx
            | m, path -> do! sendError ctx 404 $"unknown endpoint {m} {path}"
        with ex ->
            log "unhandled error: %s" ex.Message
            try do! sendError ctx 500 ex.Message with _ -> ()
    }

// --------------------------------------------------------------------------- //
// Main
// --------------------------------------------------------------------------- //
// Listens on loopback only (loopback prefixes need no admin / urlacl). Requests
// from Docker arrive via host.docker.internal as 127.0.0.1. A loopback request
// with `Host: host.docker.internal:<port>` was accepted in testing; the backend
// still sends `Host: 127.0.0.1:<port>` so it never depends on that.
let listener = new HttpListener()
listener.Prefixes.Add $"http://127.0.0.1:{cfg.Port}/"
listener.Prefixes.Add $"http://localhost:{cfg.Port}/"

try listener.Start()
with ex ->
    log "claude-bridge cannot listen on port %d (already running?): %s" cfg.Port ex.Message
    exit 1

log "claude-bridge listening on http://127.0.0.1:%d" cfg.Port
log "  claude binary : %s (%s)" cfg.ClaudeBin (defaultArg claudeVersion "NOT FOUND")
log "  json-schema   : %s" (if schemaSupported then "supported" else "NOT supported")
log "  timeout       : per request, default %ds, max %ds; max concurrency: %d" defaultTimeoutSeconds maxTimeoutSeconds cfg.MaxConcurrency
log "  workdir       : %s" cfg.WorkDir

while listener.IsListening do
    let ctx = listener.GetContext()
    Task.Run(fun () -> handle ctx) |> ignore
