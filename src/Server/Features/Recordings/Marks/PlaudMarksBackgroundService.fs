/// Imports the marks made in Plaud during a call (docs/attachments.md, Plaud Marks): photos become
/// attachments; highlights, text notes and voice memos (transcribed by Speechmatics, audio not kept) become marks. Imported once - then they belong
/// to Loopback, and one deleted there is not imported again. The only later update: a highlight
/// imported while Plaud was still analyzing it gets its text (unless the user edited the note).
module Loopback.Server.Features.Recordings.Marks.PlaudMarksBackgroundService

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Loopback.Server
open Loopback.Server.Configuration
open Loopback.Server.Integrations.Plaud
open Loopback.Server.Integrations.Speechmatics
open Loopback.Server.Features.Recordings.Domain
open Loopback.Server.Features.Recordings.Database
open Loopback.Server.Features.Recordings.Sync.PlaudSession
open Loopback.Server.Features.Recordings.Processing.Pipeline

/// Highlights still analyzing in Plaud are fetched again this often...
let private recheckPendingEvery = TimeSpan.FromMinutes 5.
/// ...for this long after the recording started.
let private pendingWindow = TimeSpan.FromHours 24.
/// A recording whose import failed is retried only after this long.
let private retryAfter = TimeSpan.FromMinutes 30.
/// Voice memos are seconds long; Speechmatics needs a few seconds for them.
let private transcriptionTimeout = TimeSpan.FromMinutes 3.

type PlaudMarksBackgroundService(
    recordings: RecordingsRepository,
    marks: MarksRepository,
    session: PlaudSession,
    plaud: PlaudClient,
    speechmatics: SpeechmaticsClient,
    scopes: IServiceScopeFactory,
    cfg: Configuration,
    logger: ILogger<PlaudMarksBackgroundService>) =
    inherit BackgroundService()

    let failedAt = Dictionary<string, DateTimeOffset>()

    let isDue (now: DateTimeOffset) (c: PlaudMarksCandidateRow) =
        let failedRecently =
            match failedAt.TryGetValue c.Id with
            | true, at -> now - at < retryAfter
            | _ -> false
        let checkedAt = c.PlaudMarksCheckedAt |> Option.map DateTimeOffset.FromUnixTimeMilliseconds
        let pendingDue =
            c.PendingMarks > 0L
            && now - DateTimeOffset.FromUnixTimeMilliseconds c.StartTime < pendingWindow
            && checkedAt |> Option.forall (fun at -> now - at >= recheckPendingEvery)
        not failedRecently && (c.PlaudMarksVersion <> Some c.VersionMs || pendingDue)

    /// The memo's text by Speechmatics (same model / language as recordings); None if it fails or times out.
    let transcribe (recordingId: string) (path: string) =
        task {
            try
                let! jobId = speechmatics.Submit path
                try
                    let deadline = DateTimeOffset.UtcNow + transcriptionTimeout
                    let mutable result = None
                    let mutable go = true
                    while go do
                        do! Task.Delay(TimeSpan.FromSeconds 2.)
                        match! speechmatics.GetStatus jobId with
                        | JobStatus.Running when DateTimeOffset.UtcNow < deadline -> ()
                        | JobStatus.Running -> raise (SpeechmaticsError "voice memo transcription timed out")
                        | JobStatus.Failed message -> raise (SpeechmaticsError message)
                        | JobStatus.Done ->
                            let! segments = speechmatics.GetSegments jobId
                            result <- segments |> List.map _.Text |> String.concat " " |> (fun t -> t.Trim()) |> Option.ofObj |> Option.filter ((<>) "")
                            go <- false
                    return result
                finally
                    try speechmatics.Delete(jobId).Wait() with _ -> ()
            with ex ->
                logger.LogWarning("Recording {Id}: voice memo transcription failed, keeping Plaud's text ({Error})", recordingId, describe ex)
                return None
        }

    /// The voice memo's text: Speechmatics' transcript of its audio, else Plaud's. The audio is not kept.
    let transcribeMemo (c: PlaudMarksCandidateRow) (m: PlaudMark) =
        task {
            match m.FileUrl with
            | None -> return m.Content
            | Some url ->
                // Speechmatics detects the format from the name.
                let temp = Path.Combine(Path.GetTempPath(), $"loopback-memo-{Guid.NewGuid():N}.ogg")
                try
                    do! plaud.DownloadMarkFile(url, temp)
                    let! text = transcribe c.Id temp
                    return text |> Option.orElse m.Content
                finally
                    if File.Exists temp then File.Delete temp
        }

    /// Downloads the mark's file (a photo) and adds it as an attachment; returns the attachment id.
    let importFile (c: PlaudMarksCandidateRow) (m: PlaudMark) (url: string) =
        task {
            let temp = Path.Combine(Path.GetTempPath(), $"loopback-mark-{Guid.NewGuid():N}")
            try
                do! plaud.DownloadMarkFile(url, temp)
                use scope = scopes.CreateScope()
                let handler = scope.ServiceProvider.GetRequiredService<RecordingsCommandHandler>()
                let! events =
                    handler.Handle(AddAttachment {
                        RecordingId = c.Id
                        File = temp
                        FileName = m.FilePath |> Option.map Path.GetFileName |> Option.defaultValue $"plaud-mark-{m.OffsetMs}"
                        FileModifiedAt = None
                        Source = FromPlaudMark
                        ReportedCapturedAt = Some(DateTimeOffset.FromUnixTimeMilliseconds(c.StartTime + m.OffsetMs))
                        Description = m.Content
                    })
                return
                    events
                    |> List.pick (function
                        | AttachmentAdded e | AttachmentAlreadyExists e -> Some e.AttachmentId
                        | _ -> None)
            finally
                if File.Exists temp then File.Delete temp
        }

    let importRecording (c: PlaudMarksCandidateRow) =
        task {
            let! found = session.Use(fun s -> plaud.GetMarks(s.ApiBase, s.WorkspaceToken, c.Id))
            let! imports = marks.GetImports c.Id
            let known = imports |> List.map (fun i -> i.MarkKey, i) |> Map.ofList
            let mutable changed = 0
            let remember (m: PlaudMark) (targetId: string) (pending: bool) =
                marks.InsertImport { RecordingId = c.Id; MarkKey = m.Key; TargetId = targetId; Pending = (if pending then 1L else 0L); ImportedAt = Db.now () }
            for m in found do
                match known.TryFind m.Key with
                // Plaud finished analyzing a highlight imported without text.
                | Some i when i.Pending <> 0L && not m.Pending ->
                    let! row = marks.TryGet i.TargetId
                    match row with
                    | Some mark when mark.UserEdited = 0L ->
                        do! marks.Update { mark with Note = m.Content; Pending = 0L }
                        changed <- changed + 1
                    | _ -> ()
                    do! marks.ResolveImport(c.Id, m.Key)
                | Some _ -> ()
                | None when m.Deleted -> ()
                | None ->
                    let addMark (kind: MarkKind) (note: string option) =
                        task {
                            let id = Guid.NewGuid().ToString "N"
                            let now = Db.now ()
                            do! marks.Insert {
                                Id = id
                                RecordingId = c.Id
                                OffsetMs = m.OffsetMs
                                Note = note
                                Source = ItemSource.toKey FromPlaudMark
                                Pending = (if m.Pending then 1L else 0L)
                                UserEdited = 0L
                                CreatedAt = now
                                UpdatedAt = now
                                Kind = MarkKind.toKey kind
                            }
                            do! remember m id m.Pending
                            changed <- changed + 1
                        }
                    match m.Kind with
                    | Highlight -> do! addMark HighlightMark m.Content
                    | TextNote -> do! addMark NoteMark m.Content
                    | VoiceMemo ->
                        let! note = transcribeMemo c m
                        do! addMark VoiceMark note
                    | Photo ->
                        match m.FileUrl with
                        // Fails the recording: retried later, already imported marks are skipped then.
                        | None -> raise (PlaudError $"mark {m.Key} has no download URL for {m.FilePath}")
                        | Some url ->
                            let! attachmentId = importFile c m url
                            do! remember m attachmentId false
                            changed <- changed + 1
                    | UnknownMark n ->
                        logger.LogWarning("Recording {Id}: Plaud mark {Key} has an unknown mark_type {Type} - skipped", c.Id, m.Key, n)
                        do! remember m "" false
            do! recordings.SetPlaudMarksChecked(c.Id, c.VersionMs)
            if changed > 0 then
                do! recordings.TouchAttachments c.Id
                logger.LogInformation("Recording {Id}: {Count} Plaud marks imported / updated", c.Id, changed)
        }

    let processNext () =
        task {
            let! candidates = recordings.GetPlaudMarksCandidates()
            let now = DateTimeOffset.UtcNow
            match candidates |> List.tryFind (isDue now) with
            | None -> return false
            | Some c ->
                try
                    do! importRecording c
                    failedAt.Remove c.Id |> ignore
                with ex ->
                    failedAt[c.Id] <- DateTimeOffset.UtcNow
                    logger.LogWarning("Recording {Id}: Plaud marks import failed ({Error})", c.Id, describe ex)
                return true
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    // Drain the backlog, then wait for the next poll.
                    let mutable more = true
                    while more && not stoppingToken.IsCancellationRequested do
                        let! worked = processNext ()
                        more <- worked
                with ex ->
                    logger.LogError(ex, "Plaud marks loop failed")
                try
                    do! Task.Delay(TimeSpan.FromSeconds(float cfg.Processing.PollSeconds), stoppingToken)
                with :? OperationCanceledException -> ()
        } :> Task
