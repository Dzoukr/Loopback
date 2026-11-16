/// Client for Plaud's private API (no public API exists - reverse-engineered, see
/// docs/riffado-plaud-sync-analysis.md, sections 7 and 8 for what was verified live).
/// Recordings use the Plaud 4.0 `/file-app/v4/...` API (since 2026-11-15; the 3.x `/file/...`
/// endpoints answer status -1800907 "Account migrated to 4.0" for migrated accounts).
/// Every response shape is treated as unstable and parsed defensively.
module Loopback.Server.Integrations.Plaud

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Org.BouncyCastle.Asn1.X9
open Org.BouncyCastle.Crypto.Engines
open Org.BouncyCastle.Crypto.Modes
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Security

/// 401 from Plaud: the token is expired, revoked or for another region.
exception PlaudUnauthorized of string
/// Business status -419 (workspace token expired) / -420 (session invalid, re-exchange
/// required): Plaud revoked the workspace session. Recover by refreshing or minting a new
/// workspace token.
exception PlaudSessionExpired of string
/// Any other Plaud failure (HTTP error, business status <> 0, unexpected body).
exception PlaudError of string
/// The password login was refused (wrong credentials, two-factor code required, throttled, ...) -
/// retrying right away will not help.
exception PlaudLoginFailed of string

// A browser User-Agent is required, requests without it fail.
let private userAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

/// Host for the password login; Plaud answers status -302 with the account's regional host.
let private globalApi = "https://api.plaud.ai"

// ---------------------------------------------------------------------------
// Defensive JSON access
// ---------------------------------------------------------------------------
let rec private get (n: JsonNode) (keys: string list) : JsonNode =
    match keys, n with
    | [], _ -> n
    | k :: rest, (:? JsonObject as o) ->
        match o[k] with
        | null -> null
        | c -> get c rest
    | _ -> null

let private str (n: JsonNode) =
    match n with
    | null -> ""
    | n when n.GetValueKind() = JsonValueKind.String -> n.GetValue<string>()
    | n -> n.ToJsonString()

let private int64' (n: JsonNode) =
    match Double.TryParse(str n, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
    | true, v -> int64 v
    | _ -> 0L

let private items (n: JsonNode) =
    match n with
    | :? JsonArray as a -> a |> Seq.filter (isNull >> not) |> List.ofSeq
    | _ -> []

// ---------------------------------------------------------------------------
// Login helpers
// ---------------------------------------------------------------------------
let private regionApiBase =
    function
    | "aws:us-west-2" -> Some "https://api.plaud.ai"
    | "aws:eu-central-1" -> Some "https://api-euc1.plaud.ai"
    | "aws:ap-southeast-1" -> Some "https://api-apse1.plaud.ai"
    | _ -> None

/// `region` claim of a JWT (decoded, not verified).
let private regionClaim (jwt: string) =
    try
        let payload = (jwt.Split '.')[1]
        let b64 = payload.Replace('-', '+').Replace('_', '/')
        let padded = b64 + String('=', (4 - b64.Length % 4) % 4)
        let claims = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String padded))
        str (get claims [ "region" ])
    with _ -> ""

let private isPlaudHost (url: string) =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, u -> u.Scheme = "https" && (u.Host = "plaud.ai" || u.Host.EndsWith ".plaud.ai")
    | _ -> false

/// The regional host from a -302 "domain switch" answer.
let private domainSwitch (r: JsonNode) =
    [ get r [ "data"; "domains"; "api" ]; get r [ "domains"; "api" ]; get r [ "data"; "domain" ] ]
    |> List.map str
    |> List.tryFind (fun d -> d <> "" && isPlaudHost d)
    |> Option.map _.TrimEnd('/')

/// The password as web.plaud.ai sends it: base64(ECIES(pubKey, {"pass","time"})), compatible
/// with eciesjs defaults - secp256k1, uncompressed ephemeral key, HKDF-SHA256 over
/// ephemeralPk || sharedPoint (both uncompressed), AES-256-GCM with a 16-byte nonce, output
/// ephemeralPk || nonce || tag || ciphertext. BouncyCastle, because .NET exposes neither the
/// full ECDH shared point nor 16-byte GCM nonces.
let private encryptPassword (pubKeyHex: string) (password: string) =
    let payload = JsonObject()
    payload["pass"] <- JsonValue.Create password
    payload["time"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
    let plaintext = Encoding.UTF8.GetBytes(payload.ToJsonString())
    let curve = ECNamedCurveTable.GetByName "secp256k1"
    let receiver = curve.Curve.DecodePoint(Convert.FromHexString pubKeyHex)
    let rng = SecureRandom()
    let mutable d = Org.BouncyCastle.Math.BigInteger.Zero
    while d.SignValue <= 0 || d.CompareTo curve.N >= 0 do
        d <- Org.BouncyCastle.Math.BigInteger(256, rng)
    let ephemeralPk = curve.G.Multiply(d).Normalize().GetEncoded false
    let shared = receiver.Multiply(d).Normalize().GetEncoded false
    let key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Array.append ephemeralPk shared, 32, Array.empty, Array.empty)
    let nonce = RandomNumberGenerator.GetBytes 16
    let gcm = GcmBlockCipher(AesEngine())
    gcm.Init(true, AeadParameters(KeyParameter key, 128, nonce))
    let out = Array.zeroCreate<byte> (gcm.GetOutputSize plaintext.Length)
    let n = gcm.ProcessBytes(plaintext, 0, plaintext.Length, out, 0)
    gcm.DoFinal(out, n) |> ignore
    // BouncyCastle appends the tag; eciesjs puts it in front of the ciphertext.
    Convert.ToBase64String(Array.concat [ ephemeralPk; nonce; out[out.Length - 16 ..]; out[.. out.Length - 17] ])

// ---------------------------------------------------------------------------
// Response types
// ---------------------------------------------------------------------------
/// Loopback's own Plaud login session (not shared with any browser).
type UserSession = {
    ApiBase : string
    /// 24 h user token: mints workspace tokens.
    UserToken : string
    UserTokenExpiresAt : DateTimeOffset
    /// Value of the HttpOnly `pld_urt` cookie (30 days): renews the user token without the password.
    RefreshToken : string
    RefreshExpiresAt : DateTimeOffset
}

type WorkspaceSession = {
    WorkspaceId : string
    WorkspaceToken : string
    WorkspaceTokenExpiresAt : DateTimeOffset
    /// Empty if Plaud returned none.
    RefreshToken : string
    RefreshExpiresAt : DateTimeOffset
}

/// Plaud 4.0 (2026-11) gave every recording a new id: `of_<old id>` for recordings migrated
/// from 3.x, `f_<id>` for new ones. Loopback keeps the old ids of migrated recordings (its rows,
/// audio and attachment folders use them), so the `of_` prefix is dropped on the way in and
/// added back on the way out.
let private migratedPrefix = "of_"

/// Loopback's id of a Plaud 4.0 file id.
let localId (plaudId: string) =
    if plaudId.StartsWith migratedPrefix then plaudId.Substring migratedPrefix.Length else plaudId

/// Plaud 4.0 file id of a Loopback recording id.
let plaudId (localId: string) =
    if localId.StartsWith "f_" || localId.StartsWith migratedPrefix then localId else migratedPrefix + localId

/// A recording as listed by `/file-app/v4/recordings/all` (ids already Loopback's, see `localId`).
type PlaudFile = {
    Id : string
    Filename : string
    StartTime : DateTimeOffset
    DurationMs : int64
    VersionMs : string
}

/// What only `/file-app/v4/files/detail` tells about a recording.
type PlaudFileDetail = {
    /// Size of the Opus audio.
    Filesize : int64
    /// Local UTC offset at the recording start (from `timezone_iana`); None if Plaud has no time zone.
    UtcOffsetMinutes : int option
    /// The recording has marks made during the call (a non-empty `MARK_MEMO` object, see GetMarks).
    HasMarks : bool
}

/// `mark_type` of a Plaud mark (docs/attachments.md, Plaud Marks).
type PlaudMarkKind =
    /// Plaud 3.x app voice memo - an Ogg file (transcribed, not kept), `Content` is Plaud's transcript of it.
    | VoiceMemo
    /// Plaud app photo - an image file, `Content` is Plaud's AI description.
    | Photo
    /// Plaud app text note - `Content` is the user's text.
    | TextNote
    /// Device button press (`mark_type` 4) - `Content` is Plaud's AI summary of the moment, filled in later.
    /// Also the Plaud 4.0 app's flag (`mark_type` 1, `USER_FLAG` in web.plaud.ai): only a time, no file or text.
    | Highlight
    | UnknownMark of int

type PlaudMark = {
    /// `mark_id`, or type:timestamp:hash(content) for marks without one.
    Key : string
    Kind : PlaudMarkKind
    /// From the recording start.
    OffsetMs : int64
    /// Plaud's text; None while it is still analyzing (or empty).
    Content : string option
    /// Plaud is still analyzing it (`mark_status` other than 3) - the text comes later.
    Pending : bool
    /// Deleted by the user in Plaud (`is_user_deleted`).
    Deleted : bool
    /// Storage path of the file (`picture_link`), e.g. permanent/.../mark/20261009_114810_095861eb.png.
    FilePath : string option
    /// Presigned download URL of the file (valid ~15 minutes).
    FileUrl : string option
}

/// Marks of a recording from the content of its `MARK_MEMO` object; `urls` maps a mark's
/// `picture_link` to its presigned URL (`relation_content_mapping` of the detail, as web.plaud.ai does).
/// Handles both the Plaud app's format and the device's (with `mark_status`, `is_user_deleted`, ...).
let parseMarks (urls: Map<string, string>) (content: string option) : PlaudMark list =
    let marks =
        match content with
        | Some json -> (try JsonNode.Parse json with _ -> null)
        | None -> null
    items marks
    |> List.map (fun m ->
        let typeNumber = int (int64' (get m [ "mark_type" ]))
        let text = (str (get m [ "mark_content" ])).Trim()
        let filePath = match str (get m [ "picture_link" ]) with "" -> None | p -> Some p
        let kind =
            match typeNumber with
            // Plaud 4.0 reuses 1 for the app's flag button - a bare time, nothing to transcribe.
            | 1 when filePath.IsNone && text = "" && str (get m [ "picture_full_url" ]) = "" -> Highlight
            | 1 -> VoiceMemo
            | 2 -> Photo
            | 3 -> TextNote
            | 4 -> Highlight
            | n -> UnknownMark n
        let offset = int64' (get m [ "timestamp" ])
        // The app's format has no mark_status - its marks are complete.
        let pending =
            match get m [ "mark_status" ] with
            | null -> false
            | status -> int64' status <> 3L
        let key =
            match str (get m [ "mark_id" ]) with
            | "" ->
                let hash = Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes text)).Substring(0, 12)
                $"{typeNumber}:{offset}:{hash}"
            | id -> id
        {
            Key = key
            Kind = kind
            OffsetMs = offset
            Content = if pending || text = "" then None else Some text
            Pending = pending
            Deleted = str (get m [ "is_user_deleted" ]) = "true"
            FilePath = filePath
            FileUrl =
                filePath
                |> Option.bind (fun p -> urls.TryFind p)
                |> Option.orElse (match str (get m [ "picture_full_url" ]) with "" -> None | u -> Some u)
        })

/// Login / user-token refresh answer: tokens at the top level or under `data`, expiries as
/// unix seconds (`*_expire_at`) or seconds from now (`*_expires_in`), the refresh token in
/// the `pld_urt` cookie (the body field may be empty).
let private parseUserSession (apiBase: string) (r: JsonNode) (setCookies: string list) (previousRefresh: string) =
    let field (k: string) =
        match str (get r [ k ]) with
        | "" -> str (get r [ "data"; k ])
        | v -> v
    let at (expireAt: string) (expiresIn: string) =
        let seconds (k: string) =
            match Int64.TryParse(field k) with
            | true, v -> v
            | _ -> 0L
        match seconds expireAt, seconds expiresIn with
        | a, _ when a > 0L -> DateTimeOffset.FromUnixTimeSeconds a
        | _, i when i > 0L -> DateTimeOffset.UtcNow.AddSeconds(float i)
        | _ -> DateTimeOffset.UtcNow
    // Plaud first clears stale cookies (empty value, Max-Age=0) for several domains/paths, then
    // sets the real one - take the last non-empty value.
    let cookie (name: string) =
        setCookies
        |> List.choose (fun c ->
            let c = c.Trim()
            if c.StartsWith(name + "=") then Some((c.Substring(name.Length + 1).Split ';')[0]) else None)
        |> List.filter (fun v -> v <> "")
        |> List.tryLast
    // The user token comes in the body or (since 2026-10) only as the `pld_ut` cookie.
    match (match field "access_token" with "" -> cookie "pld_ut" | t -> Some t) with
    | None -> raise (PlaudError "Plaud login answer has no access_token (body) nor pld_ut (cookie)")
    | Some ut ->
        {
            ApiBase = regionApiBase (regionClaim ut) |> Option.defaultValue apiBase
            UserToken = ut
            UserTokenExpiresAt = at "ut_expire_at" "ut_expires_in"
            RefreshToken =
                cookie "pld_urt"
                |> Option.orElse (match field "refresh_token" with "" -> None | t -> Some t)
                |> Option.defaultValue previousRefresh
            RefreshExpiresAt = at "urt_expire_at" "urt_expires_in"
        }

let private parseSession (workspaceId: string) (r: JsonNode) =
    let d = get r [ "data" ]
    let at (seconds: int64) (fallback: TimeSpan) =
        if seconds > 0L then DateTimeOffset.FromUnixTimeSeconds seconds else DateTimeOffset.UtcNow.Add fallback
    match str (get d [ "workspace_token" ]) with
    | "" -> raise (PlaudError "workspace token response has no data.workspace_token")
    | wt ->
        {
            WorkspaceId = match str (get d [ "workspace_id" ]) with "" -> workspaceId | id -> id
            WorkspaceToken = wt
            WorkspaceTokenExpiresAt = at (int64' (get d [ "wt_expires_at" ])) (TimeSpan.FromSeconds(float (max 60L (int64' (get d [ "expires_in" ])))))
            RefreshToken = str (get d [ "refresh_token" ])
            RefreshExpiresAt = at (int64' (get d [ "refresh_expires_at" ])) (TimeSpan.FromSeconds(float (int64' (get d [ "refresh_expires_in" ]))))
        }

// ---------------------------------------------------------------------------
// Client
// ---------------------------------------------------------------------------
type PlaudClient(http: HttpClient) =

    /// HTTP 200 + business `status` 0 = OK. Bodies are parsed as text first (errors may be non-JSON).
    let call (apiBase: string) (bearer: string) (meth: HttpMethod) (path: string) (body: string option) =
        task {
            use req = new HttpRequestMessage(meth, apiBase + path)
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer) |> ignore
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
            body |> Option.iter (fun b -> req.Content <- new StringContent(b, Encoding.UTF8, "application/json"))
            use! resp = http.SendAsync req
            let! text = resp.Content.ReadAsStringAsync()
            let name = $"{meth.Method} {path.Split('?')[0]}"
            if resp.StatusCode = HttpStatusCode.Unauthorized then
                raise (PlaudUnauthorized $"{name} -> 401 (token expired, revoked or wrong region)")
            let node = try JsonNode.Parse text with _ -> null
            if isNull node then
                raise (PlaudError $"{name} -> HTTP {int resp.StatusCode}, non-JSON body")
            let msg, status = str (get node [ "msg" ]), str (get node [ "status" ])
            if not resp.IsSuccessStatusCode then
                raise (PlaudError $"{name} -> HTTP {int resp.StatusCode}: {msg}")
            if status = "-419" || status = "-420" then
                raise (PlaudSessionExpired $"{name} -> status {status}: {msg}")
            if status <> "0" then
                raise (PlaudError $"{name} -> status {status}: {msg}")
            return node
        }

    /// Login endpoints: no bearer, browser origin; returns the body whatever its business status,
    /// plus the Set-Cookie headers. `content` is a factory - a -302 retry needs a fresh body.
    let authCall (meth: HttpMethod) (url: string) (content: (unit -> HttpContent) option) (cookie: string option) =
        task {
            use req = new HttpRequestMessage(meth, url)
            for k, v in [ "User-Agent", userAgent; "Origin", "https://web.plaud.ai"; "Referer", "https://web.plaud.ai/"; "app-platform", "web" ] do
                req.Headers.TryAddWithoutValidation(k, v) |> ignore
            cookie |> Option.iter (fun c -> req.Headers.TryAddWithoutValidation("Cookie", c) |> ignore)
            content |> Option.iter (fun c -> req.Content <- c ())
            use! resp = http.SendAsync req
            let! text = resp.Content.ReadAsStringAsync()
            let node = try JsonNode.Parse text with _ -> null
            if isNull node then
                raise (PlaudError $"{meth.Method} {Uri(url).AbsolutePath} -> HTTP {int resp.StatusCode}, non-JSON body")
            let setCookies =
                match resp.Headers.TryGetValues "Set-Cookie" with
                | true, v -> List.ofSeq v
                | _ -> []
            return node, setCookies
        }

    let emptyJson () = new StringContent("{}", Encoding.UTF8, "application/json") :> HttpContent

    /// `/file-app/v4/files/detail` of a recording (Loopback id): metadata plus presigned URLs
    /// (~60 minutes) of its objects (AUDIO, MARK_MEMO, TRANSCRIPT, ...).
    let detail (apiBase: string) (workspaceToken: string) (id: string) =
        call apiBase workspaceToken HttpMethod.Get $"/file-app/v4/files/detail/{Uri.EscapeDataString(plaudId id)}" None

    /// The detail's object of `objectType` that has a download URL.
    let detailObject (objectType: string) (d: JsonNode) =
        items (get d [ "data"; "objects" ])
        |> List.tryFind (fun o -> str (get o [ "object_type" ]) = objectType && (str (get o [ "content_url" ])).StartsWith "https://")

    /// One page of a cursor-paged v4 listing: the items and the next cursor (None after the last page).
    let page (apiBase: string) (workspaceToken: string) (path: string) (cursor: string option) =
        task {
            let sep = if path.Contains '?' then "&" else "?"
            let cursorParam = cursor |> Option.map (fun c -> "&cursor=" + Uri.EscapeDataString c) |> Option.defaultValue ""
            let! r = call apiBase workspaceToken HttpMethod.Get $"{path}{sep}page_size=50{cursorParam}" None
            let next = match str (get r [ "data"; "next_cursor" ]) with "" -> None | c -> Some c
            return items (get r [ "data"; "items" ]), next
        }

    /// Password login, as web.plaud.ai does it. Every call creates a new login session, and
    /// Plaud throttles logins per hour - prefer RefreshUserToken.
    member _.Login(email: string, password: string) : Threading.Tasks.Task<UserSession> =
        let rec attempt (apiBase: string) (hops: int) =
            task {
                let! sec, _ = authCall HttpMethod.Get (apiBase + "/config/security") None None
                let pubKey, algorithm = str (get sec [ "data"; "pass_pub_key" ]), str (get sec [ "data"; "pass_algorithm" ])
                if algorithm <> "secp256k1" || pubKey = "" then
                    raise (PlaudError $"Plaud changed its password encryption (algorithm '{algorithm}') - the login needs updating")
                let encrypted = encryptPassword pubKey password
                let form () =
                    let f = new MultipartFormDataContent()
                    f.Add(new StringContent(email), "username")
                    f.Add(new StringContent(encrypted), "password")
                    f.Add(new StringContent("web"), "client_id")
                    f.Add(new StringContent("true"), "password_encrypted")
                    f :> HttpContent
                let! r, cookies = authCall HttpMethod.Post (apiBase + "/auth/access-token") (Some form) None
                match str (get r [ "status" ]), domainSwitch r with
                | "-302", Some d when hops < 2 -> return! attempt d (hops + 1)
                | "0", _ -> return parseUserSession apiBase r cookies ""
                | "10001", _ ->
                    return raise (PlaudLoginFailed "Plaud asks for a two-factor code - not supported, turn two-factor auth off for this account")
                | "-3", _ -> return raise (PlaudLoginFailed "Plaud rejected the email or password (PLAUD_EMAIL / PLAUD_PASSWORD in .env)")
                | s, _ ->
                    let msg = str (get r [ "msg" ])
                    return raise (PlaudLoginFailed $"Plaud refused the login (status {s}: {msg})")
            }
        attempt globalApi 0

    /// Renews the user token with the `pld_urt` refresh cookie - no password, same login session.
    member _.RefreshUserToken(apiBase: string, refreshToken: string) : Threading.Tasks.Task<UserSession> =
        let rec attempt (apiBase: string) (hops: int) =
            task {
                let! r, cookies = authCall HttpMethod.Post (apiBase + "/auth/refresh-user-token") (Some emptyJson) (Some $"pld_urt={refreshToken}")
                match str (get r [ "status" ]), domainSwitch r with
                | "-302", Some d when hops < 1 -> return! attempt d (hops + 1)
                | "0", _ -> return parseUserSession apiBase r cookies refreshToken
                | s, _ ->
                    let msg = str (get r [ "msg" ])
                    return raise (PlaudUnauthorized $"user token refresh -> status {s}: {msg}")
            }
        attempt apiBase 0

    /// Personal workspace (type "0"), else the first one. Auth: user token.
    member _.GetPersonalWorkspaceId(apiBase: string, userToken: string) =
        task {
            let! r = call apiBase userToken HttpMethod.Get "/team-app/workspaces/list?need_personal_workspace=true" None
            let workspaces = items (get r [ "data"; "workspaces" ])
            return
                workspaces
                |> List.tryFind (fun w -> str (get w [ "workspace_type" ]) = "0")
                |> Option.orElse (List.tryHead workspaces)
                |> Option.map (fun w -> str (get w [ "workspace_id" ]))
                |> Option.defaultWith (fun () -> raise (PlaudError "Plaud account has no workspaces"))
        }

    /// Mints a workspace token (+ 30-day refresh token). Auth: user token.
    member _.MintWorkspaceToken(apiBase: string, userToken: string, workspaceId: string) =
        task {
            let! r = call apiBase userToken HttpMethod.Post $"/user-app/auth/workspace/token/{Uri.EscapeDataString workspaceId}" (Some "{}")
            return parseSession workspaceId r
        }

    /// Refreshes the workspace token with the workspace refresh token - no user token needed.
    /// While the workspace token is still valid Plaud returns the same tokens unchanged.
    member _.RefreshWorkspaceToken(apiBase: string, refreshToken: string, workspaceId: string) =
        task {
            let! r = call apiBase refreshToken HttpMethod.Post $"/user-app/auth/workspace/refresh/{Uri.EscapeDataString workspaceId}" (Some "{}")
            return parseSession workspaceId r
        }

    /// One page (50) of recordings from all folders, most recently edited first, and the cursor
    /// of the next page.
    member _.ListFiles(apiBase: string, workspaceToken: string, cursor: string option) =
        task {
            let! files, next = page apiBase workspaceToken "/file-app/v4/recordings/all?sort_by=updated_at&sort_order=desc" cursor
            let parsed =
                files
                |> List.choose (fun f ->
                    match str (get f [ "file_id" ]) with
                    | "" -> None
                    | id ->
                        Some {
                            Id = localId id
                            Filename = str (get f [ "name" ])
                            StartTime = DateTimeOffset.FromUnixTimeMilliseconds(int64' (get f [ "created_at_show_ms" ]))
                            DurationMs = int64' (get f [ "duration_ms" ])
                            VersionMs = str (get f [ "version_ms" ])
                        })
            return parsed, next
        }

    /// Ids of all recordings in the main list or the trash. Fails rather than returning a
    /// partial set (callers delete local rows for ids missing here).
    member _.ListAllIds(apiBase: string, workspaceToken: string, trash: bool) =
        task {
            let path = if trash then "/file-app/v4/folders/content?scope=trash" else "/file-app/v4/recordings/all"
            let maxPages = 400
            let ids = Collections.Generic.HashSet<string>()
            let mutable cursor = None
            let mutable pages = 0
            let mutable go = true
            while go do
                let! files, next = page apiBase workspaceToken path cursor
                // The trash also holds folders and other files - only file nodes have a file_id.
                for f in files do
                    match str (get f [ "file_id" ]) with
                    | "" -> ()
                    | id -> ids.Add(localId id) |> ignore
                pages <- pages + 1
                cursor <- next
                go <- next.IsSome && not files.IsEmpty
                if go && pages >= maxPages then
                    raise (PlaudError $"more than {maxPages} pages of recordings - listing stopped")
            return Set.ofSeq ids
        }

    /// Time zone, audio size and whether the recording has marks.
    member _.GetFileDetail(apiBase: string, workspaceToken: string, id: string) =
        task {
            let! d = detail apiBase workspaceToken id
            let meta = get d [ "data"; "meta" ]
            let offset =
                match str (get meta [ "timezone_iana" ]) with
                | "" -> None
                | tz ->
                    try
                        let zone = TimeZoneInfo.FindSystemTimeZoneById tz
                        let start = DateTimeOffset.FromUnixTimeMilliseconds(int64' (get meta [ "start_time" ]))
                        Some(int (zone.GetUtcOffset start).TotalMinutes)
                    with _ -> None
            return {
                Filesize = detailObject "AUDIO" d |> Option.map (fun o -> int64' (get o [ "object_size" ])) |> Option.defaultValue 0L
                UtcOffsetMinutes = offset
                // An empty mark list is "[]".
                HasMarks = detailObject "MARK_MEMO" d |> Option.exists (fun o -> int64' (get o [ "object_size" ]) > 2L)
            }
        }

    /// Marks made during the call (docs/attachments.md, Plaud Marks); empty when the recording has none.
    member _.GetMarks(apiBase: string, workspaceToken: string, id: string) =
        task {
            let! d = detail apiBase workspaceToken id
            match detailObject "MARK_MEMO" d with
            | None -> return []
            | Some o ->
                // Presigned, no auth; may be gzip.
                let! bytes = http.GetByteArrayAsync(str (get o [ "content_url" ]))
                let bytes =
                    if bytes.Length > 2 && bytes[0] = 0x1Fuy && bytes[1] = 0x8Buy then
                        use input = new IO.Compression.GZipStream(new IO.MemoryStream(bytes), IO.Compression.CompressionMode.Decompress)
                        use output = new IO.MemoryStream()
                        input.CopyTo output
                        output.ToArray()
                    else bytes
                let urls =
                    match get d [ "data"; "relation_content_mapping" ] with
                    | :? JsonObject as m -> m |> Seq.map (fun kv -> kv.Key, str kv.Value) |> Map.ofSeq
                    | _ -> Map.empty
                return parseMarks urls (Some(Encoding.UTF8.GetString bytes))
        }

    /// Downloads a mark's file from its presigned URL (no auth) to `path`.
    member _.DownloadMarkFile(url: string, path: string) =
        task {
            use! resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            if not resp.IsSuccessStatusCode then
                raise (PlaudError $"mark file download -> HTTP {int resp.StatusCode}")
            use file = IO.File.Create path
            do! resp.Content.CopyToAsync file
        }

    /// Presigned S3 URL of the audio (Ogg/Opus, the AUDIO object), valid ~60 minutes, supports HTTP Range.
    member _.GetAudioUrl(apiBase: string, workspaceToken: string, id: string) =
        task {
            let! d = detail apiBase workspaceToken id
            match detailObject "AUDIO" d with
            | None -> return raise (PlaudError $"file detail of {id} has no AUDIO object")
            | Some o -> return str (get o [ "content_url" ])
        }
