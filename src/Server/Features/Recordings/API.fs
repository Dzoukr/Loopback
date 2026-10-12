module Loopback.Server.Features.Recordings.API

open System
open System.IO
open Giraffe
open Giraffe.EndpointRouting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.DependencyInjection
open Loopback.Server.Handlers
open Loopback.Server.OpenAPI
open Loopback.Server.Integrations.AudioFiles
open Loopback.Server.Features.Recordings.Domain
open Loopback.Server.Features.Recordings.Attachments.AttachmentStore

// API contracts use plain .NET types only (no option / list), so the generated
// TypeScript client stays simple. Missing values are null.

type AttachmentDto = {
    Id : string
    /// Original file name incl. extension.
    FileName : string
    ContentType : string
    /// image | pdf | text | other (how it is previewed)
    Kind : string
    SizeBytes : int64
    /// When the file was captured, null if unknown or moved to reference by the user.
    CapturedAt : Nullable<DateTimeOffset>
    /// filename | exif | modified | manual | none
    TimeSource : string
    Note : string
    /// upload | plaud (imported from a Plaud mark)
    Source : string
    /// Machine-made text: Plaud's photo description, a voice memo's transcript. Null if none.
    Description : string
    /// duringCall | reference
    Placement : string
    /// Seconds from the recording's start (during call only), else null.
    OffsetSeconds : Nullable<float>
    CreatedAt : DateTimeOffset
}

type MarkDto = {
    Id : string
    /// highlight (device button) | note (typed in Plaud) | voice (voice memo, the note is its transcript)
    Kind : string
    /// Seconds from the recording's start.
    OffsetSeconds : float
    Note : string
    /// plaud
    Source : string
    /// Plaud is still analyzing it - the note comes later.
    Pending : bool
}

type RecordingDto = {
    Id : string
    Filename : string
    /// plaud | upload
    Source : string
    StartTime : DateTimeOffset
    DurationMs : int64
    /// synced | queued | transcribing | summarizing | done | failed
    Status : string
    Workflow : string
    Title : string
    Error : string
    FailedStep : string
    OutputFile : string
    UpdatedAt : DateTimeOffset
    /// Waveform envelope in [0, 1] (600 values), null until computed.
    Peaks : float[]
    /// Result file JSON (envelope with `content`, shape per workflow schema) of a done recording, else null.
    Result : string
    /// Done with a stored transcript: the workflow can run again without transcribing.
    CanReprocess : bool
    /// Transcription finished and the transcript is stored (see getTranscript).
    HasTranscript : bool
    /// During call by offset, then reference by file name.
    Attachments : AttachmentDto[]
    /// Done, and the attachments changed after the result was produced.
    AttachmentsOutdated : bool
    /// Moments marked in Plaud during the call, by offset.
    Marks : MarkDto[]
}

type TranscriptSegmentDto = {
    /// Speechmatics speaker label (S1, S2, ... or UU for unknown).
    Speaker : string
    /// Seconds from the start of the recording.
    Start : float
    End : float
    Text : string
}

type WorkflowDto = {
    Name : string
    HasCustomSchema : bool
}

type SyncStatusDto = {
    Connected : bool
    LastSyncAt : Nullable<DateTimeOffset>
    LastError : string
    Account : string
}

type ProcessRecordingRequest = {
    RecordingId : string
    Workflow : string
}

type RetryRecordingRequest = {
    RecordingId : string
}

type ReprocessRecordingRequest = {
    RecordingId : string
    Workflow : string
}

type DeleteRecordingRequest = {
    RecordingId : string
}

type SuccessResponse = {
    Success : bool
}

type DeleteProcessedResponse = {
    /// Number of recordings deleted.
    Deleted : int
}

type UploadRecordingResponse = {
    RecordingId : string
}

type AddAttachmentResponse = {
    AttachmentId : string
    /// The same file was attached already - nothing new was stored.
    AlreadyExisted : bool
}

type SetAttachmentTimeRequest = {
    AttachmentId : string
    /// offset (OffsetSeconds into the recording) | reference (the call as a whole) | detected (drop the manual time)
    Mode : string
    /// Used with mode offset only (send 0 otherwise).
    OffsetSeconds : float
}

type SetAttachmentNoteRequest = {
    AttachmentId : string
    /// Null or blank removes the note.
    Note : string
}

type DeleteAttachmentRequest = {
    AttachmentId : string
}

type SetMarkNoteRequest = {
    MarkId : string
    /// Null or blank removes the note.
    Note : string
}

type DeleteMarkRequest = {
    MarkId : string
}

/// Largest accepted upload (90 minutes of 320 kbit/s MP3 is ~210 MB).
let private maxUploadBytes = 1024L * 1024L * 1024L

let private orNull (o: string option) = Option.toObj o
let private orNullable (o: DateTimeOffset option) = Option.toNullable o

let private toAttachmentDto (a: Queries.Attachment) =
    {
        Id = a.Id
        FileName = a.FileName
        ContentType = a.ContentType
        Kind = AttachmentKind.toKey a.Kind
        SizeBytes = a.SizeBytes
        CapturedAt = orNullable a.CapturedAt
        TimeSource = TimeSource.toKey a.TimeSource
        Note = orNull a.Note
        Source = ItemSource.toKey a.Source
        Description = orNull a.Description
        Placement = (match a.Placement with DuringCall _ -> "duringCall" | Reference -> "reference")
        OffsetSeconds = (match a.Placement with DuringCall offset -> Nullable offset | Reference -> Nullable())
        CreatedAt = a.CreatedAt
    }

let private getRecordings (ctx: HttpContext) =
    task {
        let queries = ctx.RequestServices.GetRequiredService<RecordingsQueries>()
        let! recordings = queries.GetRecordings()
        return
            recordings
            |> List.map (fun r ->
                {
                    Id = r.Id
                    Filename = r.Filename
                    Source = RecordingSource.toKey r.Source
                    StartTime = r.StartTime
                    DurationMs = r.DurationMs
                    Status = RecordingStatus.toKey r.Status
                    Workflow = orNull r.Workflow
                    Title = orNull r.Title
                    Error = orNull r.Error
                    FailedStep = r.FailedStep |> Option.map RecordingStatus.toKey |> orNull
                    OutputFile = orNull r.OutputFile
                    UpdatedAt = r.UpdatedAt
                    Peaks = Option.toObj r.Peaks
                    Result = orNull r.Result
                    CanReprocess = r.CanReprocess
                    HasTranscript = r.HasTranscript
                    Attachments = r.Attachments |> List.map toAttachmentDto |> Array.ofList
                    AttachmentsOutdated = r.AttachmentsOutdated
                    Marks =
                        r.Marks
                        |> List.map (fun m -> { Id = m.Id; Kind = MarkKind.toKey m.Kind; OffsetSeconds = m.OffsetSeconds; Note = orNull m.Note; Source = ItemSource.toKey m.Source; Pending = m.Pending })
                        |> Array.ofList
                })
            |> Array.ofList
    }

let private getWorkflows (ctx: HttpContext) =
    task {
        let queries = ctx.RequestServices.GetRequiredService<RecordingsQueries>()
        let! workflows = queries.GetWorkflows()
        return workflows |> List.map (fun n -> { Name = n.Name; HasCustomSchema = n.HasCustomSchema }) |> Array.ofList
    }

let private getSyncStatus (ctx: HttpContext) =
    task {
        let queries = ctx.RequestServices.GetRequiredService<RecordingsQueries>()
        let! s = queries.GetSyncStatus()
        return {
            Connected = s.Connected
            LastSyncAt = orNullable s.LastSyncAt
            LastError = orNull s.LastError
            Account = orNull s.Account
        }
    }

/// Empty when the recording is unknown or not transcribed yet.
let private getTranscript (recordingId: string) (ctx: HttpContext) =
    task {
        let queries = ctx.RequestServices.GetRequiredService<RecordingsQueries>()
        let! segments = queries.GetTranscript recordingId
        return
            segments
            |> List.map (fun s -> { Speaker = s.Speaker; Start = s.Start; End = s.End; Text = s.Text })
            |> Array.ofList
    }

/// The locally stored audio (Ogg, or MP3 for some uploads), with HTTP Range support for seeking.
let private getAudio (recordingId: string) : HttpHandler =
    fun next ctx -> task {
        let queries = ctx.RequestServices.GetRequiredService<RecordingsQueries>()
        let! path = queries.GetAudioFile recordingId
        match path with
        | Some p ->
            ctx.SetContentType(AudioFormat.contentTypeOf p)
            return! streamFile true p None None next ctx
        | None -> return! RequestErrors.NOT_FOUND $"Recording {recordingId} not found" next ctx
    }

let private processRecording (ctx: HttpContext) =
    task {
        let! req = ctx.BindJsonAsync<ProcessRecordingRequest>()
        let commandHandler = ctx.RequestServices.GetRequiredService<RecordingsCommandHandler>()
        let! _ = commandHandler.Handle(ProcessRecording { RecordingId = req.RecordingId; Workflow = req.Workflow })
        return { Success = true }
    }

let private retryRecording (ctx: HttpContext) =
    task {
        let! req = ctx.BindJsonAsync<RetryRecordingRequest>()
        let commandHandler = ctx.RequestServices.GetRequiredService<RecordingsCommandHandler>()
        let! _ = commandHandler.Handle(RetryRecording { RecordingId = req.RecordingId })
        return { Success = true }
    }

let private reprocessRecording (ctx: HttpContext) =
    task {
        let! req = ctx.BindJsonAsync<ReprocessRecordingRequest>()
        let commandHandler = ctx.RequestServices.GetRequiredService<RecordingsCommandHandler>()
        let! _ = commandHandler.Handle(ReprocessRecording { RecordingId = req.RecordingId; Workflow = req.Workflow })
        return { Success = true }
    }

let private deleteRecording (ctx: HttpContext) =
    task {
        let! req = ctx.BindJsonAsync<DeleteRecordingRequest>()
        let commandHandler = ctx.RequestServices.GetRequiredService<RecordingsCommandHandler>()
        let! _ = commandHandler.Handle(DeleteRecording { RecordingId = req.RecordingId })
        return { Success = true }
    }

let private deleteProcessed (ctx: HttpContext) =
    task {
        let commandHandler = ctx.RequestServices.GetRequiredService<RecordingsCommandHandler>()
        let! events = commandHandler.Handle DeleteProcessed
        return { Deleted = events |> List.filter (function RecordingDeleted _ -> true | _ -> false) |> List.length }
    }

/// Multipart form: `file` (MP3 or Ogg Vorbis / Opus), optional `startTime` (unix ms, default now),
/// `utcOffsetMinutes` (the recording's local offset, e.g. 120 for UTC+02:00) and `workflow`
/// (queue it right away). 400 with the reason when the file is not usable audio.
let private uploadRecording : HttpHandler =
    fun next ctx -> task {
        match ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() with
        | null -> ()
        | f when f.IsReadOnly -> ()
        | f -> f.MaxRequestBodySize <- Nullable maxUploadBytes
        ctx.Features.Set<IFormFeature>(FormFeature(ctx.Request, FormOptions(MultipartBodyLengthLimit = maxUploadBytes)))
        let! form = ctx.Request.ReadFormAsync()
        match form.Files.GetFile "file" with
        | null -> return! (setStatusCode 400 >=> text "No file uploaded (form field 'file')") next ctx
        | file ->
            let field (name: string) = match string form[name] with | "" -> None | v -> Some v
            let startTime =
                match field "startTime" |> Option.map Int64.TryParse with
                | Some(true, ms) -> DateTimeOffset.FromUnixTimeMilliseconds ms
                | _ -> DateTimeOffset.UtcNow
            let utcOffsetMinutes =
                match field "utcOffsetMinutes" |> Option.map Int32.TryParse with
                | Some(true, m) -> m
                | _ -> int (TimeZoneInfo.Local.GetUtcOffset startTime).TotalMinutes
            let filename =
                match Path.GetFileNameWithoutExtension file.FileName with
                | null | "" -> "Upload"
                | n -> n
            // The original name is never part of a path; the format is detected from the content.
            let temp = Path.Combine(Path.GetTempPath(), $"loopback-upload-{Guid.NewGuid():N}")
            try
                do! task {
                    use s = File.Create temp
                    do! file.CopyToAsync s
                }
                let commandHandler = ctx.RequestServices.GetRequiredService<RecordingsCommandHandler>()
                try
                    let! events =
                        commandHandler.Handle(UploadRecording {
                            AudioFile = temp
                            Filename = filename
                            StartTime = startTime
                            UtcOffsetMinutes = utcOffsetMinutes
                            Workflow = field "workflow" })
                    let id = events |> List.pick (function RecordingUploaded e -> Some e.RecordingId | _ -> None)
                    return! json { RecordingId = id } next ctx
                with
                | UnsupportedAudio message
                // e.g. unknown workflow (CommandHandler fails with a plain message)
                | Failure message -> return! (setStatusCode 400 >=> text message) next ctx
            finally
                if File.Exists temp then File.Delete temp
    }

/// Runs an attachment command: 400 with the reason when it is rejected, 404 for an unknown recording / attachment.
let private attachmentCommand (command: Command) (respond: Event list -> HttpHandler) : HttpHandler =
    fun next ctx -> task {
        let commandHandler = ctx.RequestServices.GetRequiredService<RecordingsCommandHandler>()
        try
            let! events = commandHandler.Handle command
            return! respond events next ctx
        with
        | AttachmentRejected message -> return! (setStatusCode 400 >=> text message) next ctx
        | :? Collections.Generic.KeyNotFoundException as e -> return! (setStatusCode 404 >=> text e.Message) next ctx
    }

/// Multipart form, one file per request: `file`, optional `lastModified` (the file's last-modified time,
/// unix ms - omitted for clipboard pastes, where it is the paste time).
let private addAttachment (recordingId: string) : HttpHandler =
    fun next ctx -> task {
        // Some headroom over the file limit for the multipart framing; the command checks the file itself.
        let limit = maxFileBytes + 1024L * 1024L
        match ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() with
        | null -> ()
        | f when f.IsReadOnly -> ()
        | f -> f.MaxRequestBodySize <- Nullable limit
        ctx.Features.Set<IFormFeature>(FormFeature(ctx.Request, FormOptions(MultipartBodyLengthLimit = limit)))
        let! form =
            task {
                try
                    let! f = ctx.Request.ReadFormAsync()
                    return Ok f
                with
                | :? InvalidDataException
                | :? BadHttpRequestException -> return Error $"The file is larger than {maxFileMegabytes} MB"
            }
        match form with
        | Error message -> return! (setStatusCode 400 >=> text message) next ctx
        | Ok form ->
        match form.Files.GetFile "file" with
        | null -> return! (setStatusCode 400 >=> text "No file uploaded (form field 'file')") next ctx
        | file ->
            let fileModifiedAt =
                match Int64.TryParse(string form["lastModified"]) with
                | true, ms when ms > 0L -> Some(DateTimeOffset.FromUnixTimeMilliseconds ms)
                | _ -> None
            let temp = Path.Combine(Path.GetTempPath(), $"loopback-attachment-{Guid.NewGuid():N}")
            try
                do! task {
                    use s = File.Create temp
                    do! file.CopyToAsync s
                }
                let command =
                    AddAttachment {
                        RecordingId = recordingId
                        File = temp
                        FileName = file.FileName
                        FileModifiedAt = fileModifiedAt
                        Source = Uploaded
                        ReportedCapturedAt = None
                        Description = None
                    }
                let respond (events: Event list) =
                    events
                    |> List.pick (function
                        | AttachmentAdded e -> Some { AttachmentId = e.AttachmentId; AlreadyExisted = false }
                        | AttachmentAlreadyExists e -> Some { AttachmentId = e.AttachmentId; AlreadyExisted = true }
                        | _ -> None)
                    |> json
                return! attachmentCommand command respond next ctx
            finally
                if File.Exists temp then File.Delete temp
    }

/// The attachment's file. Images, PDFs and text open in the browser; anything else (and `?download=1`)
/// is a download - never rendered, as it may be HTML / SVG with scripts.
let private getAttachment (recordingId: string, attachmentId: string) : HttpHandler =
    fun next ctx -> task {
        let queries = ctx.RequestServices.GetRequiredService<RecordingsQueries>()
        let! file = queries.GetAttachmentFile(recordingId, attachmentId)
        match file with
        | Some f ->
            let inline' = f.Kind <> Other && not (ctx.Request.Query.ContainsKey "download")
            let disposition = Net.Http.Headers.ContentDispositionHeaderValue(if inline' then "inline" else "attachment")
            disposition.FileNameStar <- f.FileName
            ctx.SetContentType f.ContentType
            ctx.SetHttpHeader("Content-Disposition", disposition.ToString())
            ctx.SetHttpHeader("X-Content-Type-Options", "nosniff")
            ctx.SetHttpHeader("Cache-Control", "private, max-age=3600")
            return! streamFile true f.Path None None next ctx
        | None -> return! RequestErrors.NOT_FOUND $"Attachment {attachmentId} not found" next ctx
    }

let private setAttachmentTime : HttpHandler =
    fun next ctx -> task {
        let! req = ctx.BindJsonAsync<SetAttachmentTimeRequest>()
        let time =
            match req.Mode with
            | "offset" -> Some(CommandArgs.AtOffset req.OffsetSeconds)
            | "reference" -> Some CommandArgs.AsReference
            | "detected" -> Some CommandArgs.Detected
            | _ -> None
        match time with
        | Some t -> return! attachmentCommand (SetAttachmentTime { AttachmentId = req.AttachmentId; Time = t }) (fun _ -> json { Success = true }) next ctx
        | None -> return! (setStatusCode 400 >=> text "Mode must be offset, reference or detected") next ctx
    }

let private setAttachmentNote : HttpHandler =
    fun next ctx -> task {
        let! req = ctx.BindJsonAsync<SetAttachmentNoteRequest>()
        return! attachmentCommand (SetAttachmentNote { AttachmentId = req.AttachmentId; Note = Option.ofObj req.Note }) (fun _ -> json { Success = true }) next ctx
    }

let private deleteAttachment : HttpHandler =
    fun next ctx -> task {
        let! req = ctx.BindJsonAsync<DeleteAttachmentRequest>()
        return! attachmentCommand (DeleteAttachment { AttachmentId = req.AttachmentId }) (fun _ -> json { Success = true }) next ctx
    }

let private setMarkNote : HttpHandler =
    fun next ctx -> task {
        let! req = ctx.BindJsonAsync<SetMarkNoteRequest>()
        return! attachmentCommand (SetMarkNote { MarkId = req.MarkId; Note = Option.ofObj req.Note }) (fun _ -> json { Success = true }) next ctx
    }

let private deleteMark : HttpHandler =
    fun next ctx -> task {
        let! req = ctx.BindJsonAsync<DeleteMarkRequest>()
        return! attachmentCommand (DeleteMark { MarkId = req.MarkId }) (fun _ -> json { Success = true }) next ctx
    }

let private syncNow (ctx: HttpContext) =
    task {
        let commandHandler = ctx.RequestServices.GetRequiredService<RecordingsCommandHandler>()
        let! _ = commandHandler.Handle SyncNow
        return { Success = true }
    }

let api =
    subRoute "/recordings" [
        GET [
            route "" (simpleJson getRecordings)
            |> jsonOut<RecordingDto[]> "getRecordings"

            route "/workflows" (simpleJson getWorkflows)
            |> jsonOut<WorkflowDto[]> "getWorkflows"

            route "/sync-status" (simpleJson getSyncStatus)
            |> jsonOut<SyncStatusDto> "getSyncStatus"

            routef "/%s/transcript" (simpleJsonWith getTranscript)
            |> jsonOut<TranscriptSegmentDto[]> "getTranscript"

            // Binary, not part of the generated client - the web app proxies it (app/api/recordings/[id]/audio).
            routef "/%s/audio" getAudio

            // Binary, not part of the generated client - proxied by app/api/recordings/[id]/attachments/[attachmentId].
            routef "/%s/attachments/%s" getAttachment
        ]
        POST [
            route "/process" (simpleJson processRecording)
            |> jsonInOut<ProcessRecordingRequest, SuccessResponse> "processRecording"

            route "/retry" (simpleJson retryRecording)
            |> jsonInOut<RetryRecordingRequest, SuccessResponse> "retryRecording"

            route "/reprocess" (simpleJson reprocessRecording)
            |> jsonInOut<ReprocessRecordingRequest, SuccessResponse> "reprocessRecording"

            route "/delete" (simpleJson deleteRecording)
            |> jsonInOut<DeleteRecordingRequest, SuccessResponse> "deleteRecording"

            route "/delete-processed" (simpleJson deleteProcessed)
            |> jsonOut<DeleteProcessedResponse> "deleteProcessed"

            route "/sync" (simpleJson syncNow)
            |> jsonOut<SuccessResponse> "syncNow"

            // Multipart, not part of the generated client - the web app streams it through (app/api/recordings/upload).
            route "/upload" uploadRecording

            // Multipart, not part of the generated client - streamed through app/api/recordings/[id]/attachments.
            routef "/%s/attachments" addAttachment

            route "/attachments/time" setAttachmentTime
            |> jsonInOut<SetAttachmentTimeRequest, SuccessResponse> "setAttachmentTime"

            route "/attachments/note" setAttachmentNote
            |> jsonInOut<SetAttachmentNoteRequest, SuccessResponse> "setAttachmentNote"

            route "/attachments/delete" deleteAttachment
            |> jsonInOut<DeleteAttachmentRequest, SuccessResponse> "deleteAttachment"

            route "/marks/note" setMarkNote
            |> jsonInOut<SetMarkNoteRequest, SuccessResponse> "setMarkNote"

            route "/marks/delete" deleteMark
            |> jsonInOut<DeleteMarkRequest, SuccessResponse> "deleteMark"
        ]
    ]
