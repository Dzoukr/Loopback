/// Speechmatics batch API (verified with spikes/speechmatics-probe.fsx).
module Loopback.Server.Integrations.Speechmatics

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

exception SpeechmaticsError of string

type JobStatus =
    | Running
    | Done
    /// rejected / expired / deleted, with Speechmatics' error details.
    | Failed of string

/// Consecutive words of one speaker merged together.
type Segment = {
    Speaker : string
    Start : float
    End : float
    Text : string
}

let private baseUrl = "https://asr.api.speechmatics.com/v2"

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

let private float' (n: JsonNode) =
    match Double.TryParse(str n, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
    | true, v -> v
    | _ -> 0.

let private items (n: JsonNode) =
    match n with
    | :? JsonArray as a -> a |> Seq.filter (isNull >> not) |> List.ofSeq
    | _ -> []

/// json-v2 transcript (words + punctuation) -> speaker segments.
let toSegments (transcript: JsonNode) : Segment list =
    let segs = ResizeArray<Segment * StringBuilder>()
    for r in items (get transcript [ "results" ]) do
        match items (get r [ "alternatives" ]) |> List.tryHead with
        | Some alt when str (get alt [ "content" ]) <> "" ->
            let content = str (get alt [ "content" ])
            let speaker = match str (get alt [ "speaker" ]) with "" -> "UU" | s -> s
            let start = float' (get r [ "start_time" ])
            let finish = float' (get r [ "end_time" ])
            let isPunctuation = str (get r [ "type" ]) = "punctuation"
            if segs.Count = 0 || (fst segs[segs.Count - 1]).Speaker <> speaker then
                segs.Add({ Speaker = speaker; Start = start; End = finish; Text = "" }, StringBuilder(content))
            else
                let seg, sb = segs[segs.Count - 1]
                // Words get a leading space; punctuation attaches to the previous token.
                if not isPunctuation then sb.Append ' ' |> ignore
                sb.Append content |> ignore
                segs[segs.Count - 1] <- ({ seg with End = finish }, sb)
        | _ -> ()
    segs |> Seq.map (fun (s, sb) -> { s with Text = sb.ToString() }) |> List.ofSeq

type SpeechmaticsClient(http: HttpClient, apiKey: string, model: string, language: string) =

    let send (meth: HttpMethod) (path: string) (content: HttpContent option) =
        task {
            use req = new HttpRequestMessage(meth, baseUrl + path)
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey) |> ignore
            content |> Option.iter (fun c -> req.Content <- c)
            use! resp = http.SendAsync req
            let! text = resp.Content.ReadAsStringAsync()
            return int resp.StatusCode, text
        }

    let parse (name: string) (status: int, text: string) =
        if status < 200 || status >= 300 then
            raise (SpeechmaticsError $"{name} -> HTTP {status}: {text.Substring(0, min 300 text.Length)}")
        match (try JsonNode.Parse text with _ -> null) with
        | null -> raise (SpeechmaticsError $"{name} -> non-JSON body")
        | n -> n

    /// Submits a job with the audio file uploaded in the same request.
    member _.Submit(audioPath: string) =
        task {
            let tc = JsonObject()
            tc["language"] <- JsonValue.Create language
            tc["diarization"] <- JsonValue.Create "speaker"
            // melia-* is selected via `model` (and does not accept language "auto");
            // standard / enhanced via `operating_point`.
            if model.StartsWith "melia" then tc["model"] <- JsonValue.Create model
            else tc["operating_point"] <- JsonValue.Create model
            let config = JsonObject()
            config["type"] <- JsonValue.Create "transcription"
            config["transcription_config"] <- tc
            use form = new MultipartFormDataContent()
            form.Add(new StringContent(config.ToJsonString(), Encoding.UTF8), "config")
            let audio = new StreamContent(File.OpenRead audioPath)
            audio.Headers.ContentType <- MediaTypeHeaderValue "audio/ogg"
            form.Add(audio, "data_file", Path.GetFileName audioPath)
            let! r = send HttpMethod.Post "/jobs" (Some form)
            match str (get (parse "POST /jobs" r) [ "id" ]) with
            | "" -> return raise (SpeechmaticsError "POST /jobs returned no job id")
            | id -> return id
        }

    member _.GetStatus(jobId: string) =
        task {
            let! r = send HttpMethod.Get $"/jobs/{jobId}" None
            let job = get (parse "GET /jobs/{id}" r) [ "job" ]
            match str (get job [ "status" ]) with
            | "done" -> return Done
            | "rejected" | "expired" | "deleted" as s ->
                let errors = str (get job [ "errors" ])
                return Failed $"job {s}: {errors}"
            | _ -> return Running
        }

    member _.GetSegments(jobId: string) =
        task {
            let! r = send HttpMethod.Get $"/jobs/{jobId}/transcript?format=json-v2" None
            return toSegments (parse "GET transcript" r)
        }

    /// Removes the job (audio + transcript) from Speechmatics. 404 counts as already gone.
    member _.Delete(jobId: string) =
        task {
            let! status, text = send HttpMethod.Delete $"/jobs/{jobId}" None
            if status <> 404 && (status < 200 || status >= 300) then
                raise (SpeechmaticsError $"DELETE /jobs/{jobId} -> HTTP {status}: {text.Substring(0, min 300 text.Length)}")
        }
