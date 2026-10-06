/// The steps a queued recording goes through. Each step reads and writes only SQLite
/// state, so a restart resumes exactly where processing stopped.
module Loopback.Server.Features.Recordings.Processing.Pipeline

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Loopback.Server
open Loopback.Server.Configuration
open Loopback.Server.Integrations.Plaud
open Loopback.Server.Integrations.Speechmatics
open Loopback.Server.Integrations.ClaudeBridge
open Loopback.Server.Features.Recordings.Domain
open Loopback.Server.Features.Recordings.Database
open Loopback.Server.Features.Recordings.Audio.AudioStore
open Loopback.Server.Features.Recordings.Processing.Workflows

/// Human-readable message for a failed step.
let describe (ex: exn) =
    match ex with
    | PlaudUnauthorized m | PlaudSessionExpired m | PlaudError m | SpeechmaticsError m | BridgeError m -> m
    | :? AggregateException as a when a.InnerExceptions.Count = 1 -> a.InnerExceptions[0].Message
    | _ -> ex.Message

let private attempt (f: unit -> Task<'a>) : Task<Result<'a, exn>> =
    task {
        try
            let! v = f ()
            return Ok v
        with ex -> return Error ex
    }

let private timestamp (seconds: float) = TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss")

/// Transcript as Claude reads it: one line per speaker turn.
let private transcriptText (segments: Segment list) =
    segments
    |> List.map (fun s -> $"[{timestamp s.Start}] {s.Speaker}: {s.Text}")
    |> String.concat "\n"

/// First non-empty line of a title run, without surrounding quotes / markdown.
let private cleanTitle (text: string) =
    text.Split('\n')
    |> Array.map (fun l -> l.Trim().TrimStart('#').Trim().Trim('"', '\'', '*').Trim())
    |> Array.tryFind ((<>) "")
    |> Option.map (fun t -> if t.Length > 200 then t.Substring(0, 200) else t)

type Pipeline(
    recordings: RecordingsRepository,
    audio: AudioStore,
    speechmatics: SpeechmaticsClient,
    bridge: ClaudeBridgeClient,
    workflows: WorkflowCatalog,
    cfg: Configuration,
    logger: ILogger<Pipeline>) =

    let setStatus (r: RecordingRow) (status: RecordingStatus) =
        { r with Status = RecordingStatus.toKey status; StepStartedAt = Some(Db.now ()) }

    /// The background steps work on a snapshot; before writing they check the recording is still in
    /// the step's status (it may have been deleted meanwhile) - otherwise the work is dropped.
    let stillIn (id: string) (status: RecordingStatus) =
        task {
            let! current = recordings.TryGet id
            let ok = current |> Option.exists (fun c -> c.Status = RecordingStatus.toKey status)
            if not ok then logger.LogInformation("Recording {Id} changed meanwhile (deleted?) - step result dropped", id)
            return ok
        }

    let deleteJob (jobId: string) =
        task {
            try
                do! speechmatics.Delete jobId
                return true
            with ex ->
                logger.LogWarning("Could not delete Speechmatics job {JobId}: {Error}", jobId, describe ex)
                return false
        }

    /// Marks the recording failed in `step`; retry returns it there (see CommandHandler).
    member _.Fail(r: RecordingRow, step: RecordingStatus, ex: exn) =
        task {
            let message = describe ex
            logger.LogWarning("Recording {Id} failed in {Step}: {Error}", r.Id, RecordingStatus.toKey step, message)
            let! current = recordings.TryGet r.Id
            match current with
            | Some row when row.Status <> RecordingStatus.toKey Deleted ->
                do! recordings.Update { row with Status = RecordingStatus.toKey Failed; FailedStep = Some(RecordingStatus.toKey step); Error = Some message }
            | _ -> ()
        }

    /// queued -> transcribing: uploads the locally stored audio (downloaded first if still missing).
    member _.StartTranscription(r: RecordingRow) =
        task {
            let! audioPath = audio.Ensure(r.Id, RecordingSource.fromKey r.Source)
            let! jobId = speechmatics.Submit audioPath
            logger.LogInformation("Recording {Id}: Speechmatics job {JobId} submitted", r.Id, jobId)
            let! ok = stillIn r.Id Queued
            if ok then do! recordings.Update { setStatus r Transcribing with SpeechmaticsJobId = Some jobId }
            else
                let! _ = deleteJob jobId
                ()
        }

    /// transcribing -> summarizing, once the job is done: store transcript, then delete the job.
    member this.CheckTranscription(r: RecordingRow) =
        task {
            match r.SpeechmaticsJobId with
            | None -> do! this.Fail(r, Queued, exn "no Speechmatics job id")
            | Some jobId ->
                let! status = speechmatics.GetStatus jobId
                match status with
                | JobStatus.Running ->
                    let started = r.StepStartedAt |> Option.defaultValue r.UpdatedAt
                    if Db.now () - started > int64 cfg.Speechmatics.MaxJobMinutes * 60_000L then
                        let! _ = deleteJob jobId
                        do! this.Fail({ r with SpeechmaticsJobId = None }, Queued, exn $"Speechmatics job {jobId} did not finish within {cfg.Speechmatics.MaxJobMinutes} min")
                | JobStatus.Failed message ->
                    do! this.Fail({ r with SpeechmaticsJobId = None }, Queued, exn $"Speechmatics {message}")
                | JobStatus.Done ->
                    let! segments = speechmatics.GetSegments jobId
                    if segments.IsEmpty then
                        let! _ = deleteJob jobId
                        do! this.Fail({ r with SpeechmaticsJobId = None }, Queued, exn "Speechmatics returned an empty transcript")
                    else
                        let transcript = JsonSerializer.Serialize(segments, Serialization.options)
                        let row = { setStatus r Summarizing with Transcript = Some transcript }
                        let! ok = stillIn r.Id Transcribing
                        if ok then
                            do! recordings.Update row
                            logger.LogInformation("Recording {Id}: transcript stored ({Segments} segments)", r.Id, segments.Length)
                        let! deleted = deleteJob jobId
                        if ok && deleted then do! recordings.Update { row with SpeechmaticsJobId = None }
        }

    /// summarizing -> done: title + ensemble passes + merge via the Claude bridge, then the result file.
    member _.Summarize(r: RecordingRow) =
        task {
            let workflow =
                match r.Workflow |> Option.bind workflows.TryLoad with
                | Some n -> n
                | None -> failwith $"Workflow '{defaultArg r.Workflow String.Empty}' no longer exists in workflows/"
            let segments =
                match r.Transcript with
                | Some t -> JsonSerializer.Deserialize<Segment list>(t, Serialization.options)
                | None -> failwith "no stored transcript"
            let text = transcriptText segments
            let claude = workflow.Claude
            logger.LogInformation("Recording {Id}: summarizing with workflow {Workflow} ({Model}, effort {Effort}, {Passes} passes, {Chars} chars)",
                                  r.Id, workflow.Name, claude.Model, claude.Effort, claude.Passes, text.Length)
            let run (system: string) (prompt: string) (schema: string option) =
                bridge.Run(system, prompt, claude.Model, claude.Effort, claude.TimeoutSeconds, schema)
            // Passes run in parallel; beyond the bridge's MAX_CONCURRENCY they queue, and the queueing
            // time counts against the timeout.
            let! maxConcurrency = bridge.GetMaxConcurrency()
            match maxConcurrency with
            | Some m when claude.Passes > m ->
                logger.LogWarning("Workflow {Workflow} runs {Passes} passes but the bridge allows only {Max} at once (MAX_CONCURRENCY) - passes will queue and may time out",
                                  workflow.Name, claude.Passes, m)
            | _ -> ()

            let isValid (n: JsonNode) = not (isNull n) && (validate workflow.Schema n |> Result.isOk)
            let passRuns = [ for _ in 1 .. claude.Passes -> attempt (fun () -> run workflow.SummaryPrompt text (Some workflow.Schema)) ]
            let! passes = Task.WhenAll passRuns

            let good = passes |> Array.choose (function Ok p when isValid p.StructuredOutput -> Some p.StructuredOutput | _ -> None)
            if good.Length = 0 then
                let reason =
                    passes |> Array.tryPick (function Error e -> Some(describe e) | _ -> None)
                    |> Option.defaultValue "no pass returned a result matching the schema"
                failwith $"all {passes.Length} summary passes failed: {reason}"
            let richest () = good |> Array.maxBy (fun n -> n.ToJsonString().Length)
            let! content =
                task {
                    if good.Length = 1 then return good[0]
                    else
                        let versions = good |> Array.mapi (fun i n -> $"Version {i + 1}:\n{n.ToJsonString()}") |> String.concat "\n\n"
                        let! merged = attempt (fun () -> run workflow.MergePrompt versions (Some workflow.Schema))
                        match merged with
                        | Ok m when isValid m.StructuredOutput -> return m.StructuredOutput
                        | Ok _ ->
                            logger.LogWarning("Recording {Id}: merge result does not match the schema, using the richest pass", r.Id)
                            return richest ()
                        | Error e ->
                            logger.LogWarning("Recording {Id}: merge failed ({Error}), using the richest pass", r.Id, describe e)
                            return richest ()
                }
            // The title is generated from the finished summary (not the raw transcript), together
            // with the recording's local date/time - the transcript does not contain it.
            let recordedAt =
                let offset = TimeSpan.FromMinutes(float (defaultArg r.UtcOffsetMinutes 0L))
                DateTimeOffset.FromUnixTimeMilliseconds(r.StartTime).ToOffset(offset)
            let titleInput =
                let summaryJson = content.ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
                String.concat "\n" [
                    // e.g. "Recorded: 2026-09-30 11:26 (UTC+02:00)"
                    "Recorded: " + recordedAt.ToString("yyyy-MM-dd HH:mm '(UTC'zzz')'", Globalization.CultureInfo.InvariantCulture)
                    $"Duration: {r.DurationMs / 60000L} min"
                    ""
                    "Summary:"
                    summaryJson
                ]
            let! title = attempt (fun () -> run workflow.TitlePrompt titleInput None)
            let finalTitle =
                match title with
                | Ok t -> cleanTitle t.Result |> Option.defaultValue r.Filename
                | Error e ->
                    logger.LogWarning("Recording {Id}: title run failed ({Error}), using the filename", r.Id, describe e)
                    r.Filename

            // Fixed envelope; only `content` varies by workflow (its schema).
            let envelope = JsonObject()
            envelope["title"] <- JsonValue.Create finalTitle
            envelope["workflow"] <- JsonValue.Create workflow.Name
            envelope["sourceName"] <- JsonValue.Create r.Source
            envelope["sourceId"] <- JsonValue.Create r.Id
            envelope["recordedAt"] <- JsonValue.Create(recordedAt.ToString "o")
            envelope["durationSeconds"] <- JsonValue.Create(r.DurationMs / 1000L)
            envelope["processedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString "o")
            envelope["content"] <- JsonNode.Parse(content.ToJsonString())

            let! ok = stillIn r.Id Summarizing
            if ok then
                Directory.CreateDirectory cfg.Paths.Output |> ignore
                let fileName = $"loopback-{r.Id}.json"
                let path = Path.Combine(cfg.Paths.Output, fileName)
                let temp = path + ".tmp"
                do! File.WriteAllTextAsync(temp, envelope.ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)))
                File.Move(temp, path, true)
                logger.LogInformation("Recording {Id}: wrote {File}", r.Id, fileName)

                // Tombstone: done recordings are never re-synced from Plaud (harmless for uploads).
                do! recordings.Update { setStatus r Done with Title = Some finalTitle; OutputFile = Some fileName; Result = Some(envelope.ToJsonString()); DeletedAt = Some(Db.now ()) }
        }
