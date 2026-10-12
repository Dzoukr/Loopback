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
open Loopback.Server.Features.Recordings.Attachments.AttachmentStore
open Loopback.Server.Features.Recordings.Processing.Workflows
open Loopback.Server.Features.Recordings.Processing.Moments

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

/// Only files Claude can read are described; bigger ones go in by name (the bridge takes 40 MB of base64).
let private maxDescribeBytes = 20L * 1024L * 1024L

/// First non-empty line of a title run, without surrounding quotes / markdown.
let private cleanTitle (text: string) =
    text.Split('\n')
    |> Array.map (fun l -> l.Trim().TrimStart('#').Trim().Trim('"', '\'', '*').Trim())
    |> Array.tryFind ((<>) "")
    |> Option.map (fun t -> if t.Length > 200 then t.Substring(0, 200) else t)

type Pipeline(
    recordings: RecordingsRepository,
    attachments: AttachmentsRepository,
    marks: MarksRepository,
    audio: AudioStore,
    attachmentFiles: AttachmentStore,
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

    /// Describes the attachments Claude can read (image / pdf / text) that this describe model has not
    /// described yet - from the cache by file content when possible. Returns attachment id -> description.
    /// A failure leaves the attachment as it is (it goes in by name, note and any earlier description).
    let describeAttachments (r: RecordingRow) (workflow: Workflow) (items: ExportAttachment list) (languageSample: string) =
        task {
            let model, effort = workflow.Claude.DescribeModel, workflow.Claude.DescribeEffort
            let describable (a: ExportAttachment) =
                (a.Row.Kind = "image" || a.Row.Kind = "pdf" || a.Row.Kind = "text")
                && a.Row.SizeBytes <= maxDescribeBytes
                && a.Row.DescriptionModel <> Some model
            let describeOne (a: ExportAttachment) =
                task {
                    try
                        let! cached = attachments.TryGetCachedDescription(a.Row.Sha256, model)
                        let! description =
                            match cached with
                            | Some d -> Task.FromResult d
                            | None ->
                                task {
                                    let! bytes = File.ReadAllBytesAsync(attachmentFiles.PathOf(r.Id, a.Row.Id))
                                    let prompt =
                                        String.concat "\n" [
                                            $"File: {a.ExportFileName} ({a.Row.Kind}, original name: {a.Row.FileName})"
                                            match a.Row.Note with
                                            | Some note -> $"User's note: {note}"
                                            | None -> ()
                                            ""
                                            "Conversation sample (for the language):"
                                            languageSample
                                        ]
                                    let! run = bridge.Run(workflow.DescribePrompt, prompt, model, effort, workflow.Claude.TimeoutSeconds, None, [ a.ExportFileName, bytes ])
                                    let text = run.Result.Trim()
                                    if text <> "" then do! attachments.CacheDescription(a.Row.Sha256, model, text)
                                    return text
                                }
                        if description = "" then return None
                        else
                            do! attachments.SetDescription(a.Row.Id, description, model)
                            return Some(a.Row.Id, description)
                    with ex ->
                        logger.LogWarning("Recording {Id}: describing attachment {File} failed ({Error}) - it goes in by name", r.Id, a.Row.FileName, describe ex)
                        return None
                }
            let! supported = bridge.SupportsFiles()
            let todo = if supported then items |> List.filter describable else []
            if not supported && items |> List.exists describable then
                logger.LogWarning("Recording {Id}: the Claude bridge does not accept files (update claude-bridge.fsx) - attachments go in without descriptions", r.Id)
            if not todo.IsEmpty then
                logger.LogInformation("Recording {Id}: describing {Count} attachments ({Model})", r.Id, todo.Length, model)
            // In parallel; the bridge's MAX_CONCURRENCY queues them.
            let! described = todo |> List.map describeOne |> Task.WhenAll
            return described |> Array.choose id |> Map.ofArray
        }

    /// Copies the attachment files next to the result: <output>/loopback-{id}.files/a1.png ...
    let exportFiles (r: RecordingRow) (folder: string) (items: ExportAttachment list) =
        if not items.IsEmpty then
            let dir = Path.Combine(cfg.Paths.Output, folder)
            Directory.CreateDirectory dir |> ignore
            for a in items do
                let source = attachmentFiles.PathOf(r.Id, a.Row.Id)
                if File.Exists source then File.Copy(source, Path.Combine(dir, a.ExportFileName), true)
                else logger.LogWarning("Recording {Id}: attachment file of {File} is missing - not exported", r.Id, a.Row.FileName)

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
            let localOffset = TimeSpan.FromMinutes(float (defaultArg r.UtcOffsetMinutes 0L))
            let start = DateTimeOffset.FromUnixTimeMilliseconds r.StartTime
            let duration = TimeSpan.FromMilliseconds(float r.DurationMs)
            // Snapshot of marks and attachments as they are now; later changes make the result outdated.
            let! markRows = marks.GetByRecording r.Id
            let! attachmentRows = attachments.GetByRecording r.Id
            let exportMarks, exportAttachments = assignIds start duration segments markRows attachmentRows
            let languageSample =
                let speech = segments |> List.map (fun s -> s.Text) |> String.concat " "
                if speech.Length > 600 then speech.Substring(0, 600) else speech
            let! described = describeAttachments r workflow exportAttachments languageSample
            let exportAttachments =
                exportAttachments
                |> List.map (fun a ->
                    match described.TryFind a.Row.Id with
                    | Some d -> { a with Row = { a.Row with Description = Some d; DescriptionModel = Some workflow.Claude.DescribeModel } }
                    | None -> a)
            // The date lets the passes resolve deadlines like "by Friday".
            let text =
                let recorded = start.ToOffset(localOffset).ToString("yyyy-MM-dd (dddd)", Globalization.CultureInfo.InvariantCulture)
                $"Recorded: {recorded}\n\n" + transcriptWithMoments segments exportMarks exportAttachments
            let claude = workflow.Claude
            logger.LogInformation("Recording {Id}: summarizing with workflow {Workflow} ({Model}, effort {Effort}, {Passes} passes, {Chars} chars, {Marks} marks, {Attachments} attachments)",
                                  r.Id, workflow.Name, claude.Model, claude.Effort, claude.Passes, text.Length, exportMarks.Length, exportAttachments.Length)
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
            // The topics the result assigned notes / attachments to go into the envelope's lists.
            let content = JsonNode.Parse(content.ToJsonString())
            let exportIds = (exportMarks |> List.map _.ExportId) @ (exportAttachments |> List.map _.ExportId) |> Set.ofList
            let topics, invalid = assignTopics content exportIds
            if invalid > 0 then
                logger.LogWarning("Recording {Id}: {Count} topic assignments of notes / attachments referred to unknown ids or topics - left without a topic", r.Id, invalid)
            // The title is generated from the finished summary (not the raw transcript), together
            // with the recording's local date/time - the transcript does not contain it.
            let recordedAt = start.ToOffset localOffset
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

            // Fixed envelope; only `content` varies by workflow (its schema). Format 2 = with marks,
            // attachments and their files (docs/attachments-processing.md).
            let filesFolder = $"loopback-{r.Id}.files"
            let envelope = JsonObject()
            envelope["format"] <- JsonValue.Create 2
            envelope["title"] <- JsonValue.Create finalTitle
            envelope["workflow"] <- JsonValue.Create workflow.Name
            envelope["sourceName"] <- JsonValue.Create r.Source
            envelope["sourceId"] <- JsonValue.Create r.Id
            envelope["recordedAt"] <- JsonValue.Create(recordedAt.ToString "o")
            envelope["durationSeconds"] <- JsonValue.Create(r.DurationMs / 1000L)
            envelope["processedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString "o")
            envelope["marks"] <- marksJson exportMarks topics start localOffset
            envelope["attachments"] <- attachmentsJson exportAttachments topics filesFolder localOffset
            envelope["content"] <- content

            let! ok = stillIn r.Id Summarizing
            if ok then
                Directory.CreateDirectory cfg.Paths.Output |> ignore
                exportFiles r filesFolder exportAttachments
                let fileName = $"loopback-{r.Id}.json"
                let path = Path.Combine(cfg.Paths.Output, fileName)
                let temp = path + ".tmp"
                do! File.WriteAllTextAsync(temp, envelope.ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)))
                File.Move(temp, path, true)
                logger.LogInformation("Recording {Id}: wrote {File}", r.Id, fileName)

                // Tombstone: done recordings are never re-synced from Plaud (harmless for uploads).
                do! recordings.Update { setStatus r Done with Title = Some finalTitle; OutputFile = Some fileName; Result = Some(envelope.ToJsonString()); DeletedAt = Some(Db.now ()) }
        }
