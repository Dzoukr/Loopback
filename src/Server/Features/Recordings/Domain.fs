module Loopback.Server.Features.Recordings.Domain

open System
open System.Threading.Tasks

// ============================================================================
// CQRS Contracts for Recordings (sync from Plaud + processing pipeline)
// ============================================================================

/// Lifecycle of a recording: synced -> queued -> transcribing -> summarizing -> done | failed,
/// plus deleted (hidden in Loopback, kept as a tombstone until the Plaud file is gone too).
type RecordingStatus =
    | Synced
    | Queued
    | Transcribing
    | Summarizing
    | Done
    | Failed
    | Deleted

module RecordingStatus =
    let toKey =
        function
        | Synced -> "synced"
        | Queued -> "queued"
        | Transcribing -> "transcribing"
        | Summarizing -> "summarizing"
        | Done -> "done"
        | Failed -> "failed"
        | Deleted -> "deleted"

    let fromKey =
        function
        | "synced" -> Synced
        | "queued" -> Queued
        | "transcribing" -> Transcribing
        | "summarizing" -> Summarizing
        | "done" -> Done
        | "failed" -> Failed
        | "deleted" -> Deleted
        | other -> failwith $"Unknown recording status '{other}'"

/// Query Models - Read-side representations
module Queries =
    type Recording = {
        Id : string
        Filename : string
        StartTime : DateTimeOffset
        DurationMs : int64
        Status : RecordingStatus
        Workflow : string option
        Title : string option
        Error : string option
        FailedStep : RecordingStatus option
        OutputFile : string option
        UpdatedAt : DateTimeOffset
        /// Waveform envelope in [0, 1], None until computed.
        Peaks : float[] option
        /// Result file JSON (envelope with `content`) of a done recording.
        Result : string option
        /// Done and a transcript is stored, so the workflow can run again without transcribing.
        CanReprocess : bool
    }

    type Workflow = {
        Name : string
        HasCustomSchema : bool
    }

    type SyncStatus = {
        Connected : bool
        LastSyncAt : DateTimeOffset option
        LastError : string option
        /// The Plaud account Loopback logs in with (PLAUD_EMAIL), None if not configured.
        Account : string option
    }

/// Query Interface - Read operations
type RecordingsQueries =
    abstract member GetRecordings : unit -> Task<Queries.Recording list>
    abstract member GetWorkflows : unit -> Task<Queries.Workflow list>
    abstract member GetSyncStatus : unit -> Task<Queries.SyncStatus>
    /// Path of the locally stored audio (downloaded from Plaud first if still missing); None for an unknown or deleted recording.
    abstract member GetAudioFile : string -> Task<string option>

/// Command Arguments
module CommandArgs =
    type ProcessRecording = {
        RecordingId : string
        Workflow : string
    }

    type RetryRecording = {
        RecordingId : string
    }

    type ReprocessRecording = {
        RecordingId : string
        Workflow : string
    }

    type DeleteRecording = {
        RecordingId : string
    }

/// Command Union Type - All possible write operations
type Command =
    | ProcessRecording of CommandArgs.ProcessRecording
    | RetryRecording of CommandArgs.RetryRecording
    | ReprocessRecording of CommandArgs.ReprocessRecording
    | DeleteRecording of CommandArgs.DeleteRecording
    | SyncNow

/// Event Arguments
module EventArgs =
    type RecordingQueued = {
        RecordingId : string
        Workflow : string
    }

    type RecordingRetried = {
        RecordingId : string
        Step : RecordingStatus
    }

/// Event Union Type - All possible domain events
type Event =
    | RecordingQueued of EventArgs.RecordingQueued
    | RecordingRetried of EventArgs.RecordingRetried
    | RecordingReprocessQueued of EventArgs.RecordingQueued
    | RecordingDeleted of recordingId: string
    | SyncRequested

/// Command Handler Interface - Write operations
type RecordingsCommandHandler =
    abstract member Handle : Command -> Task<Event list>
