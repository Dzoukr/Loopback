// speechmatics-probe.fsx
// =============================================================================
// Throwaway spike: verifies the Speechmatics batch flow the processing job will
// use, with audio fetched by Speechmatics straight from Plaud's presigned URL
// (no download/upload by Loopback).
//
//   dotnet fsi spikes/speechmatics-probe.fsx
//
// Needs SPEECHMATICS_API_KEY in .env (repo root, gitignored)
// and either AUDIO_URL (any publicly fetchable audio URL) or a saved
// spikes/fixtures/.refresh-state.json (apiBase, workspaceId, refreshToken - used to
// get a fresh Plaud audio URL via workspace-token refresh).
// Optional env: PLAUD_FILE_ID (default: newest recording), SM_MODEL (default
// melia-1), SM_LANGUAGE (default multi).
//
// COSTS MONEY: Speechmatics bills per audio hour (melia-1 ~$0.129/h).
// The job is deleted at the end; the transcript is saved to spikes/fixtures/
// (gitignored - private data).
// =============================================================================

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading

exception ProbeFailed of string

let fail fmt = Printf.kprintf (fun s -> raise (ProbeFailed s)) fmt
let step (n: int) (title: string) = printfn "\n[%d] %s" n title
let info fmt = Printf.kprintf (fun s -> printfn "    %s" s) fmt
let findings = ResizeArray<string>()
let finding fmt = Printf.kprintf (fun s -> findings.Add s; printfn "    => %s" s) fmt

let fixturesDir = Path.Combine(__SOURCE_DIRECTORY__, "fixtures")
let jsonOut = JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
let smBase = "https://asr.api.speechmatics.com/v2"
let plaudUserAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

let http = new HttpClient(Timeout = TimeSpan.FromSeconds 120.)

let env (name: string) (fallback: string) =
    match Environment.GetEnvironmentVariable name with
    | null | "" -> fallback
    | v -> v

let rec get (n: JsonNode) (keys: string list) : JsonNode =
    match keys, n with
    | [], _ -> n
    | k :: rest, (:? JsonObject as o) ->
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
    match Double.TryParse(str n, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
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

let save (name: string) (n: JsonNode) =
    Directory.CreateDirectory fixturesDir |> ignore
    File.WriteAllText(Path.Combine(fixturesDir, name), n.ToJsonString jsonOut, UTF8Encoding false)
    info "saved spikes/fixtures/%s" name

let send (req: HttpRequestMessage) =
    use resp = http.Send req
    let text = (new StreamReader(resp.Content.ReadAsStream())).ReadToEnd()
    int resp.StatusCode, text

let parseOrFail (call: string) (status: int, text: string) =
    if status < 200 || status >= 300 then fail "%s -> HTTP %d: %s" call status (text.Substring(0, min 400 text.Length))
    match (try JsonNode.Parse text with _ -> null) with
    | null -> fail "%s -> non-JSON body: %s" call (text.Substring(0, min 200 text.Length))
    | n -> n

// --------------------------------------------------------------------------- //
// Config
// --------------------------------------------------------------------------- //
let apiKey =
    let path = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".env"))
    if not (File.Exists path) then fail ".env not found at %s" path
    File.ReadAllLines path
    |> Array.tryPick (fun l -> if l.Trim().StartsWith "SPEECHMATICS_API_KEY=" then Some(l.Trim().Substring 21) else None)
    |> Option.defaultWith (fun () -> fail ".env has no SPEECHMATICS_API_KEY")

let smRequest (meth: HttpMethod) (path: string) =
    let r = new HttpRequestMessage(meth, smBase + path)
    r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey) |> ignore
    r

// --------------------------------------------------------------------------- //
// Plaud: fresh presigned audio URL via the saved workspace refresh token
// --------------------------------------------------------------------------- //
let plaudCall (apiBase: string) (bearer: string) (meth: HttpMethod) (path: string) (body: string option) =
    use r = new HttpRequestMessage(meth, apiBase + path)
    r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer) |> ignore
    r.Headers.TryAddWithoutValidation("User-Agent", plaudUserAgent) |> ignore
    body |> Option.iter (fun b -> r.Content <- new StringContent(b, Encoding.UTF8, "application/json"))
    let n = parseOrFail $"Plaud {meth.Method} {path.Split('?')[0]}" (send r)
    if str (get n [ "status" ]) <> "0" then fail "Plaud %s -> status %s: %s" path (str (get n [ "status" ])) (str (get n [ "msg" ]))
    n

let plaudAudio () =
    let statePath = Path.Combine(fixturesDir, ".refresh-state.json")
    if not (File.Exists statePath) then fail "No %s - set AUDIO_URL instead" statePath
    let s = JsonNode.Parse(File.ReadAllText statePath)
    let apiBase, wsId, rt = str (get s [ "apiBase" ]), str (get s [ "workspaceId" ]), str (get s [ "refreshToken" ])
    let r = plaudCall apiBase rt HttpMethod.Post $"/user-app/auth/workspace/refresh/{Uri.EscapeDataString wsId}" (Some "{}")
    let wt = str (get r [ "data"; "workspace_token" ])
    info "Plaud workspace token refreshed (expires %s)"
        (num (get r [ "data"; "wt_expires_at" ]) |> Option.map (fun v -> DateTimeOffset.FromUnixTimeSeconds(int64 v).ToString "yyyy-MM-dd HH:mm 'UTC'") |> Option.defaultValue "?")
    let list = plaudCall apiBase wt HttpMethod.Get "/file/simple/web?skip=0&limit=50&is_trash=0&sort_by=edit_time&is_desc=true" None
    let files = items (get list [ "data_file_list" ])
    let target =
        match env "PLAUD_FILE_ID" "" with
        | "" -> files |> List.tryHead
        | id -> files |> List.tryFind (fun f -> str (get f [ "id" ]) = id)
    match target with
    | None -> fail "no Plaud recording found"
    | Some f ->
        let id = str (get f [ "id" ])
        let minutes = (num (get f [ "duration" ]) |> Option.defaultValue 0.) / 60000.
        info "recording %s, %.1f min" id minutes
        let t = plaudCall apiBase wt HttpMethod.Get $"/file/temp-url/{id}?is_opus=0" None
        str (get t [ "temp_url" ]), minutes

// --------------------------------------------------------------------------- //
// Transcript reshaping: json-v2 words -> speaker segments
// --------------------------------------------------------------------------- //
type Segment = { Speaker: string; Start: float; End: float; Text: StringBuilder }

let toSegments (transcript: JsonNode) =
    let segs = ResizeArray<Segment>()
    for r in items (get transcript [ "results" ]) do
        match items (get r [ "alternatives" ]) |> List.tryHead with
        | Some alt when str (get alt [ "content" ]) <> "" ->
            let content = str (get alt [ "content" ])
            let speaker = match str (get alt [ "speaker" ]) with "" -> "UU" | s -> s
            let start = num (get r [ "start_time" ]) |> Option.defaultValue 0.
            let finish = num (get r [ "end_time" ]) |> Option.defaultValue start
            let isPunct = str (get r [ "type" ]) = "punctuation"
            if segs.Count = 0 || segs[segs.Count - 1].Speaker <> speaker then
                segs.Add { Speaker = speaker; Start = start; End = finish; Text = StringBuilder(content) }
            else
                let last = segs[segs.Count - 1]
                // Words get a leading space; punctuation attaches to the previous token.
                if not isPunct then last.Text.Append ' ' |> ignore
                last.Text.Append content |> ignore
                segs[segs.Count - 1] <- { last with End = finish }
        | _ -> ()
    List.ofSeq segs

// --------------------------------------------------------------------------- //
// Probe
// --------------------------------------------------------------------------- //
let run () =
    step 1 "Audio URL"
    let audioUrl, minutes =
        match env "AUDIO_URL" "" with
        | "" -> plaudAudio ()
        | u -> u, 0.
    info "audio host %s" (Uri audioUrl).Host
    let model, language = env "SM_MODEL" "melia-1", env "SM_LANGUAGE" "multi"
    if minutes > 0. then info "expected cost at $0.129/h: ~$%.2f" (minutes / 60. * 0.129)

    step 2 $"Submit job (fetch_data, {model}, language {language}, diarization speaker)"
    let tc = JsonObject()
    tc["language"] <- JsonValue.Create language
    tc["diarization"] <- JsonValue.Create "speaker"
    // melia-* is selected via `model`; standard/enhanced via `operating_point` (see speechmatics-proxy.py).
    if model.StartsWith "melia" then tc["model"] <- JsonValue.Create model
    else tc["operating_point"] <- JsonValue.Create model
    let fetchData = JsonObject()
    fetchData["url"] <- JsonValue.Create audioUrl
    let config = JsonObject()
    config["type"] <- JsonValue.Create "transcription"
    config["transcription_config"] <- tc
    config["fetch_data"] <- fetchData
    use submit = smRequest HttpMethod.Post "/jobs"
    let form = new MultipartFormDataContent()
    form.Add(new StringContent(config.ToJsonString(), Encoding.UTF8), "config")
    submit.Content <- form
    let submitted = parseOrFail "POST /jobs" (send submit)
    let jobId = str (get submitted [ "id" ])
    if jobId = "" then fail "submit response has no id: %s" (submitted.ToJsonString())
    info "job id %s" jobId
    finding "Submit with fetch_data (Plaud presigned URL, no upload) accepted"

    let deleteJob () =
        step 5 "Delete job"
        use del = smRequest HttpMethod.Delete $"/jobs/{jobId}"
        let status, text = send del
        info "DELETE -> HTTP %d %s" status (text.Substring(0, min 200 text.Length))
        use check = smRequest HttpMethod.Get $"/jobs/{jobId}"
        let status2, text2 = send check
        let after =
            match (try JsonNode.Parse text2 with _ -> null) with
            | null -> ""
            | n -> str (get n [ "job"; "status" ])
        info "GET after delete -> HTTP %d, status '%s'" status2 after
        if status >= 200 && status < 300 then
            finding "Job delete works (GET afterwards: HTTP %d%s)" status2 (if after = "" then "" else $", status {after}")
        else finding "Job delete FAILED: HTTP %d" status

    try
        step 3 "Poll job status"
        let sw = Stopwatch.StartNew()
        let timeout = TimeSpan.FromMinutes 45.
        let mutable last = ""
        let mutable final = ""
        let mutable jobInfo: JsonNode = null
        while final = "" do
            use r = smRequest HttpMethod.Get $"/jobs/{jobId}"
            let n = parseOrFail "GET /jobs/{id}" (send r)
            jobInfo <- get n [ "job" ]
            let st = str (get jobInfo [ "status" ])
            if st <> last then
                info "%5.0fs  status %s" sw.Elapsed.TotalSeconds st
                last <- st
            match st with
            | "done" | "rejected" | "expired" | "deleted" -> final <- st
            | _ when sw.Elapsed > timeout -> fail "job not finished after %.0f min (status %s)" timeout.TotalMinutes st
            | _ -> Thread.Sleep 10_000
        save "speechmatics-job.json" jobInfo
        if final <> "done" then fail "job ended as '%s': %s" final (str (get jobInfo [ "errors" ]))
        let audioSec = num (get jobInfo [ "duration" ]) |> Option.defaultValue 0.
        finding "Job done in %.1f min for %.1f min of audio (%.0fx realtime), billed ~$%.3f"
            sw.Elapsed.TotalMinutes (audioSec / 60.) (audioSec / max 1. sw.Elapsed.TotalSeconds) (audioSec / 3600. * 0.129)

        step 4 "Fetch transcript (json-v2)"
        use tr = smRequest HttpMethod.Get $"/jobs/{jobId}/transcript?format=json-v2"
        let transcript = parseOrFail "GET transcript" (send tr)
        save "speechmatics-transcript.json" transcript
        info "top-level keys: %s" (keysOf transcript)
        info "metadata: %s" (keysOf (get transcript [ "metadata" ]))
        let results = items (get transcript [ "results" ])
        match results |> List.tryHead with
        | Some r ->
            info "results: %d, first result keys: %s; alternative keys: %s"
                results.Length (keysOf r) (items (get r [ "alternatives" ]) |> List.tryHead |> Option.map keysOf |> Option.defaultValue "-")
        | None -> info "results: 0"
        let languages =
            results
            |> List.choose (fun r -> items (get r [ "alternatives" ]) |> List.tryHead |> Option.map (fun a -> str (get a [ "language" ])))
            |> List.filter ((<>) "")
            |> List.countBy id
            |> List.sortByDescending snd
        info "languages (by word count): %s" (languages |> List.map (fun (l, c) -> $"{l} {c}") |> String.concat ", ")
        let segments = toSegments transcript
        let speakers = segments |> List.map (fun s -> s.Speaker) |> List.distinct
        let text = segments |> List.sumBy (fun s -> s.Text.Length)
        let arr = JsonArray()
        for s in segments do
            let o = JsonObject()
            o["speaker"] <- JsonValue.Create s.Speaker
            o["start"] <- JsonValue.Create s.Start
            o["end"] <- JsonValue.Create s.End
            o["text"] <- JsonValue.Create(s.Text.ToString())
            arr.Add o
        save "speechmatics-segments.json" arr
        finding "Transcript: %d words/punct -> %d speaker segments, speakers %s, %d chars (~%d tokens for Claude)"
            results.Length segments.Length (String.Join(",", speakers)) text (text / 4)
    finally
        deleteJob ()

try
    run ()
    printfn "\nFINDINGS"
    for f in findings do printfn "  - %s" f
with
| ProbeFailed msg ->
    printfn "\nFAILED: %s" msg
    exit 1
| ex ->
    printfn "\nFAILED (unexpected): %s" ex.Message
    exit 1
