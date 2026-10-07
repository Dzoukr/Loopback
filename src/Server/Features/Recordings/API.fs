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

// API contracts use plain .NET types only (no option / list), so the generated
// TypeScript client stays simple. Missing values are null.

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

/// Largest accepted upload (90 minutes of 320 kbit/s MP3 is ~210 MB).
let private maxUploadBytes = 1024L * 1024L * 1024L

let private orNull (o: string option) = Option.toObj o
let private orNullable (o: DateTimeOffset option) = Option.toNullable o

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
        ]
    ]
