/// Client for Plaud's private API (no public API exists - reverse-engineered, see
/// docs/riffado-plaud-sync-analysis.md, sections 7 and 8 for what was verified live).
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

type PlaudFile = {
    Id : string
    Filename : string
    StartTime : DateTimeOffset
    DurationMs : int64
    Filesize : int64
    VersionMs : string
    /// Local UTC offset of the recording (Plaud `timezone` hours + `zonemins`).
    UtcOffsetMinutes : int
}

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

    /// One page of recordings (`trash`: the trash instead of the main list), most recently edited first.
    member _.ListFiles(apiBase: string, workspaceToken: string, skip: int, limit: int, ?trash: bool) =
        task {
            let isTrash = if defaultArg trash false then 1 else 0
            let! r = call apiBase workspaceToken HttpMethod.Get $"/file/simple/web?skip={skip}&limit={limit}&is_trash={isTrash}&sort_by=edit_time&is_desc=true" None
            return
                items (get r [ "data_file_list" ])
                |> List.choose (fun f ->
                    match str (get f [ "id" ]) with
                    | "" -> None
                    | id ->
                        Some {
                            Id = id
                            Filename = str (get f [ "filename" ])
                            StartTime = DateTimeOffset.FromUnixTimeMilliseconds(int64' (get f [ "start_time" ]))
                            DurationMs = int64' (get f [ "duration" ])
                            Filesize = int64' (get f [ "filesize" ])
                            VersionMs = str (get f [ "version_ms" ])
                            UtcOffsetMinutes =
                                let hours, minutes = int (int64' (get f [ "timezone" ])), int (int64' (get f [ "zonemins" ]))
                                hours * 60 + (if hours < 0 then -(abs minutes) else minutes)
                        })
        }

    /// Ids of all recordings in the main list or the trash. Fails rather than returning a
    /// partial set (callers delete local rows for ids missing here).
    member this.ListAllIds(apiBase: string, workspaceToken: string, trash: bool) =
        task {
            let pageSize, maxPages = 50, 400
            let ids = Collections.Generic.HashSet<string>()
            let mutable page = 0
            let mutable go = true
            while go do
                let! files = this.ListFiles(apiBase, workspaceToken, page * pageSize, pageSize, trash)
                for f in files do ids.Add f.Id |> ignore
                page <- page + 1
                go <- files.Length = pageSize
                if go && page >= maxPages then
                    raise (PlaudError $"more than {pageSize * maxPages} recordings - listing stopped")
            return Set.ofSeq ids
        }

    /// Presigned S3 URL of the audio (Ogg/Opus), valid ~60 minutes, supports HTTP Range.
    member _.GetAudioUrl(apiBase: string, workspaceToken: string, fileId: string) =
        task {
            let! r = call apiBase workspaceToken HttpMethod.Get $"/file/temp-url/{Uri.EscapeDataString fileId}?is_opus=0" None
            match str (get r [ "temp_url" ]) with
            | "" -> return raise (PlaudError $"temp-url for {fileId} returned no temp_url")
            | url -> return url
        }
