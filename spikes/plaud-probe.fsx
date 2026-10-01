// plaud-probe.fsx
// =============================================================================
// Throwaway spike: verifies the private Plaud API (as reverse-engineered by
// Riffado, see docs/riffado-plaud-sync-analysis.md) works with YOUR token, and
// captures real responses as fixtures for the backend's Plaud module.
//
//   dotnet fsi spikes/plaud-probe.fsx
//
// Token (the long-lived USER token, not the workspace token):
//   web.plaud.ai -> DevTools -> Application -> Local storage -> `pld_tokenstr`
//   (or cookie `pld_ut`). Provide it via PLAUD_TOKEN env var, or config.json in
//   the repo root: { "plaud": { "token": "..." } }  (config.json is gitignored).
//
// Optional env: PLAUD_API_BASE (skip region detection), PLAUD_FILE_ID (which
// recording to inspect; default = newest one with a Plaud transcript),
// PLAUD_REFRESH_ONLY=1 (only refresh the workspace token from the state saved
// by the last full run - for testing refresh after the tokens expired).
//
// Read-only: it never modifies anything in Plaud. Tokens are never printed;
// fixtures in spikes/fixtures/ (gitignored) have token fields and presigned URL
// signatures redacted.
// =============================================================================

open System
open System.Globalization
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

exception ProbeFailed of string

let fail fmt = Printf.kprintf (fun s -> raise (ProbeFailed s)) fmt

let fixturesDir = Path.Combine(__SOURCE_DIRECTORY__, "fixtures")

// A browser User-Agent is required, requests without it fail (Riffado issue #132).
let userAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

let http =
    new HttpClient(new HttpClientHandler(AutomaticDecompression = DecompressionMethods.All),
                   Timeout = TimeSpan.FromSeconds 60.)

http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent) |> ignore

// --------------------------------------------------------------------------- //
// Output helpers
// --------------------------------------------------------------------------- //
let step (n: int) (title: string) = printfn "\n[%d] %s" n title
let info fmt = Printf.kprintf (fun s -> printfn "    %s" s) fmt
let warn fmt = Printf.kprintf (fun s -> printfn "    WARNING: %s" s) fmt
let findings = ResizeArray<string>()
let finding fmt = Printf.kprintf (fun s -> findings.Add s; printfn "    => %s" s) fmt

// --------------------------------------------------------------------------- //
// JSON helpers (Plaud shapes are unstable - everything is optional)
// --------------------------------------------------------------------------- //
let rec get (n: JsonNode) (keys: string list) : JsonNode =
    match keys with
    | [] -> n
    | k :: rest ->
        match n with
        | :? JsonObject as o ->
            match o[k] with
            | null -> null
            | c -> get c rest
        | _ -> null

let str (n: JsonNode) =
    match n with
    | null -> ""
    | n when n.GetValueKind() = JsonValueKind.String -> n.GetValue<string>()
    | n -> n.ToJsonString()

let num (n: JsonNode) =
    match Double.TryParse(str n, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

let items (n: JsonNode) =
    match n with
    | :? JsonArray as a -> a |> Seq.filter (isNull >> not) |> List.ofSeq
    | _ -> []

let keysOf (n: JsonNode) =
    match n with
    | :? JsonObject as o -> o |> Seq.map (fun kv -> kv.Key) |> String.concat ", "
    | _ -> ""

let truncate (len: int) (s: string) = if s.Length > len then s.Substring(0, len) + "..." else s

let unixMs (n: JsonNode) =
    num n |> Option.map (fun ms -> DateTimeOffset.FromUnixTimeMilliseconds(int64 ms).ToLocalTime().ToString "yyyy-MM-dd HH:mm")

// --------------------------------------------------------------------------- //
// Fixtures (redacted copies of real responses)
// --------------------------------------------------------------------------- //
let stripSignature (url: string) =
    match url.IndexOf '?' with
    | -1 -> url
    | i -> url.Substring(0, i) + "?<signature-redacted>"

let rec redact (n: JsonNode) =
    match n with
    | :? JsonObject as o ->
        for kv in List.ofSeq o do
            match kv.Value with
            | null -> ()
            | v when kv.Key.ToLowerInvariant().Contains "token" -> o[kv.Key] <- JsonValue.Create "<redacted>"
            | v when v.GetValueKind() = JsonValueKind.String && (str v).StartsWith "http" && (str v).Contains "?" ->
                o[kv.Key] <- JsonValue.Create(stripSignature (str v))
            | v -> redact v
    | :? JsonArray as a -> for x in a do if not (isNull x) then redact x
    | _ -> ()

let jsonOut = JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let save (name: string) (n: JsonNode) =
    Directory.CreateDirectory fixturesDir |> ignore
    let copy = JsonNode.Parse(n.ToJsonString())
    redact copy
    File.WriteAllText(Path.Combine(fixturesDir, name), copy.ToJsonString jsonOut, UTF8Encoding false)
    info "saved spikes/fixtures/%s" name

let saveText (name: string) (text: string) =
    Directory.CreateDirectory fixturesDir |> ignore
    File.WriteAllText(Path.Combine(fixturesDir, name), text, UTF8Encoding false)
    info "saved spikes/fixtures/%s" name

// --------------------------------------------------------------------------- //
// Token
// --------------------------------------------------------------------------- //
let readToken () =
    let fromConfig () =
        let path = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "config.json"))
        if File.Exists path then
            let opts = JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)
            str (get (JsonNode.Parse(File.ReadAllText path, documentOptions = opts)) [ "plaud"; "token" ])
        else ""
    let raw =
        match Environment.GetEnvironmentVariable "PLAUD_TOKEN" with
        | null | "" -> fromConfig ()
        | t -> t
    let t = raw.Trim().Trim('"')
    let t = if t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then t.Substring 7 else t
    if t = "" then
        fail "No token. Set PLAUD_TOKEN, or create config.json in the repo root: { \"plaud\": { \"token\": \"...\" } }"
    t

let decodeClaims (jwt: string) =
    let parts = jwt.Split '.'
    if parts.Length <> 3 then
        fail "Token is not a JWT (expected 3 dot-separated parts, got %d; length %d). The user token starts with `eyJ` and is several hundred chars long - on web.plaud.ai run `localStorage.getItem('pld_tokenstr')` in the DevTools console, or copy the `pld_ut` cookie."
            parts.Length jwt.Length
    let b64 = parts[1].Replace('-', '+').Replace('_', '/')
    let padded = b64 + String('=', (4 - b64.Length % 4) % 4)
    match JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String padded)) with
    | :? JsonObject as o -> o
    | _ -> fail "JWT payload is not a JSON object"

let regionApiBase =
    function
    | "aws:us-west-2" -> Some "https://api.plaud.ai"
    | "aws:eu-central-1" -> Some "https://api-euc1.plaud.ai"
    | "aws:ap-southeast-1" -> Some "https://api-apse1.plaud.ai"
    | _ -> None

let isPlaudHost (url: string) =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, u ->
        u.Scheme = "https"
        && (u.Host = "plaud.ai" || u.Host.EndsWith ".plaud.ai" || u.Host = "plaud.cn" || u.Host.EndsWith ".plaud.cn")
    | _ -> false

// --------------------------------------------------------------------------- //
// Plaud API call: HTTP 200 + business `status` 0 = OK; body parsed as text first
// --------------------------------------------------------------------------- //
let plaud (apiBase: string) (bearer: string) (meth: HttpMethod) (path: string) (body: string option) : JsonNode =
    use req = new HttpRequestMessage(meth, apiBase + path)
    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer) |> ignore
    body |> Option.iter (fun b -> req.Content <- new StringContent(b, Encoding.UTF8, "application/json"))
    use resp = http.Send req
    let text = (new StreamReader(resp.Content.ReadAsStream())).ReadToEnd()
    let call = $"{meth.Method} {path.Split('?')[0]}"
    if resp.StatusCode = HttpStatusCode.Unauthorized then
        fail "%s -> 401: token rejected (expired, revoked, or wrong region)" call
    let node = try JsonNode.Parse text with _ -> null
    match node with
    | null -> fail "%s -> HTTP %d, non-JSON body: %s" call (int resp.StatusCode) (truncate 300 text)
    | _ when not resp.IsSuccessStatusCode -> fail "%s -> HTTP %d: %s" call (int resp.StatusCode) (truncate 300 text)
    | n when str (get n [ "status" ]) <> "0" ->
        fail "%s -> business status %s, msg: %s" call (str (get n [ "status" ])) (str (get n [ "msg" ]))
    | n -> n

// --------------------------------------------------------------------------- //
// Presigned links (no auth header)
// --------------------------------------------------------------------------- //
let queryParam (url: string) (name: string) =
    match url.IndexOf '?' with
    | -1 -> None
    | i ->
        url.Substring(i + 1).Split('&')
        |> Array.tryPick (fun kv ->
            match kv.Split('=', 2) with
            | [| k; v |] when k.Equals(name, StringComparison.OrdinalIgnoreCase) -> Some(Uri.UnescapeDataString v)
            | _ -> None)

let presignExpiry (url: string) =
    match queryParam url "X-Amz-Date", queryParam url "X-Amz-Expires" with
    | Some d, Some e ->
        match DateTime.TryParseExact(d, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal ||| DateTimeStyles.AssumeUniversal) with
        | true, signed -> Some(signed.AddSeconds(float e))
        | _ -> None
    | _ ->
        queryParam url "Expires"
        |> Option.bind (fun e ->
            match Int64.TryParse e with
            | true, s -> Some(DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime)
            | _ -> None)

let sniff (b: byte[]) =
    let ascii i n = if b.Length >= i + n then Encoding.ASCII.GetString(b, i, n) else ""
    if ascii 0 4 = "OggS" then
        if Encoding.ASCII.GetString(b).Contains "OpusHead" then "Ogg/Opus" else "Ogg (not Opus?)"
    elif ascii 0 3 = "ID3" || (b.Length >= 2 && b[0] = 0xFFuy && (b[1] &&& 0xE0uy) = 0xE0uy) then "MP3"
    elif ascii 0 4 = "RIFF" then "WAV"
    elif ascii 0 4 = "fLaC" then "FLAC"
    elif ascii 4 4 = "ftyp" then "MP4/M4A"
    elif b.Length >= 4 && b[0] = 0x1Auy && b[1] = 0x45uy && b[2] = 0xDFuy && b[3] = 0xA3uy then "WebM/Matroska"
    else sprintf "unknown (%s)" (BitConverter.ToString(b, 0, min 8 b.Length))

/// Reads only the first 128 bytes, so no full download happens.
let probeAudio (label: string) (url: string) =
    use req = new HttpRequestMessage(HttpMethod.Get, url)
    req.Headers.Range <- Headers.RangeHeaderValue(Nullable 0L, Nullable 127L)
    use resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead)
    if not resp.IsSuccessStatusCode then
        warn "%s: HTTP %d from %s" label (int resp.StatusCode) (Uri url).Host
    else
        let total =
            match resp.Content.Headers.ContentRange with
            | null -> Option.ofNullable resp.Content.Headers.ContentLength
            | cr -> Option.ofNullable cr.Length
        use s = resp.Content.ReadAsStream()
        let buf = Array.zeroCreate<byte> 128
        let mutable read = 0
        let mutable go = true
        while go && read < buf.Length do
            let n = s.Read(buf, read, buf.Length - read)
            if n = 0 then go <- false else read <- read + n
        let format = sniff buf[0 .. read - 1]
        let ext = Path.GetExtension((Uri url).AbsolutePath)
        let expiry =
            match presignExpiry url with
            | Some e -> sprintf "%s UTC (%.0f min from now)" (e.ToString "HH:mm") (e - DateTime.UtcNow).TotalMinutes
            | None -> "unknown"
        info "%s: host %s, extension '%s', bytes say %s, size %s, Range %s, expires %s"
            label (Uri url).Host ext format
            (total |> Option.map (fun t -> sprintf "%.1f MB" (float t / 1048576.)) |> Option.defaultValue "?")
            (if resp.StatusCode = HttpStatusCode.PartialContent then "supported" else "ignored")
            expiry
        finding "%s audio: %s in a '%s' file, URL valid ~%s" label format ext
            (presignExpiry url |> Option.map (fun e -> sprintf "%.0f min" (e - DateTime.UtcNow).TotalMinutes) |> Option.defaultValue "?")

let fetchLink (url: string) : JsonNode * string =
    use resp = http.Send(new HttpRequestMessage(HttpMethod.Get, url))
    if resp.StatusCode = HttpStatusCode.Forbidden then fail "content link -> 403 (expired presign)"
    if not resp.IsSuccessStatusCode then fail "content link -> HTTP %d" (int resp.StatusCode)
    use ms = new MemoryStream()
    resp.Content.ReadAsStream().CopyTo ms
    let mutable bytes = ms.ToArray()
    if bytes.Length >= 2 && bytes[0] = 0x1Fuy && bytes[1] = 0x8Buy then
        use gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)
        use out = new MemoryStream()
        gz.CopyTo out
        bytes <- out.ToArray()
    let text = Encoding.UTF8.GetString bytes
    (try JsonNode.Parse text with _ -> null), text

let describeShape (n: JsonNode) =
    match n with
    | null -> "not JSON"
    | :? JsonArray as a ->
        let first = items a |> List.tryHead
        sprintf "array of %d, first item keys: %s" a.Count (first |> Option.map keysOf |> Option.defaultValue "-")
    | :? JsonObject as o -> sprintf "object, keys: %s" (keysOf o)
    | v -> sprintf "%A value" (v.GetValueKind())

// --------------------------------------------------------------------------- //
// Refresh state (lets `PLAUD_REFRESH_ONLY=1` test refresh after tokens expire)
// --------------------------------------------------------------------------- //
// Holds a live 30-day refresh token - it is a secret, kept in the gitignored
// fixtures folder; delete it when done.
let refreshStatePath = Path.Combine(fixturesDir, ".refresh-state.json")

let saveRefreshState (apiBase: string) (workspaceId: string) (refreshToken: string) =
    Directory.CreateDirectory fixturesDir |> ignore
    let o = JsonObject()
    o["apiBase"] <- JsonValue.Create apiBase
    o["workspaceId"] <- JsonValue.Create workspaceId
    o["refreshToken"] <- JsonValue.Create refreshToken
    o["savedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString "o")
    File.WriteAllText(refreshStatePath, o.ToJsonString jsonOut, UTF8Encoding false)

let refreshOnly () =
    step 1 "Workspace token refresh from saved state (no user token)"
    if not (File.Exists refreshStatePath) then fail "No %s - run the full probe first" refreshStatePath
    let s = JsonNode.Parse(File.ReadAllText refreshStatePath)
    let apiBase, wsId, rt0 = str (get s [ "apiBase" ]), str (get s [ "workspaceId" ]), str (get s [ "refreshToken" ])
    info "state saved at %s" (str (get s [ "savedAt" ]))
    let r = plaud apiBase rt0 HttpMethod.Post $"/user-app/auth/workspace/refresh/{Uri.EscapeDataString wsId}" (Some "{}")
    save "workspace-refresh-later.json" r
    let wt, rt1 = str (get r [ "data"; "workspace_token" ]), str (get r [ "data"; "refresh_token" ])
    let at (k: string) =
        num (get r [ "data"; k ]) |> Option.map (fun v -> DateTimeOffset.FromUnixTimeSeconds(int64 v).ToString "yyyy-MM-dd HH:mm 'UTC'") |> Option.defaultValue "?"
    info "workspace token expires %s, refresh token expires %s, refresh token rotated %b" (at "wt_expires_at") (at "refresh_expires_at") (rt1 <> "" && rt1 <> rt0)
    if rt1 <> "" && rt1 <> rt0 then saveRefreshState apiBase wsId rt1
    step 2 "Data call with the refreshed workspace token"
    let list = plaud apiBase wt HttpMethod.Get "/file/simple/web?skip=0&limit=5&is_trash=0&sort_by=edit_time&is_desc=true" None
    info "recording list OK, data_file_total = %s" (str (get list [ "data_file_total" ]))
    finding "Refresh from saved state works: WT until %s, refresh token until %s (rotated: %b)" (at "wt_expires_at") (at "refresh_expires_at") (rt1 <> "" && rt1 <> rt0)

// --------------------------------------------------------------------------- //
// Probe
// --------------------------------------------------------------------------- //
let run () =
    step 1 "User token"
    let ut = readToken ()
    let claims = decodeClaims ut
    let isWorkspaceToken = not (isNull claims["ut_ref"]) || not (isNull claims["wid"])
    if isWorkspaceToken && Environment.GetEnvironmentVariable "PLAUD_ALLOW_WORKSPACE_TOKEN" <> "1" then
        fail "This is a WORKSPACE token (has ut_ref/wid claims) - it dies within ~24 h. Use cookie `pld_ut`, or the Authorization header of the `workspaces/list` / `workspace/token` request in the Network tab. (Set PLAUD_ALLOW_WORKSPACE_TOKEN=1 to probe the data calls with it anyway.)"
    if isWorkspaceToken then
        warn "WORKSPACE token accepted (PLAUD_ALLOW_WORKSPACE_TOKEN=1) - steps 2-3 (workspaces, minting) are skipped"
    if isNull claims["exp"] && isNull claims["region"] && not (isNull claims["email"]) then
        fail "This looks like the Frill SSO token (feedback widget; claims email/id/name only), not the Plaud user token. Run `localStorage.getItem('pld_tokenstr')` in the DevTools console on web.plaud.ai, or copy the `pld_ut` cookie."
    match num claims["exp"] with
    | Some exp ->
        let e = DateTimeOffset.FromUnixTimeSeconds(int64 exp)
        let days = (e - DateTimeOffset.UtcNow).TotalDays
        if days < 0. then fail "Token expired on %s - log in to web.plaud.ai again and copy a fresh one" (e.ToString "yyyy-MM-dd")
        info "token OK, expires %s (%.1f days left)" (e.ToString "yyyy-MM-dd HH:mm") days
    | None -> warn "token has no exp claim"
    let region = str claims["region"]
    let apiBase =
        match Environment.GetEnvironmentVariable "PLAUD_API_BASE" with
        | null | "" ->
            match regionApiBase region with
            | Some b -> b
            | None ->
                warn "unknown region claim '%s', using the global host" region
                "https://api.plaud.ai"
        | b -> b.TrimEnd '/'
    if not (isPlaudHost apiBase) then fail "API base %s is not an https plaud.ai / plaud.cn host" apiBase
    info "region '%s' -> %s" region apiBase

    let mintWorkspaceToken () =
        step 2 "Workspaces (user token)"
        let ws = plaud apiBase ut HttpMethod.Get "/team-app/workspaces/list?need_personal_workspace=true" None
        save "workspaces.json" ws
        let workspaces = items (get ws [ "data"; "workspaces" ])
        for w in workspaces do
            info "workspace %s, type %s, role %s" (str (get w [ "workspace_id" ])) (str (get w [ "workspace_type" ])) (str (get w [ "role" ]))
        let workspaceId =
            workspaces
            |> List.tryFind (fun w -> str (get w [ "workspace_type" ]) = "0")
            |> Option.orElse (List.tryHead workspaces)
            |> Option.map (fun w -> str (get w [ "workspace_id" ]))
        if workspaceId.IsNone then fail "account has no workspaces"

        step 3 "Workspace token (mint)"
        try
            let r = plaud apiBase ut HttpMethod.Post $"/user-app/auth/workspace/token/{Uri.EscapeDataString workspaceId.Value}" (Some "{}")
            save "workspace-token.json" r
            let wt = str (get r [ "data"; "workspace_token" ])
            if wt = "" then fail "mint response has no data.workspace_token"
            info "minted workspace token for %s, expires_in %s" workspaceId.Value (str (get r [ "data"; "expires_in" ]))
            finding "Workspace token minting works"
            let rt0 = str (get r [ "data"; "refresh_token" ])
            if rt0 <> "" then saveRefreshState apiBase workspaceId.Value rt0
            if rt0 = "" then
                finding "Mint response has NO refresh_token"
                wt
            else
                // How web.plaud.ai keeps a workspace session alive without the user token
                // (found in its JS bundle): POST /user-app/auth/workspace/refresh/{wsId},
                // Authorization: Bearer <workspace refresh token>.
                step 31 "Workspace token refresh (refresh_token, no user token)"
                info "refresh_expires_in %s s (%.0f days)" (str (get r [ "data"; "refresh_expires_in" ]))
                    ((num (get r [ "data"; "refresh_expires_in" ]) |> Option.defaultValue 0.) / 86400.)
                let refresh (rt: string) =
                    plaud apiBase rt HttpMethod.Post $"/user-app/auth/workspace/refresh/{Uri.EscapeDataString workspaceId.Value}" (Some "{}")
                try
                    let r1 = refresh rt0
                    save "workspace-refresh.json" r1
                    let wt1 = str (get r1 [ "data"; "workspace_token" ])
                    let rt1 = str (get r1 [ "data"; "refresh_token" ])
                    info "refresh OK: new workspace token %b, expires_in %s, refresh token rotated %b, new refresh_expires_in %s"
                        (wt1 <> "" && wt1 <> wt) (str (get r1 [ "data"; "expires_in" ])) (rt1 <> "" && rt1 <> rt0)
                        (str (get r1 [ "data"; "refresh_expires_in" ]))
                    finding "Workspace token refresh works (refresh token rotated: %b, data keys: %s)" (rt1 <> "" && rt1 <> rt0) (keysOf (get r1 [ "data" ]))
                    if rt1 <> "" && rt1 <> rt0 then
                        try
                            refresh rt0 |> ignore
                            finding "OLD refresh token still works after rotation"
                        with ProbeFailed msg ->
                            finding "OLD refresh token is invalidated after rotation (%s) - always persist the new one" msg
                    if wt1 <> "" then
                        info "using the REFRESHED workspace token for all data calls below"
                        wt1
                    else wt
                with ProbeFailed msg ->
                    finding "Workspace token refresh FAILED (%s)" msg
                    wt
        with ProbeFailed msg ->
            warn "%s - falling back to the user token for data calls (regional servers may return empty lists)" msg
            finding "Workspace token minting FAILED (%s)" msg
            ut

    let bearer = if isWorkspaceToken then ut else mintWorkspaceToken ()

    step 4 "Devices"
    let dev = plaud apiBase bearer HttpMethod.Get "/device/list" None
    save "device-list.json" dev
    let devices = items (get dev [ "data_devices" ])
    for d in devices do
        info "device %s (%s), sn %s, firmware %s" (str (get d [ "name" ])) (str (get d [ "model" ])) (str (get d [ "sn" ])) (str (get d [ "version_number" ]))
    if devices.IsEmpty then warn "no devices returned"

    step 5 "Recordings (newest 50, sorted by edit_time desc)"
    let list = plaud apiBase bearer HttpMethod.Get "/file/simple/web?skip=0&limit=50&is_trash=0&sort_by=edit_time&is_desc=true" None
    save "file-list.json" list
    let files = items (get list [ "data_file_list" ])
    info "data_file_total = %s, page has %d" (str (get list [ "data_file_total" ])) files.Length
    for f in List.truncate 10 files do
        info "%s  %5.1f min  trans=%-5s sum=%-5s  %s  [%s]"
            (unixMs (get f [ "start_time" ]) |> Option.defaultValue "?")
            ((num (get f [ "duration" ]) |> Option.defaultValue 0.) / 60000.)
            (str (get f [ "is_trans" ])) (str (get f [ "is_summary" ]))
            (truncate 40 (str (get f [ "filename" ]))) (str (get f [ "id" ]))
    match List.tryHead files with
    | Some f ->
        info "fields on a recording: %s" (keysOf f)
        let expected = [ "id"; "filename"; "filesize"; "file_md5"; "start_time"; "end_time"; "duration"; "version_ms"; "edit_time"; "is_trash"; "is_trans"; "is_summary"; "serial_number" ]
        let missing = expected |> List.filter (fun k -> isNull (get f [ k ]))
        if missing.IsEmpty then finding "Recording list shape matches the analysis (incl. version_ms)"
        else finding "Recording list is MISSING expected fields: %s" (String.Join(", ", missing))
    | None -> fail "no recordings returned - nothing more to probe (wrong region/workspace, or empty account)"

    let target =
        match Environment.GetEnvironmentVariable "PLAUD_FILE_ID" with
        | null | "" ->
            files
            |> List.tryFind (fun f -> str (get f [ "is_trans" ]) = "true")
            |> Option.defaultValue (List.head files)
        | id ->
            match files |> List.tryFind (fun f -> str (get f [ "id" ]) = id) with
            | Some f -> f
            | None -> fail "PLAUD_FILE_ID %s is not among the newest 50 recordings" id
    let fileId = str (get target [ "id" ])
    info "inspecting %s (%s)" fileId (truncate 40 (str (get target [ "filename" ])))

    step 6 "Audio URL (temp-url) + format sniff"
    for isOpus in [ 0; 1 ] do
        let t = plaud apiBase bearer HttpMethod.Get $"/file/temp-url/{fileId}?is_opus={isOpus}" None
        save $"temp-url-opus{isOpus}.json" t
        for key in [ "temp_url"; "temp_url_opus" ] do
            match str (get t [ key ]) with
            | "" -> info "is_opus=%d: no %s" isOpus key
            | url -> probeAudio $"is_opus={isOpus} {key}" url

    step 7 "Plaud content (file/detail)"
    let detail = plaud apiBase bearer HttpMethod.Get $"/file/detail/{fileId}" None
    save "file-detail.json" detail
    let contentList = items (get detail [ "data"; "content_list" ])
    let inline' =
        items (get detail [ "data"; "pre_download_content_list" ])
        |> List.map (fun i -> str (get i [ "data_id" ]), str (get i [ "data_content" ]))
        |> Map.ofList
    for c in contentList do
        let dataId = str (get c [ "data_id" ])
        info "item type=%-16s id=%-22s task_status=%s link=%b inline=%b"
            (str (get c [ "data_type" ])) (truncate 22 (dataId.Split(':')[0] + ":..."))
            (str (get c [ "task_status" ])) (str (get c [ "data_link" ]) <> "") (inline'.ContainsKey dataId)
    if contentList.IsEmpty then warn "content_list is empty (no Plaud transcript/summary for this file)"

    let isKind (c: JsonNode) (types: string list) (prefixes: string list) =
        let t, id = str (get c [ "data_type" ]), str (get c [ "data_id" ])
        List.contains t types || prefixes |> List.exists (fun p -> id.StartsWith p)
    let ready (c: JsonNode) = str (get c [ "task_status" ]) = "1" && str (get c [ "data_link" ]) <> ""

    let body (c: JsonNode) =
        let dataId = str (get c [ "data_id" ])
        match inline'.TryFind dataId with
        | Some text when text <> "" -> (try JsonNode.Parse text with _ -> null), text, "inline"
        | _ ->
            let n, text = fetchLink (str (get c [ "data_link" ]))
            n, text, "link"

    match contentList |> List.tryFind (fun c -> isKind c [ "transaction"; "transcript" ] [ "source_transaction" ] && ready c) with
    | Some c ->
        let n, text, src = body c
        if isNull n then saveText "transcript.txt" text else save "transcript.json" n
        let segments =
            match n with
            | :? JsonArray -> n
            | :? JsonObject -> [ "segments"; "transcript"; "data" ] |> List.map (fun k -> get n [ k ]) |> List.tryFind (fun x -> x :? JsonArray) |> Option.toObj
            | _ -> null
        info "transcript (%s): %s" src (describeShape n)
        if not (isNull segments) && not (obj.ReferenceEquals(segments, n)) then info "segments: %s" (describeShape segments)
        finding "Plaud transcript shape: %s" (describeShape (if isNull segments then n else segments))
    | None -> finding "No ready Plaud transcript on this recording"

    let summaries =
        contentList |> List.filter (fun c -> isKind c [ "auto_sum_note"; "sum_multi_note" ] [ "auto_sum"; "sum_multi" ] && ready c)
    match summaries |> List.sortBy (fun c -> if isKind c [ "auto_sum_note" ] [ "auto_sum" ] then 0 else 1) |> List.tryHead with
    | Some c ->
        let n, text, src = body c
        if isNull n then saveText "summary.txt" text else save "summary.json" n
        info "summary (%s): %s" src (describeShape n)
    | None -> info "no ready Plaud summary on this recording"

// --------------------------------------------------------------------------- //
try
    if Environment.GetEnvironmentVariable "PLAUD_REFRESH_ONLY" = "1" then refreshOnly () else run ()
    printfn "\nFINDINGS"
    for f in findings do printfn "  - %s" f
    printfn "\nFixtures: %s (gitignored - contains your private recording data)" fixturesDir
with
| ProbeFailed msg ->
    printfn "\nFAILED: %s" msg
    exit 1
| ex ->
    printfn "\nFAILED (unexpected): %s" ex.Message
    exit 1
