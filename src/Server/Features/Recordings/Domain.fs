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

/// Where a recording comes from: synced from Plaud, or an audio file uploaded in the UI.
type RecordingSource =
    | Plaud
    | Upload

module RecordingSource =
    let toKey =
        function
        | Plaud -> "plaud"
        | Upload -> "upload"

    let fromKey =
        function
        | "plaud" -> Plaud
        | "upload" -> Upload
        | other -> failwith $"Unknown recording source '{other}'"

/// How an attachment is previewed, detected from the file's first bytes (docs/attachments.md).
type AttachmentKind =
    | Image
    | Pdf
    | Text
    | Other

module AttachmentKind =
    let toKey =
        function
        | Image -> "image"
        | Pdf -> "pdf"
        | Text -> "text"
        | Other -> "other"

    let fromKey =
        function
        | "image" -> Image
        | "pdf" -> Pdf
        | "text" -> Text
        | "other" -> Other
        | other -> failwith $"Unknown attachment kind '{other}'"

/// Where an attachment's capture time comes from: reported by Plaud (imported marks), else first match
/// of file name, EXIF, the file's last-modified time; manual = set by the user, none = unknown.
type TimeSource =
    | FromPlaud
    | FromFileName
    | FromExif
    | FromModified
    | Manual
    | NoTime

module TimeSource =
    let toKey =
        function
        | FromPlaud -> "plaud"
        | FromFileName -> "filename"
        | FromExif -> "exif"
        | FromModified -> "modified"
        | Manual -> "manual"
        | NoTime -> "none"

    let fromKey =
        function
        | "plaud" -> FromPlaud
        | "filename" -> FromFileName
        | "exif" -> FromExif
        | "modified" -> FromModified
        | "manual" -> Manual
        | "none" -> NoTime
        | other -> failwith $"Unknown time source '{other}'"

/// Where an attachment or mark comes from: dropped / pasted in Loopback, or imported from a Plaud mark.
type ItemSource =
    | Uploaded
    | FromPlaudMark

module ItemSource =
    let toKey =
        function
        | Uploaded -> "upload"
        | FromPlaudMark -> "plaud"

    let fromKey =
        function
        | "upload" -> Uploaded
        | "plaud" -> FromPlaudMark
        | other -> failwith $"Unknown item source '{other}'"

/// What a mark is: a device button press, a note typed in the Plaud app, or a spoken voice memo.
type MarkKind =
    | HighlightMark
    | NoteMark
    | VoiceMark

module MarkKind =
    let toKey =
        function
        | HighlightMark -> "highlight"
        | NoteMark -> "note"
        | VoiceMark -> "voice"

    let fromKey =
        function
        | "highlight" -> HighlightMark
        | "note" -> NoteMark
        | "voice" -> VoiceMark
        | other -> failwith $"Unknown mark kind '{other}'"

/// Derived from the capture time and the recording's start + duration, never stored.
type Placement =
    /// Captured during the recording, seconds from its start.
    | DuringCall of offsetSeconds: float
    /// Outside the recording or unknown - belongs to the call as a whole.
    | Reference

/// Query Models - Read-side representations
module Queries =
    type Attachment = {
        Id : string
        RecordingId : string
        FileName : string
        ContentType : string
        Kind : AttachmentKind
        SizeBytes : int64
        CapturedAt : DateTimeOffset option
        TimeSource : TimeSource
        Note : string option
        Source : ItemSource
        /// Machine-made text: Plaud's photo description, a voice memo's transcript.
        Description : string option
        Placement : Placement
        CreatedAt : DateTimeOffset
    }

    /// A moment in the call without a file (a Plaud highlight, text note or voice memo), or a note
    /// added in Loopback - at a moment or about the call as a whole.
    type Mark = {
        Id : string
        RecordingId : string
        Kind : MarkKind
        /// Seconds from the recording start (clamped to the recording); None = no time.
        OffsetSeconds : float option
        Note : string option
        Source : ItemSource
        /// Plaud is still analyzing it - the note comes later.
        Pending : bool
    }

    /// The stored file of an attachment, for serving it.
    type AttachmentFile = {
        Path : string
        FileName : string
        ContentType : string
        Kind : AttachmentKind
    }

    type Recording = {
        Id : string
        Filename : string
        Source : RecordingSource
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
        /// The transcript is stored (transcription finished), viewable via GetTranscript.
        HasTranscript : bool
        /// Ordered: during-call by offset, then reference by file name.
        Attachments : Attachment list
        /// Done, and attachments were added / changed / deleted after the result was produced.
        AttachmentsOutdated : bool
        /// By offset, then the ones without a time by creation.
        Marks : Mark list
    }

    /// One speaker turn of the stored transcript.
    type TranscriptSegment = {
        Speaker : string
        /// Seconds from the start of the recording.
        Start : float
        End : float
        Text : string
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
    /// Speaker segments of the stored transcript; empty for an unknown recording or one not transcribed yet.
    abstract member GetTranscript : string -> Task<Queries.TranscriptSegment list>
    /// The stored file of an attachment (recording id, attachment id); None if unknown or the recording is deleted.
    abstract member GetAttachmentFile : recordingId: string * attachmentId: string -> Task<Queries.AttachmentFile option>

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

    type UploadRecording = {
        /// Uploaded file (MP3 or Ogg); the caller deletes it afterwards.
        AudioFile : string
        /// Original file name, without extension - the title until the workflow names it.
        Filename : string
        StartTime : DateTimeOffset
        UtcOffsetMinutes : int
        /// Queue it for processing right away.
        Workflow : string option
    }

    type AddAttachment = {
        RecordingId : string
        /// Uploaded file; the caller deletes it afterwards.
        File : string
        /// Original file name incl. extension.
        FileName : string
        /// The file's last-modified time from the browser; None for clipboard pastes (it is the paste time there).
        FileModifiedAt : DateTimeOffset option
        Source : ItemSource
        /// Capture time reported by the source (Plaud), wins over detection.
        ReportedCapturedAt : DateTimeOffset option
        /// Machine-made text (Plaud's description, a voice memo's transcript).
        Description : string option
    }

    type AttachmentTime =
        /// Seconds from the recording's start.
        | AtOffset of float
        /// The call as a whole.
        | AsReference
        /// Drops the manual time and detects it again.
        | Detected

    type SetAttachmentTime = {
        AttachmentId : string
        Time : AttachmentTime
    }

    type SetAttachmentNote = {
        AttachmentId : string
        /// None (or blank) removes the note.
        Note : string option
    }

    type DeleteAttachment = {
        AttachmentId : string
    }

    /// A note added in Loopback (kind note).
    type AddMark = {
        RecordingId : string
        /// Seconds from the recording's start; None = about the call as a whole.
        OffsetSeconds : float option
        Note : string
    }

    type SetMarkNote = {
        MarkId : string
        /// None (or blank) removes the note.
        Note : string option
    }

    type DeleteMark = {
        MarkId : string
    }

/// Command Union Type - All possible write operations
type Command =
    | ProcessRecording of CommandArgs.ProcessRecording
    | RetryRecording of CommandArgs.RetryRecording
    | ReprocessRecording of CommandArgs.ReprocessRecording
    | DeleteRecording of CommandArgs.DeleteRecording
    | UploadRecording of CommandArgs.UploadRecording
    /// Deletes every processed (done) recording, as DeleteRecording does one.
    | DeleteProcessed
    | SyncNow
    | AddAttachment of CommandArgs.AddAttachment
    | SetAttachmentTime of CommandArgs.SetAttachmentTime
    | SetAttachmentNote of CommandArgs.SetAttachmentNote
    | DeleteAttachment of CommandArgs.DeleteAttachment
    | AddMark of CommandArgs.AddMark
    | SetMarkNote of CommandArgs.SetMarkNote
    | DeleteMark of CommandArgs.DeleteMark

/// Event Arguments
module EventArgs =
    type AttachmentAdded = {
        RecordingId : string
        AttachmentId : string
    }

    type RecordingQueued = {
        RecordingId : string
        Workflow : string
    }

    type RecordingUploaded = {
        RecordingId : string
        Workflow : string option
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
    | RecordingUploaded of EventArgs.RecordingUploaded
    | SyncRequested
    | AttachmentAdded of EventArgs.AttachmentAdded
    /// The same file (SHA-256) is already attached to the recording - nothing was stored.
    | AttachmentAlreadyExists of EventArgs.AttachmentAdded
    | AttachmentUpdated of attachmentId: string
    | AttachmentDeleted of attachmentId: string
    | MarkAdded of markId: string
    | MarkUpdated of markId: string
    | MarkDeleted of markId: string

/// The attachment cannot be added (too large, too many, deleted recording) - the message says why.
exception AttachmentRejected of string

/// Command Handler Interface - Write operations
type RecordingsCommandHandler =
    abstract member Handle : Command -> Task<Event list>
