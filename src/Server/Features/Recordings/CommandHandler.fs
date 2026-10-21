module Loopback.Server.Features.Recordings.CommandHandler

open System
open System.IO
open Loopback.Server
open Loopback.Server.Integrations.Speechmatics
open Loopback.Server.Integrations.AudioFiles
open Loopback.Server.Features.Recordings.Domain
open Loopback.Server.Features.Recordings.Database
open Loopback.Server.Features.Recordings.Sync.SyncTrigger
open Loopback.Server.Features.Recordings.Audio.AudioStore
open Loopback.Server.Features.Recordings.Processing.Workflows
open Loopback.Server.Features.Recordings.Attachments
open Loopback.Server.Features.Recordings.Attachments.AttachmentStore

/// SQLite-backed implementation of RecordingsCommandHandler. Commands only change the
/// recording's status; the background services do the actual work.
type StorageCommandHandler(
    recordings: RecordingsRepository,
    attachments: AttachmentsRepository,
    marks: MarksRepository,
    workflows: WorkflowCatalog,
    syncTrigger: SyncTrigger,
    speechmatics: SpeechmaticsClient,
    audio: AudioStore,
    attachmentFiles: AttachmentStore) =

    let getLive (id: string) =
        task {
            let! row = recordings.TryGet id
            match row with
            | Some r when r.DeletedAt.IsNone -> return r
            | Some _ -> return failwith $"Recording {id} was already processed"
            | None -> return failwith $"Recording {id} not found"
        }

    let processRecording (args: CommandArgs.ProcessRecording) =
        task {
            if (workflows.TryLoad args.Workflow).IsNone then failwith $"Unknown workflow '{args.Workflow}'"
            let! r = getLive args.RecordingId
            if r.Status <> RecordingStatus.toKey Synced then
                failwith $"Recording {r.Id} is already {r.Status}"
            do! recordings.Update { r with Status = RecordingStatus.toKey Queued; Workflow = Some args.Workflow; Error = None; FailedStep = None }
            return [ RecordingQueued { RecordingId = r.Id; Workflow = args.Workflow } ]
        }

    let retryRecording (args: CommandArgs.RetryRecording) =
        task {
            let! r = getLive args.RecordingId
            if r.Status <> RecordingStatus.toKey Failed then
                failwith $"Recording {r.Id} is {r.Status}, only failed recordings can be retried"
            // Return to the failed step; a transcription without a Speechmatics job starts over,
            // a stored transcript is never transcribed (and paid for) again.
            let step =
                match r.FailedStep |> Option.map RecordingStatus.fromKey with
                | Some Summarizing when r.Transcript.IsSome -> Summarizing
                | Some Transcribing when r.SpeechmaticsJobId.IsSome -> Transcribing
                | _ when r.Transcript.IsSome -> Summarizing
                | _ -> Queued
            do! recordings.Update { r with Status = RecordingStatus.toKey step; Error = None; FailedStep = None; StepStartedAt = None }
            return [ RecordingRetried { RecordingId = r.Id; Step = step } ]
        }

    /// Runs the workflow again on the stored transcript - no transcription (and no Speechmatics cost).
    let reprocessRecording (args: CommandArgs.ReprocessRecording) =
        task {
            if (workflows.TryLoad args.Workflow).IsNone then failwith $"Unknown workflow '{args.Workflow}'"
            let! row = recordings.TryGet args.RecordingId
            match row with
            | None -> return failwith $"Recording {args.RecordingId} not found"
            | Some r when r.Status <> RecordingStatus.toKey Done -> return failwith $"Recording {r.Id} is {r.Status}, only processed recordings can be reprocessed"
            | Some r when r.Transcript.IsNone -> return failwith $"Recording {r.Id} has no stored transcript - it cannot be reprocessed"
            | Some r ->
                // The tombstone is cleared while it runs (the summarizing step picks up live rows only);
                // the step sets it again when done.
                do! recordings.Update { r with Status = RecordingStatus.toKey Summarizing; Workflow = Some args.Workflow; Error = None; FailedStep = None; StepStartedAt = Some(Db.now ()); DeletedAt = None }
                return [ RecordingReprocessQueued { RecordingId = r.Id; Workflow = args.Workflow } ]
        }

    /// Stores the uploaded MP3 / Ogg file as-is in the audio store and adds it as a new recording
    /// (queued right away when a workflow is given). The waveform is computed by the audio job.
    let uploadRecording (args: CommandArgs.UploadRecording) =
        task {
            match args.Workflow with
            | Some w when (workflows.TryLoad w).IsNone -> failwith $"Unknown workflow '{w}'"
            | _ -> ()
            let format =
                match detect args.AudioFile with
                | Some f -> f
                | None -> raise (UnsupportedAudio "not an MP3 or Ogg (Vorbis / Opus) file")
            let length = duration args.AudioFile format
            let id = Guid.CreateVersion7().ToString "N"
            let path = audio.Import(id, args.AudioFile, format)
            try
                let now = Db.now ()
                do! recordings.Insert {
                    Id = id
                    Filename = args.Filename
                    StartTime = args.StartTime.ToUnixTimeMilliseconds()
                    DurationMs = int64 length.TotalMilliseconds
                    Filesize = FileInfo(path).Length
                    VersionMs = ""
                    Status = RecordingStatus.toKey (if args.Workflow.IsSome then Queued else Synced)
                    Workflow = args.Workflow
                    Title = None
                    Error = None
                    FailedStep = None
                    SpeechmaticsJobId = None
                    Transcript = None
                    OutputFile = None
                    StepStartedAt = None
                    CreatedAt = now
                    UpdatedAt = now
                    DeletedAt = None
                    Peaks = None
                    UtcOffsetMinutes = Some(int64 args.UtcOffsetMinutes)
                    Result = None
                    Source = RecordingSource.toKey Upload
                }
            with ex ->
                audio.Delete id
                Runtime.ExceptionServices.ExceptionDispatchInfo.Throw ex
            return [ RecordingUploaded { RecordingId = id; Workflow = args.Workflow } ]
        }

    /// Hides the recording in Loopback. Plaud has no known delete API, so the row stays as a
    /// tombstone (the sync skips it) until the file is deleted in Plaud too; stored transcript and
    /// result are cleared and the local audio is removed, the result file in output/ is kept. A running transcription job is removed
    /// from Speechmatics; a step already in progress notices the status change and drops its work.
    /// An upload has no Plaud file to wait for, so its row and audio are removed right away.
    let deleteRecording (args: CommandArgs.DeleteRecording) =
        task {
            let! row = recordings.TryGet args.RecordingId
            match row with
            | None -> return failwith $"Recording {args.RecordingId} not found"
            | Some r when r.Status = RecordingStatus.toKey Deleted -> return []
            | Some r ->
                match r.SpeechmaticsJobId with
                | Some jobId -> try do! speechmatics.Delete jobId with _ -> ()
                | None -> ()
                // Everything Loopback stores for the recording goes - attachments and marks included.
                do! attachments.DeleteByRecording r.Id
                do! marks.DeleteByRecording r.Id
                try attachmentFiles.DeleteAll r.Id with _ -> ()
                if r.Source = RecordingSource.toKey Upload then
                    do! recordings.Delete r.Id
                else
                    do! recordings.Update {
                        r with
                            Status = RecordingStatus.toKey Deleted
                            Transcript = None
                            Result = None
                            SpeechmaticsJobId = None
                            Error = None
                            FailedStep = None
                            DeletedAt = Some(Db.now ()) }
                try audio.Delete r.Id with _ -> ()
                return [ RecordingDeleted r.Id ]
        }

    /// A recording that can get attachments: any status but deleted (done ones are tombstoned too, so not getLive).
    let getAttachable (id: string) =
        task {
            let! row = recordings.TryGet id
            match row with
            | Some r when r.Status = RecordingStatus.toKey Deleted -> return raise (AttachmentRejected $"Recording {id} was deleted")
            | Some r -> return r
            | None -> return raise (Collections.Generic.KeyNotFoundException $"Recording {id} not found")
        }

    let getAttachment (id: string) =
        task {
            let! row = attachments.TryGet id
            match row with
            | Some a -> return a
            | None -> return raise (Collections.Generic.KeyNotFoundException $"Attachment {id} not found")
        }

    let localOffset (r: RecordingRow) = TimeSpan.FromMinutes(float (defaultArg r.UtcOffsetMinutes 0L))

    /// Stores the file (unless the same content is attached already) and detects its type and capture time.
    let addAttachment (args: CommandArgs.AddAttachment) =
        task {
            let! r = getAttachable args.RecordingId
            let size = FileInfo(args.File).Length
            if size = 0L then raise (AttachmentRejected "The file is empty")
            if size > maxFileBytes then raise (AttachmentRejected $"The file is larger than {maxFileMegabytes} MB")
            let sha256 =
                use s = File.OpenRead args.File
                Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData s)
            let! existing = attachments.GetByRecording r.Id
            match existing |> List.tryFind (fun a -> a.Sha256 = sha256) with
            | Some a -> return [ AttachmentAlreadyExists { RecordingId = r.Id; AttachmentId = a.Id } ]
            | None ->
                if existing.Length >= maxPerRecording then
                    raise (AttachmentRejected $"The recording already has {maxPerRecording} attachments")
                let fileName =
                    match Path.GetFileName args.FileName with
                    | null | "" -> "attachment"
                    | n when n.Length > 255 -> n.Substring(0, 255)
                    | n -> n
                let kind, contentType = FileKind.detect args.File fileName
                let capturedAt, source = CaptureTime.detect args.ReportedCapturedAt fileName args.File args.FileModifiedAt (localOffset r) DateTimeOffset.UtcNow
                let id = Guid.CreateVersion7().ToString "N"
                attachmentFiles.Import(r.Id, id, args.File)
                let now = Db.now ()
                try
                    do! attachments.Insert {
                        Id = id
                        RecordingId = r.Id
                        FileName = fileName
                        ContentType = contentType
                        Kind = AttachmentKind.toKey kind
                        SizeBytes = size
                        Sha256 = sha256
                        CapturedAt = capturedAt |> Option.map _.ToUnixTimeMilliseconds()
                        TimeSource = TimeSource.toKey source
                        FileModifiedAt = args.FileModifiedAt |> Option.map _.ToUnixTimeMilliseconds()
                        Note = None
                        CreatedAt = now
                        UpdatedAt = now
                        Source = ItemSource.toKey args.Source
                        Description = args.Description
                        ReportedCapturedAt = args.ReportedCapturedAt |> Option.map _.ToUnixTimeMilliseconds()
                        DescriptionModel = None
                    }
                with ex ->
                    attachmentFiles.Delete(r.Id, id)
                    Runtime.ExceptionServices.ExceptionDispatchInfo.Throw ex
                do! recordings.TouchAttachments r.Id
                return [ AttachmentAdded { RecordingId = r.Id; AttachmentId = id } ]
        }

    let setAttachmentTime (args: CommandArgs.SetAttachmentTime) =
        task {
            let! a = getAttachment args.AttachmentId
            let! r = getAttachable a.RecordingId
            let updated =
                match args.Time with
                | CommandArgs.AtOffset seconds ->
                    let durationSeconds = float r.DurationMs / 1000.
                    if not (Double.IsFinite seconds) || seconds < 0. || seconds > durationSeconds then
                        raise (AttachmentRejected $"The time must be within the recording (0 - {durationSeconds:F0} s)")
                    { a with CapturedAt = Some(r.StartTime + int64 (seconds * 1000.)); TimeSource = TimeSource.toKey Manual }
                | CommandArgs.AsReference ->
                    { a with CapturedAt = None; TimeSource = TimeSource.toKey Manual }
                | CommandArgs.Detected ->
                    let fileModifiedAt = a.FileModifiedAt |> Option.map DateTimeOffset.FromUnixTimeMilliseconds
                    let reported = a.ReportedCapturedAt |> Option.map DateTimeOffset.FromUnixTimeMilliseconds
                    let capturedAt, source =
                        CaptureTime.detect reported a.FileName (attachmentFiles.PathOf(r.Id, a.Id)) fileModifiedAt (localOffset r) DateTimeOffset.UtcNow
                    { a with CapturedAt = capturedAt |> Option.map _.ToUnixTimeMilliseconds(); TimeSource = TimeSource.toKey source }
            do! attachments.Update updated
            do! recordings.TouchAttachments r.Id
            return [ AttachmentUpdated a.Id ]
        }

    let setAttachmentNote (args: CommandArgs.SetAttachmentNote) =
        task {
            let! a = getAttachment args.AttachmentId
            let! r = getAttachable a.RecordingId
            let note =
                match args.Note |> Option.map _.Trim() with
                | Some n when n.Length > 1000 -> raise (AttachmentRejected "The note is longer than 1000 characters")
                | Some n when n <> "" -> Some n
                | _ -> None
            do! attachments.Update { a with Note = note }
            do! recordings.TouchAttachments r.Id
            return [ AttachmentUpdated a.Id ]
        }

    let deleteAttachment (args: CommandArgs.DeleteAttachment) =
        task {
            let! row = attachments.TryGet args.AttachmentId
            match row with
            | None -> return []
            | Some a ->
                do! attachments.Delete a.Id
                try attachmentFiles.Delete(a.RecordingId, a.Id) with _ -> ()
                do! recordings.TouchAttachments a.RecordingId
                return [ AttachmentDeleted a.Id ]
        }

    let getMark (id: string) =
        task {
            let! row = marks.TryGet id
            match row with
            | Some m -> return m
            | None -> return raise (Collections.Generic.KeyNotFoundException $"Mark {id} not found")
        }

    /// A note added in Loopback, at a moment of the call or without a time (the call as a whole).
    let addMark (args: CommandArgs.AddMark) =
        task {
            let! r = getAttachable args.RecordingId
            let note =
                match args.Note |> Option.ofObj |> Option.map _.Trim() with
                | Some n when n.Length > 1000 -> raise (AttachmentRejected "The note is longer than 1000 characters")
                | Some n when n <> "" -> n
                | _ -> raise (AttachmentRejected "The note is empty")
            let offsetMs =
                args.OffsetSeconds
                |> Option.map (fun seconds ->
                    let durationSeconds = float r.DurationMs / 1000.
                    if not (Double.IsFinite seconds) || seconds < 0. || seconds > durationSeconds then
                        raise (AttachmentRejected $"The time must be within the recording (0 - {durationSeconds:F0} s)")
                    int64 (seconds * 1000.))
            let id = Guid.CreateVersion7().ToString "N"
            let now = Db.now ()
            do! marks.Insert {
                Id = id
                RecordingId = r.Id
                OffsetMs = offsetMs
                Note = Some note
                Source = ItemSource.toKey Uploaded
                Pending = 0L
                UserEdited = 1L
                CreatedAt = now
                UpdatedAt = now
                Kind = MarkKind.toKey NoteMark
            }
            do! recordings.TouchAttachments r.Id
            return [ MarkAdded id ]
        }

    /// The user's note wins from now on - Plaud's late text for a pending highlight never overwrites it.
    let setMarkNote (args: CommandArgs.SetMarkNote) =
        task {
            let! m = getMark args.MarkId
            let! r = getAttachable m.RecordingId
            let note =
                match args.Note |> Option.map _.Trim() with
                | Some n when n.Length > 1000 -> raise (AttachmentRejected "The note is longer than 1000 characters")
                | Some n when n <> "" -> Some n
                | _ -> None
            do! marks.Update { m with Note = note; UserEdited = 1L; Pending = 0L }
            do! recordings.TouchAttachments r.Id
            return [ MarkUpdated m.Id ]
        }

    /// Its import key stays, so the next sync does not bring it back.
    let deleteMark (args: CommandArgs.DeleteMark) =
        task {
            let! row = marks.TryGet args.MarkId
            match row with
            | None -> return []
            | Some m ->
                do! marks.Delete m.Id
                do! recordings.TouchAttachments m.RecordingId
                return [ MarkDeleted m.Id ]
        }

    /// Done recordings only - one reprocessing meanwhile (summarizing) is left alone.
    let deleteProcessed () =
        task {
            // Not GetByStatus: it skips tombstoned rows, and done recordings are tombstoned on purpose.
            let! all = recordings.GetAll()
            let ids = all |> List.filter (fun r -> r.Status = RecordingStatus.toKey Done) |> List.map (fun r -> r.Id)
            let events = ResizeArray<Event>()
            for id in ids do
                let! e = deleteRecording { RecordingId = id }
                events.AddRange e
            return List.ofSeq events
        }

    interface RecordingsCommandHandler with
        member _.Handle command =
            task {
                match command with
                | ProcessRecording args -> return! processRecording args
                | RetryRecording args -> return! retryRecording args
                | ReprocessRecording args -> return! reprocessRecording args
                | DeleteRecording args -> return! deleteRecording args
                | UploadRecording args -> return! uploadRecording args
                | DeleteProcessed -> return! deleteProcessed ()
                | SyncNow ->
                    syncTrigger.Request()
                    return [ SyncRequested ]
                | AddAttachment args -> return! addAttachment args
                | SetAttachmentTime args -> return! setAttachmentTime args
                | SetAttachmentNote args -> return! setAttachmentNote args
                | DeleteAttachment args -> return! deleteAttachment args
                | AddMark args -> return! addMark args
                | SetMarkNote args -> return! setMarkNote args
                | DeleteMark args -> return! deleteMark args
            }
