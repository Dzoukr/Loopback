module Loopback.Server.Features.Recordings.CommandHandler

open Loopback.Server
open Loopback.Server.Integrations.Speechmatics
open Loopback.Server.Features.Recordings.Domain
open Loopback.Server.Features.Recordings.Database
open Loopback.Server.Features.Recordings.Sync.SyncTrigger
open Loopback.Server.Features.Recordings.Audio.AudioStore
open Loopback.Server.Features.Recordings.Processing.Workflows

/// SQLite-backed implementation of RecordingsCommandHandler. Commands only change the
/// recording's status; the background services do the actual work.
type StorageCommandHandler(recordings: RecordingsRepository, workflows: WorkflowCatalog, syncTrigger: SyncTrigger, speechmatics: SpeechmaticsClient, audio: AudioStore) =

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

    /// Hides the recording in Loopback. Plaud has no known delete API, so the row stays as a
    /// tombstone (the sync skips it) until the file is deleted in Plaud too; stored transcript and
    /// result are cleared and the local audio is removed, the result file in output/ is kept. A running transcription job is removed
    /// from Speechmatics; a step already in progress notices the status change and drops its work.
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

    interface RecordingsCommandHandler with
        member _.Handle command =
            task {
                match command with
                | ProcessRecording args -> return! processRecording args
                | RetryRecording args -> return! retryRecording args
                | ReprocessRecording args -> return! reprocessRecording args
                | DeleteRecording args -> return! deleteRecording args
                | SyncNow ->
                    syncTrigger.Request()
                    return [ SyncRequested ]
            }
