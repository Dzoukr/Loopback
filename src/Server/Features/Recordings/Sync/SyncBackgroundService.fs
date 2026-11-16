/// Every `PLAUD_SYNC_INTERVAL_MINUTES` (or on demand via SyncTrigger) pulls recording
/// metadata from Plaud into SQLite. The audio is downloaded afterwards by AudioBackgroundService.
module Loopback.Server.Features.Recordings.Sync.SyncBackgroundService

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Loopback.Server
open Loopback.Server.Configuration
open Loopback.Server.Integrations.Plaud
open Loopback.Server.Features.Recordings.Domain
open Loopback.Server.Features.Recordings.Database
open Loopback.Server.Features.Recordings.Sync.PlaudSession
open Loopback.Server.Features.Recordings.Sync.SyncTrigger
open Loopback.Server.Features.Recordings.Audio.AudioStore
open Loopback.Server.Features.Recordings.Attachments.AttachmentStore

/// Pages of 50 recordings (PlaudClient.ListFiles).
let private maxPages = 20

type SyncResult = { New : int; Updated : int }

type SyncBackgroundService(
    session: PlaudSession,
    plaud: PlaudClient,
    recordings: RecordingsRepository,
    attachments: AttachmentsRepository,
    marks: MarksRepository,
    audio: AudioStore,
    attachmentFiles: AttachmentStore,
    trigger: SyncTrigger,
    cfg: Configuration,
    logger: ILogger<SyncBackgroundService>) =
    inherit BackgroundService()

    let detailOf (f: PlaudFile) = session.Use(fun s -> plaud.GetFileDetail(s.ApiBase, s.WorkspaceToken, f.Id))

    /// Inserts new recordings and refreshes metadata of changed ones (`version_ms`); the file
    /// detail (time zone, size, marks) is fetched only for those.
    /// Tombstoned recordings are skipped, so a processed recording never comes back.
    let upsert (f: PlaudFile) =
        task {
            let! existing = recordings.TryGet f.Id
            match existing with
            | None ->
                let! d = detailOf f
                let now = Db.now ()
                do! recordings.Insert {
                    Id = f.Id
                    Filename = f.Filename
                    StartTime = f.StartTime.ToUnixTimeMilliseconds()
                    DurationMs = f.DurationMs
                    Filesize = d.Filesize
                    VersionMs = f.VersionMs
                    Status = RecordingStatus.toKey Synced
                    Workflow = None
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
                    UtcOffsetMinutes = Some(int64 (defaultArg d.UtcOffsetMinutes 0))
                    Result = None
                    Source = RecordingSource.toKey Plaud
                }
                do! recordings.SetHasPlaudMarks(f.Id, d.HasMarks)
                return Some true
            // Also backfills the offset of rows synced before it was stored.
            | Some r when r.DeletedAt.IsNone && (r.VersionMs <> f.VersionMs || r.UtcOffsetMinutes.IsNone) ->
                let! d = detailOf f
                let offset = d.UtcOffsetMinutes |> Option.map int64 |> Option.orElse r.UtcOffsetMinutes |> Option.defaultValue 0L
                do! recordings.Update { r with Filename = f.Filename; StartTime = f.StartTime.ToUnixTimeMilliseconds(); DurationMs = f.DurationMs; Filesize = d.Filesize; VersionMs = f.VersionMs; UtcOffsetMinutes = Some offset }
                do! recordings.SetHasPlaudMarks(f.Id, d.HasMarks)
                return Some false
            | Some _ -> return None
        }

    /// Pages through Plaud, newest edits first; stops at the last page or after two
    /// pages without any change (routine syncs touch only the first page).
    let sync () =
        task {
            let mutable page = 0
            let mutable cursor = None
            let mutable quietPages = 0
            let mutable result = { New = 0; Updated = 0 }
            let mutable go = true
            while go do
                let! files, next = session.Use(fun s -> plaud.ListFiles(s.ApiBase, s.WorkspaceToken, cursor))
                let mutable changed = 0
                for f in files do
                    let! outcome = upsert f
                    match outcome with
                    | Some true -> result <- { result with New = result.New + 1 }; changed <- changed + 1
                    | Some false -> result <- { result with Updated = result.Updated + 1 }; changed <- changed + 1
                    | None -> ()
                quietPages <- if changed = 0 then quietPages + 1 else 0
                page <- page + 1
                cursor <- next
                go <- next.IsSome && not files.IsEmpty && quietPages < 2 && page < maxPages
            return result
        }

    /// Removes local rows of recordings deleted in Plaud (gone from both the main list and the
    /// trash - a trashed file can still be restored), together with their local audio and attachments. Only done,
    /// synced and deleted rows; result files in output/ are never touched. Any listing failure aborts before anything is deleted.
    let removeDeletedInPlaud () =
        task {
            let! candidates = recordings.GetRemovableIfGoneFromPlaud()
            if not candidates.IsEmpty then
                let! active = session.Use(fun s -> plaud.ListAllIds(s.ApiBase, s.WorkspaceToken, false))
                let! trash = session.Use(fun s -> plaud.ListAllIds(s.ApiBase, s.WorkspaceToken, true))
                for r in candidates do
                    if not (active.Contains r.Id) && not (trash.Contains r.Id) then
                        let! deleted = recordings.DeleteIfRemovable r.Id
                        if deleted then
                            audio.Delete r.Id
                            do! attachments.DeleteByRecording r.Id
                            do! marks.DeleteByRecording r.Id
                            attachmentFiles.DeleteAll r.Id
                            logger.LogInformation("Recording {Id} ({Status}) was deleted in Plaud - removed from the database", r.Id, r.Status)
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            logger.LogInformation("Sync background service started (every {Minutes} min)", cfg.Plaud.SyncIntervalMinutes)
            while not stoppingToken.IsCancellationRequested do
                try
                    let! r = sync ()
                    do! removeDeletedInPlaud ()
                    do! session.RecordSync None
                    if r.New > 0 || r.Updated > 0 then
                        logger.LogInformation("Plaud sync: {New} new, {Updated} updated", r.New, r.Updated)
                with
                | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
                | ex ->
                    let message =
                        match ex with
                        | PlaudLoginFailed m -> $"Plaud login failed: {m}"
                        | PlaudUnauthorized m | PlaudSessionExpired m -> $"Plaud rejected the session even after logging in again ({m})"
                        | PlaudError m -> m
                        | _ -> ex.Message
                    logger.LogWarning("Plaud sync failed: {Error}", message)
                    try
                        do! session.RecordSync(Some message)
                    with recordEx -> logger.LogError(recordEx, "Could not record the sync failure")
                try
                    do! trigger.Wait(TimeSpan.FromMinutes(float cfg.Plaud.SyncIntervalMinutes), stoppingToken)
                with :? OperationCanceledException -> ()
        } :> Task
