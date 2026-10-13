module Loopback.Server.Features.Recordings.Queries

open System
open System.Text.Json
open Loopback.Server
open Loopback.Server.Integrations.Speechmatics
open Loopback.Server.Features.Recordings.Domain
open Loopback.Server.Features.Recordings.Database
open Loopback.Server.Features.Recordings.Sync.PlaudSession
open Loopback.Server.Features.Recordings.Audio.AudioStore
open Loopback.Server.Features.Recordings.Processing.Workflows
open Loopback.Server.Features.Recordings.Attachments
open Loopback.Server.Features.Recordings.Attachments.AttachmentStore

let private toDate (ms: int64) = DateTimeOffset.FromUnixTimeMilliseconds ms

let private toAttachment (r: RecordingRow) (a: AttachmentRow) : Queries.Attachment =
    let capturedAt = a.CapturedAt |> Option.map toDate
    {
        Id = a.Id
        RecordingId = a.RecordingId
        FileName = a.FileName
        ContentType = a.ContentType
        Kind = AttachmentKind.fromKey a.Kind
        SizeBytes = a.SizeBytes
        CapturedAt = capturedAt
        TimeSource = TimeSource.fromKey a.TimeSource
        Note = a.Note
        Source = ItemSource.fromKey a.Source
        Description = a.Description
        Placement = CaptureTime.placement (toDate r.StartTime) (TimeSpan.FromMilliseconds(float r.DurationMs)) capturedAt
        CreatedAt = toDate a.CreatedAt
    }

let private toMark (r: RecordingRow) (m: MarkRow) : Queries.Mark =
    {
        Id = m.Id
        RecordingId = m.RecordingId
        Kind = MarkKind.fromKey m.Kind
        OffsetSeconds = m.OffsetMs |> Option.map (fun ms -> Math.Clamp(float ms / 1000., 0., max 0. (float r.DurationMs / 1000.)))
        Note = m.Note
        Source = ItemSource.fromKey m.Source
        Pending = m.Pending <> 0L
    }

/// During call by offset, then reference by file name.
let private ordered (attachments: Queries.Attachment list) =
    attachments
    |> List.sortBy (fun a ->
        match a.Placement with
        | DuringCall offset -> 0, offset, ""
        | Reference -> 1, 0., a.FileName.ToLowerInvariant())

/// The result envelope's processedAt (unix ms), None when there is no (parsable) result.
let private processedAt (result: string option) =
    result
    |> Option.bind (fun json ->
        try
            use doc = JsonDocument.Parse json
            match doc.RootElement.TryGetProperty "processedAt" with
            | true, p when p.ValueKind = JsonValueKind.String ->
                match DateTimeOffset.TryParse(p.GetString()) with
                | true, t -> Some(t.ToUnixTimeMilliseconds())
                | _ -> None
            | _ -> None
        with :? JsonException -> None)

let private toRecording (attachments: AttachmentRow list) (marks: MarkRow list) (attachmentsChangedAt: int64 option) (r: RecordingRow) : Queries.Recording =
    {
        Id = r.Id
        Filename = r.Filename
        Source = RecordingSource.fromKey r.Source
        StartTime = toDate r.StartTime
        DurationMs = r.DurationMs
        Status = RecordingStatus.fromKey r.Status
        Workflow = r.Workflow
        Title = r.Title
        Error = r.Error
        FailedStep = r.FailedStep |> Option.map RecordingStatus.fromKey
        OutputFile = r.OutputFile
        UpdatedAt = toDate r.UpdatedAt
        Peaks = r.Peaks |> Option.map (fun p -> System.Text.Json.JsonSerializer.Deserialize<float[]> p)
        Result = r.Result
        CanReprocess = r.Status = RecordingStatus.toKey Done && r.Transcript.IsSome
        HasTranscript = r.Transcript.IsSome
        Attachments = attachments |> List.map (toAttachment r) |> ordered
        AttachmentsOutdated =
            r.Status = RecordingStatus.toKey Done
            && match attachmentsChangedAt, processedAt r.Result with
               | Some changed, Some processed -> changed > processed
               | _ -> false
        Marks =
            marks
            |> List.sortBy (fun m -> m.OffsetMs.IsNone, m.OffsetMs, m.CreatedAt)
            |> List.map (toMark r)
    }

/// SQLite-backed implementation of RecordingsQueries
type StorageQueries(recordings: RecordingsRepository, attachments: AttachmentsRepository, marks: MarksRepository, workflows: WorkflowCatalog, session: PlaudSession, audio: AudioStore, attachmentFiles: AttachmentStore) =

    interface RecordingsQueries with
        member _.GetRecordings() =
            task {
                let! rows = recordings.GetAll()
                let! all = attachments.GetAll()
                let! allMarks = marks.GetAll()
                let! changedAt = recordings.GetAttachmentsChangedAt()
                let byRecording = all |> List.groupBy (fun a -> a.RecordingId) |> Map.ofList
                let marksByRecording = allMarks |> List.groupBy (fun m -> m.RecordingId) |> Map.ofList
                return
                    rows
                    |> List.filter (fun r -> r.Status <> RecordingStatus.toKey Deleted)
                    |> List.map (fun r ->
                        let of' (m: Map<string, 'T list>) = m.TryFind r.Id |> Option.defaultValue []
                        toRecording (of' byRecording) (of' marksByRecording) (changedAt.TryFind r.Id) r)
            }

        member _.GetWorkflows() =
            task {
                return workflows.List() |> List.map (fun n -> { Name = n.Name; HasCustomSchema = n.HasCustomSchema } : Queries.Workflow)
            }

        member _.GetSyncStatus() =
            task {
                let! row = session.GetConnection()
                let account = if session.Account = "" then None else Some session.Account
                return
                    match row with
                    | None -> ({ Connected = false; LastSyncAt = None; LastError = session.LastError; Account = account } : Domain.Queries.SyncStatus)
                    | Some c ->
                        let lastError = session.LastError |> Option.orElse c.LastError
                        ({
                            Connected = lastError.IsNone
                            LastSyncAt = c.LastSyncAt |> Option.map toDate
                            LastError = lastError
                            Account = account
                        } : Domain.Queries.SyncStatus)
            }

        member _.GetAudioFile(recordingId: string) =
            task {
                let! row = recordings.TryGet recordingId
                match row with
                | Some r when r.Status <> RecordingStatus.toKey Deleted ->
                    let! path = audio.Ensure(r.Id, RecordingSource.fromKey r.Source)
                    return Some path
                | _ -> return None
            }

        member _.GetTranscript(recordingId: string) =
            task {
                let! row = recordings.TryGet recordingId
                return
                    match row |> Option.bind (fun r -> r.Transcript) with
                    | Some t ->
                        JsonSerializer.Deserialize<Segment list>(t, Serialization.options)
                        |> List.map (fun s -> ({ Speaker = s.Speaker; Start = s.Start; End = s.End; Text = s.Text } : Domain.Queries.TranscriptSegment))
                    | None -> []
            }

        member _.GetAttachmentFile(recordingId: string, attachmentId: string) =
            task {
                let! recording = recordings.TryGet recordingId
                let! attachment = attachments.TryGet attachmentId
                match recording, attachment with
                | Some r, Some a when r.Status <> RecordingStatus.toKey Deleted && a.RecordingId = r.Id ->
                    let path = attachmentFiles.PathOf(r.Id, a.Id)
                    return
                        if IO.File.Exists path then
                            Some({ Path = path; FileName = a.FileName; ContentType = a.ContentType; Kind = AttachmentKind.fromKey a.Kind } : Domain.Queries.AttachmentFile)
                        else None
                | _ -> return None
            }
