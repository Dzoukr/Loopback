// plaud-login-probe.fsx
// =============================================================================
// Throwaway spike: can Loopback log in to Plaud by itself (email + password) and
// keep its OWN session alive, instead of borrowing the browser's `pld_ut`?
//
//   dotnet fsi spikes/plaud-login-probe.fsx
//
// Credentials: PLAUD_EMAIL / PLAUD_PASSWORD (env vars or .env in the repo root).
// Optional: PLAUD_TOKEN (browser `pld_ut`) - used only to check the browser session
// survives our login. LOGIN_REFRESH_ONLY=1 - skip login, only refresh the user token
// from the cookies saved by the last run (test after the 24 h user token expired).
//
// Flow reverse-engineered from the web.plaud.ai bundle (prod-261001):
//   GET  /config/security            -> { pass_pub_key (secp256k1, compressed hex), pass_algorithm }
//   POST /auth/access-token          multipart: username, password = base64(ECIES(pubKey, {"pass","time"})),
//                                    client_id=web, password_encrypted=true
//                                    status 0 ok, -302 domain switch, 10001 MFA, -3 wrong password
//   POST /auth/refresh-user-token    body {}, cookie-based (HttpOnly refresh cookie)
//   GET  /auth/access-token-list     active sessions
// ECIES = eciesjs defaults: secp256k1, uncompressed ephemeral key, HKDF-SHA256 over
// ephemeralPk || sharedPoint (both uncompressed), AES-256-GCM with a 16-byte nonce,
// output ephemeralPk || nonce || tag || ciphertext.
//
// Read-only apart from creating one login session. Secrets are never printed;
// cookie values live only in spikes/fixtures/.login-state.json (gitignored).
// =============================================================================

#r "nuget: BouncyCastle.Cryptography, 2.4.0"

open System
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Org.BouncyCastle.Asn1.X9
open Org.BouncyCastle.Crypto.Engines
open Org.BouncyCastle.Crypto.Modes
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Math
open Org.BouncyCastle.Security

exception ProbeFailed of string

let fail fmt = Printf.kprintf (fun s -> raise (ProbeFailed s)) fmt

let fixturesDir = Path.Combine(__SOURCE_DIRECTORY__, "fixtures")
let statePath = Path.Combine(fixturesDir, ".login-state.json")
let globalApi = "https://api.plaud.ai"

let userAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

let cookies = CookieContainer()

let http =
    new HttpClient(new HttpClientHandler(CookieContainer = cookies, UseCookies = true,
                                         AutomaticDecompression = DecompressionMethods.All),
                   Timeout = TimeSpan.FromSeconds 60.)

for k, v in [ "User-Agent", userAgent
              "Origin", "https://web.plaud.ai"
              "Referer", "https://web.plaud.ai/"
              "app-platform", "web"
              "edit-from", "web"
              "timezone", TimeZoneInfo.Local.Id ] do
    http.DefaultRequestHeaders.TryAddWithoutValidation(k, v) |> ignore

// --------------------------------------------------------------------------- //
// Output / JSON helpers
// --------------------------------------------------------------------------- //
let step (n: int) (title: string) = printfn "\n[%d] %s" n title
let info fmt = Printf.kprintf (fun s -> printfn "    %s" s) fmt
let warn fmt = Printf.kprintf (fun s -> printfn "    WARNING: %s" s) fmt
let findings = ResizeArray<string>()
let finding fmt = Printf.kprintf (fun s -> findings.Add s; printfn "    => %s" s) fmt

let rec get (n: JsonNode) (keys: string list) : JsonNode =
    match keys, n with
    | [], _ -> n
    | k :: rest, (:? JsonObject as o) -> match o[k] with null -> null | c -> get c rest
    | _ -> null

let str (n: JsonNode) =
    match n with
    | null -> ""
    | n when n.GetValueKind() = JsonValueKind.String -> n.GetValue<string>()
    | n -> n.ToJsonString()

let keysOf (n: JsonNode) =
    match n with
    | :? JsonObject as o -> o |> Seq.map (fun kv -> kv.Key) |> String.concat ", "
    | _ -> "-"

/// First non-empty value of `key`, top level or under `data` (Plaud uses both).
let field (n: JsonNode) (key: string) =
    match str (get n [ key ]) with
    | "" -> str (get n [ "data"; key ])
    | v -> v

let jsonOut = JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let rec redact (n: JsonNode) =
    match n with
    | :? JsonObject as o ->
        for kv in List.ofSeq o do
            match kv.Value with
            | null -> ()
            | _ when (let k = kv.Key.ToLowerInvariant() in k.Contains "token" || k.Contains "secret" || k = "pre_token") ->
                o[kv.Key] <- JsonValue.Create "<redacted>"
            | v -> redact v
    | :? JsonArray as a -> for x in a do if not (isNull x) then redact x
    | _ -> ()

let save (name: string) (n: JsonNode) =
    Directory.CreateDirectory fixturesDir |> ignore
    let copy = JsonNode.Parse(n.ToJsonString())
    redact copy
    File.WriteAllText(Path.Combine(fixturesDir, name), copy.ToJsonString jsonOut, UTF8Encoding false)
    info "saved spikes/fixtures/%s" name

// --------------------------------------------------------------------------- //
// Config (.env)
// --------------------------------------------------------------------------- //
let dotEnv =
    let path = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".env"))
    if File.Exists path then
        File.ReadAllLines path
        |> Array.map _.Trim()
        |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#") && l.Contains "=")
        |> Array.map (fun l -> let i = l.IndexOf '=' in l.Substring(0, i).Trim(), l.Substring(i + 1).Trim().Trim('"'))
        |> Map.ofArray
    else Map.empty

let setting (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null | "" -> dotEnv |> Map.tryFind name |> Option.defaultValue ""
    | v -> v

// --------------------------------------------------------------------------- //
// JWT
// --------------------------------------------------------------------------- //
let claims (jwt: string) : JsonObject =
    let parts = jwt.Split '.'
    if parts.Length <> 3 then null
    else
        let b64 = parts[1].Replace('-', '+').Replace('_', '/')
        try JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64 + String('=', (4 - b64.Length % 4) % 4)))) :?> JsonObject
        with _ -> null

let expiry (jwt: string) =
    match claims jwt with
    | null -> "?"
    | c ->
        match c["exp"] with
        | null -> "no exp"
        | e ->
            let at = DateTimeOffset.FromUnixTimeSeconds(e.GetValue<int64>())
            sprintf "%s UTC (%.1f h from now)" (at.ToString "yyyy-MM-dd HH:mm") (at - DateTimeOffset.UtcNow).TotalHours

let claim (jwt: string) (name: string) =
    match claims jwt with
    | null -> ""
    | c -> str c[name]

let describeToken (label: string) (jwt: string) =
    match claims jwt with
    | null -> info "%s: not a JWT (length %d)" label jwt.Length
    | c ->
        let names = c |> Seq.map (fun kv -> kv.Key) |> String.concat ", "
        info "%s: claims [%s], sid %s, auth_method %s, expires %s" label names (claim jwt "sid") (claim jwt "auth_method") (expiry jwt)

let regionApiBase =
    function
    | "aws:us-west-2" -> Some "https://api.plaud.ai"
    | "aws:eu-central-1" -> Some "https://api-euc1.plaud.ai"
    | "aws:ap-southeast-1" -> Some "https://api-apse1.plaud.ai"
    | _ -> None

let isPlaudHost (url: string) =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, u -> u.Scheme = "https" && (u.Host = "plaud.ai" || u.Host.EndsWith ".plaud.ai")
    | _ -> false

// --------------------------------------------------------------------------- //
// HTTP: returns the parsed body whatever the business status (the caller decides)
// --------------------------------------------------------------------------- //
let send (req: HttpRequestMessage) : JsonNode =
    use resp = http.Send req
    let text = (new StreamReader(resp.Content.ReadAsStream())).ReadToEnd()
    let call = $"{req.Method.Method} {req.RequestUri.AbsolutePath}"
    match (try JsonNode.Parse text with _ -> null) with
    | null -> fail "%s -> HTTP %d, non-JSON body: %s" call (int resp.StatusCode) (if text.Length > 300 then text.Substring(0, 300) else text)
    | n ->
        if not resp.IsSuccessStatusCode then
            info "%s -> HTTP %d (status %s, msg %s)" call (int resp.StatusCode) (str (get n [ "status" ])) (str (get n [ "msg" ]))
        n

let request (meth: HttpMethod) (url: string) (bearer: string option) (content: HttpContent option) =
    let req = new HttpRequestMessage(meth, url)
    bearer |> Option.iter (fun b -> req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + b) |> ignore)
    content |> Option.iter (fun c -> req.Content <- c)
    send req

let ok (call: string) (n: JsonNode) =
    match str (get n [ "status" ]) with
    | "0" -> n
    | s -> fail "%s -> business status %s, msg: %s" call s (str (get n [ "msg" ]))

let json (s: string) = new StringContent(s, Encoding.UTF8, "application/json") :> HttpContent

let domainSwitch (n: JsonNode) =
    [ get n [ "data"; "domains"; "api" ]; get n [ "domains"; "api" ]; get n [ "data"; "domain" ] ]
    |> List.map str
    |> List.tryFind (fun d -> d <> "" && isPlaudHost d)
    |> Option.map _.TrimEnd('/')

// --------------------------------------------------------------------------- //
// Password encryption (eciesjs-compatible, secp256k1)
// --------------------------------------------------------------------------- //
let eciesEncrypt (pubKeyHex: string) (plaintext: byte[]) =
    let curve = ECNamedCurveTable.GetByName "secp256k1"
    let receiver = curve.Curve.DecodePoint(Convert.FromHexString pubKeyHex)
    let rng = SecureRandom()
    let mutable d = BigInteger.Zero
    while d.SignValue <= 0 || d.CompareTo curve.N >= 0 do
        d <- BigInteger(256, rng)
    let ephemeralPk = curve.G.Multiply(d).Normalize().GetEncoded false
    let shared = receiver.Multiply(d).Normalize().GetEncoded false
    let key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Array.append ephemeralPk shared, 32, Array.empty, Array.empty)
    let nonce = RandomNumberGenerator.GetBytes 16
    let gcm = GcmBlockCipher(AesEngine())
    gcm.Init(true, AeadParameters(KeyParameter key, 128, nonce))
    let out = Array.zeroCreate<byte> (gcm.GetOutputSize plaintext.Length)
    let n = gcm.ProcessBytes(plaintext, 0, plaintext.Length, out, 0)
    gcm.DoFinal(out, n) |> ignore
    let cipher, tag = out[.. out.Length - 17], out[out.Length - 16 ..]
    Convert.ToBase64String(Array.concat [ ephemeralPk; nonce; tag; cipher ])

let encryptPassword (apiBase: string) (password: string) =
    let sec = request HttpMethod.Get (apiBase + "/config/security") None None |> ok "GET /config/security"
    let pub, alg = str (get sec [ "data"; "pass_pub_key" ]), str (get sec [ "data"; "pass_algorithm" ])
    if alg <> "secp256k1" then fail "pass_algorithm is '%s' - only secp256k1 is implemented" alg
    let payload = JsonObject()
    payload["pass"] <- JsonValue.Create password
    payload["time"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
    eciesEncrypt pub (Encoding.UTF8.GetBytes(payload.ToJsonString()))

// --------------------------------------------------------------------------- //
// Cookies (persisted so LOGIN_REFRESH_ONLY can run later)
// --------------------------------------------------------------------------- //
let describeCookies () =
    let all = cookies.GetAllCookies() |> List.ofSeq
    if all.IsEmpty then info "no cookies set"
    for c in all do
        info "cookie %s: domain %s, path %s, HttpOnly %b, expires %s, length %d"
            c.Name c.Domain c.Path c.HttpOnly
            (if c.Expires = DateTime.MinValue then "session" else c.Expires.ToUniversalTime().ToString "yyyy-MM-dd HH:mm 'UTC'")
            c.Value.Length
    all

let saveState (apiBase: string) (ut: string) =
    Directory.CreateDirectory fixturesDir |> ignore
    let o = JsonObject()
    o["apiBase"] <- JsonValue.Create apiBase
    o["userToken"] <- JsonValue.Create ut
    o["savedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString "o")
    let arr = JsonArray()
    for c in cookies.GetAllCookies() do
        let x = JsonObject()
        x["name"] <- JsonValue.Create c.Name
        x["value"] <- JsonValue.Create c.Value
        x["domain"] <- JsonValue.Create c.Domain
        x["path"] <- JsonValue.Create c.Path
        x["httpOnly"] <- JsonValue.Create c.HttpOnly
        x["expires"] <- JsonValue.Create(c.Expires.ToUniversalTime().ToString "o")
        arr.Add x
    o["cookies"] <- arr
    File.WriteAllText(statePath, o.ToJsonString jsonOut, UTF8Encoding false)
    info "saved session state to spikes/fixtures/.login-state.json (secret - delete when done)"

let loadState () =
    if not (File.Exists statePath) then fail "No %s - run the full probe first" statePath
    let s = JsonNode.Parse(File.ReadAllText statePath)
    for c in get s [ "cookies" ] :?> JsonArray do
        let ck = Cookie(str c["name"], str c["value"], str c["path"], str c["domain"], HttpOnly = (c["httpOnly"].GetValue<bool>()))
        let exp = DateTime.Parse(str c["expires"]).ToUniversalTime()
        if exp > DateTime.MinValue.AddDays 1. then ck.Expires <- exp
        cookies.Add ck
    info "state saved at %s" (str (get s [ "savedAt" ]))
    str (get s [ "apiBase" ]), str (get s [ "userToken" ])

// --------------------------------------------------------------------------- //
// Steps
// --------------------------------------------------------------------------- //
let workspacesOk (apiBase: string) (ut: string) =
    let n = request HttpMethod.Get (apiBase + "/team-app/workspaces/list?need_personal_workspace=true") (Some ut) None
    str (get n [ "status" ]) = "0", n

/// Proves a user token works for real data: mint a workspace token, list one recording.
let dataCheck (apiBase: string) (ut: string) =
    let ok', ws = workspacesOk apiBase ut
    if not ok' then fail "workspaces/list rejected the user token (status %s, msg %s)" (str (get ws [ "status" ])) (str (get ws [ "msg" ]))
    let list = get ws [ "data"; "workspaces" ] :?> JsonArray |> Seq.filter (isNull >> not) |> List.ofSeq
    let personal = list |> List.tryFind (fun w -> str w["workspace_type"] = "0") |> Option.orElse (List.tryHead list)
    let wsId = personal |> Option.map (fun w -> str w["workspace_id"]) |> Option.defaultValue ""
    if wsId = "" then fail "no workspace found"
    let mint =
        request HttpMethod.Post (apiBase + $"/user-app/auth/workspace/token/{Uri.EscapeDataString wsId}") (Some ut) (Some(json "{}"))
        |> ok "POST workspace/token"
    let wt = str (get mint [ "data"; "workspace_token" ])
    let files =
        request HttpMethod.Get (apiBase + "/file/simple/web?skip=0&limit=1&is_trash=0&sort_by=edit_time&is_desc=true") (Some wt) None
        |> ok "GET /file/simple/web"
    info "workspace %s, workspace token sid %s, recordings total %s" wsId (claim wt "ut_sid") (str (get files [ "data_file_total" ]))

let listSessions (apiBase: string) (ut: string) (fixture: string) =
    let n = request HttpMethod.Get (apiBase + "/auth/access-token-list") (Some ut) None
    if str (get n [ "status" ]) = "0" then
        save fixture n
        let data = get n [ "data" ]
        let arr =
            match data with
            | :? JsonArray as a -> Some a
            | :? JsonObject as o -> o |> Seq.tryPick (fun kv -> match kv.Value with :? JsonArray as a -> Some a | _ -> None)
            | _ -> None
        match arr with
        | Some a ->
            info "%d active session(s), item keys: %s" a.Count (a |> Seq.tryHead |> Option.map keysOf |> Option.defaultValue "-")
            a.Count
        | None -> info "sessions response data keys: %s" (keysOf data); -1
    else
        info "access-token-list -> status %s, msg %s" (str (get n [ "status" ])) (str (get n [ "msg" ]))
        -1

let refreshUserToken (apiBase: string) =
    let rec go (apiBase: string) hops =
        let n = request HttpMethod.Post (apiBase + "/auth/refresh-user-token") None (Some(json "{}"))
        match str (get n [ "status" ]), domainSwitch n with
        | "-302", Some d when hops < 2 -> info "domain switch -> %s" d; go d (hops + 1)
        | _ -> apiBase, n
    go apiBase 0

/// The user token after a login/refresh: in the body, or only as the `pld_ut` cookie.
let userTokenFrom (n: JsonNode) =
    match field n "access_token" with
    | "" ->
        cookies.GetAllCookies()
        |> Seq.tryFind (fun c -> c.Name = "pld_ut")
        |> Option.map _.Value
        |> Option.defaultValue ""
    | t -> t

let checkBrowserToken (label: string) (browserUt: string) =
    if browserUt <> "" then
        match claims browserUt with
        | null -> info "PLAUD_TOKEN is not a JWT, skipping browser-session check"
        | _ when claim browserUt "exp" <> "" && DateTimeOffset.FromUnixTimeSeconds(int64 (claim browserUt "exp")) < DateTimeOffset.UtcNow ->
            info "PLAUD_TOKEN (browser) already expired, skipping browser-session check"
        | _ ->
            let apiBase = regionApiBase (claim browserUt "region") |> Option.defaultValue globalApi
            let ok', n = workspacesOk apiBase browserUt
            info "%s: browser PLAUD_TOKEN (sid %s) %s" label (claim browserUt "sid")
                (if ok' then "still works" else sprintf "REJECTED (status %s, msg %s)" (str (get n [ "status" ])) (str (get n [ "msg" ])))
            if label = "after login" then
                finding "Browser session %s our own login" (if ok' then "survives" else "is KILLED by")

let refreshPhase (stepNo: int) (apiBase: string) (ut0: string) =
    step stepNo "User token refresh (cookie-based, no password)"
    let apiBase', r = refreshUserToken apiBase
    save "user-token-refresh.json" r
    info "status %s, msg %s, top keys [%s], data keys [%s]" (str (get r [ "status" ])) (str (get r [ "msg" ])) (keysOf r) (keysOf (get r [ "data" ]))
    info "ut_expires_in %s s, urt_expires_in %s s, urt_expire_at %s" (field r "ut_expires_in") (field r "urt_expires_in") (field r "urt_expire_at")
    describeCookies () |> ignore
    if str (get r [ "status" ]) <> "0" then
        finding "User token refresh FAILED (status %s, %s)" (str (get r [ "status" ])) (str (get r [ "msg" ]))
        None
    else
        let ut1 = userTokenFrom r
        describeToken "refreshed user token" ut1
        finding "User token refresh works: new token %s, same sid %b, refresh-cookie lifetime %s s"
            (if ut1 <> "" && ut1 <> ut0 then "issued" else "NOT found (body nor pld_ut cookie)")
            (claim ut1 "sid" = claim ut0 "sid") (field r "urt_expires_in")
        if ut1 <> "" then
            step (stepNo + 1) "Data call with the refreshed user token"
            dataCheck apiBase' ut1
            let oldOk, _ = workspacesOk apiBase' ut0
            finding "Previous user token after refresh: %s" (if oldOk then "still valid" else "revoked")
            saveState apiBase' ut1
        Some ut1

let login () =
    let email, password = setting "PLAUD_EMAIL", setting "PLAUD_PASSWORD"
    if email = "" || password = "" then fail "Set PLAUD_EMAIL and PLAUD_PASSWORD (env or .env)"
    let browserUt = setting "PLAUD_TOKEN"

    step 1 "Browser session before login"
    if browserUt = "" then info "no PLAUD_TOKEN - skipping" else checkBrowserToken "before login" browserUt

    step 2 "Password login (POST /auth/access-token)"
    let rec attempt (apiBase: string) hops =
        let form = new MultipartFormDataContent()
        form.Add(new StringContent(email), "username")
        form.Add(new StringContent(encryptPassword apiBase password), "password")
        form.Add(new StringContent("web"), "client_id")
        form.Add(new StringContent("true"), "password_encrypted")
        let n = request HttpMethod.Post (apiBase + "/auth/access-token") None (Some form)
        match str (get n [ "status" ]), domainSwitch n with
        | "-302", Some d when hops < 2 -> info "domain switch -> %s" d; attempt d (hops + 1)
        | _ -> apiBase, n
    let apiBase0, r = attempt globalApi 0
    save "password-login.json" r
    let status = str (get r [ "status" ])
    info "status %s, msg %s, top keys [%s], data keys [%s]" status (str (get r [ "msg" ])) (keysOf r) (keysOf (get r [ "data" ]))
    match status with
    | "0" -> ()
    | "10001" -> fail "MFA required - the account has two-factor auth on; password login alone is not enough"
    | "-3" -> fail "Wrong password (status -3)"
    | s -> fail "Login failed: status %s, msg %s" s (str (get r [ "msg" ]))
    let ut = userTokenFrom r
    if ut = "" then fail "Login OK but no user token in the body nor a pld_ut cookie"
    describeToken "user token" ut
    info "ut_expires_in %s s, urt_expires_in %s s, token_id present %b, refresh_token in body %b"
        (field r "ut_expires_in") (field r "urt_expires_in") (field r "token_id" <> "") (field r "refresh_token" <> "")
    let apiBase =
        match domainSwitch r with
        | Some d -> d
        | None -> regionApiBase (claim ut "region") |> Option.defaultValue apiBase0
    info "api base %s" apiBase
    describeCookies () |> ignore
    finding "Password login works: user token sid %s, expires %s, auth_method %s" (claim ut "sid") (expiry ut) (claim ut "auth_method")
    if browserUt <> "" && claims browserUt <> null then
        finding "Own session id differs from the browser's: %b" (claim ut "sid" <> claim browserUt "sid")

    step 3 "Data call with our own user token"
    dataCheck apiBase ut
    saveState apiBase ut

    step 4 "Active sessions"
    listSessions apiBase ut "sessions-after-login.json" |> ignore

    step 5 "Browser session after login"
    if browserUt = "" then info "no PLAUD_TOKEN - check web.plaud.ai manually: are you still logged in?"
    else checkBrowserToken "after login" browserUt

    refreshPhase 6 apiBase ut |> ignore

let refreshOnly () =
    step 1 "Load saved session"
    let apiBase, ut0 = loadState ()
    describeToken "saved user token" ut0
    describeCookies () |> ignore
    refreshPhase 2 apiBase ut0 |> ignore

try
    try
        if setting "LOGIN_REFRESH_ONLY" = "1" then refreshOnly () else login ()
    with
    | ProbeFailed m -> printfn "\nFAILED: %s" m
    | ex -> printfn "\nERROR: %s" (ex.ToString())
finally
    printfn "\n=== Findings ==="
    for f in findings do printfn " - %s" f
