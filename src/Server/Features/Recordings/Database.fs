module Loopback.Server.Features.Recordings.Database

open Dapper
open Dapper.FSharp.SQLite
open Loopback.Server.Db

// Rows map 1:1 to the tables in Migrations/001_Init.sql (timestamps = unix ms).
// CLIMutable: Dapper materializes via setters, which honours the option type handlers
// (constructor matching does not).

[<CLIMutable>]
type RecordingRow = {
    Id : string
    Filename : string
    StartTime : int64
    DurationMs : int64
    Filesize : int64
    VersionMs : string
    Status : string
    Workflow : string option
    Title : string option
    Error : string option
    FailedStep : string option
    SpeechmaticsJobId : string option
    Transcript : string option
    OutputFile : string option
    StepStartedAt : int64 option
    CreatedAt : int64
    UpdatedAt : int64
    DeletedAt : int64 option
    /// Waveform peaks as JSON array (Migrations/002_Peaks.sql).
    Peaks : string option
    /// Local UTC offset of the recording (Migrations/004_UtcOffset.sql).
    UtcOffsetMinutes : int64 option
    /// Result file JSON of a done recording (Migrations/005_Result.sql).
    Result : string option
    /// plaud | upload (Migrations/007_Source.sql).
    Source : string
}

[<CLIMutable>]
type PlaudConnectionRow = {
    Id : int64
    ApiBase : string
    WorkspaceId : string
    WorkspaceToken : string
    WorkspaceTokenExpiresAt : int64
    RefreshToken : string
    RefreshExpiresAt : int64
    /// SHA-256 of the lower-cased PLAUD_EMAIL; another account starts a new session.
    AccountHash : string
    LastSyncAt : int64 option
    LastError : string option
    UpdatedAt : int64
    /// Loopback's own login session (Migrations/006_PlaudLogin.sql).
    UserToken : string
    UserTokenExpiresAt : int64
    /// `pld_urt` cookie value - renews the user token without the password.
    UserRefreshToken : string
    UserRefreshExpiresAt : int64
}

[<CLIMutable>]
type AudioCandidateRow = {
    Id : string
    Source : string
    /// SQLite boolean (0 / 1).
    MissingPeaks : int64
}

/// Migrations/008_Attachments.sql
[<CLIMutable>]
type AttachmentRow = {
    Id : string
    RecordingId : string
    FileName : string
    ContentType : string
    Kind : string
    SizeBytes : int64
    Sha256 : string
    CapturedAt : int64 option
    TimeSource : string
    FileModifiedAt : int64 option
    Note : string option
    CreatedAt : int64
    UpdatedAt : int64
    /// upload | plaud (Migrations/009_PlaudMarks.sql).
    Source : string
    Description : string option
    ReportedCapturedAt : int64 option
    /// The model that wrote Description, None when not by Claude (Migrations/011_AttachmentDescriptions.sql).
    DescriptionModel : string option
}

/// Migrations/009_PlaudMarks.sql
[<CLIMutable>]
type MarkRow = {
    Id : string
    RecordingId : string
    OffsetMs : int64
    Note : string option
    Source : string
    /// SQLite boolean (0 / 1).
    Pending : int64
    UserEdited : int64
    CreatedAt : int64
    UpdatedAt : int64
    /// highlight | note | voice (Migrations/010_VoiceMemoMarks.sql).
    Kind : string
}

[<CLIMutable>]
type PlaudMarkImportRow = {
    RecordingId : string
    MarkKey : string
    TargetId : string
    Pending : int64
    ImportedAt : int64
}

/// A Plaud recording whose marks may need importing (Recordings columns of 009_PlaudMarks.sql).
[<CLIMutable>]
type PlaudMarksCandidateRow = {
    Id : string
    VersionMs : string
    StartTime : int64
    DurationMs : int64
    PlaudMarksVersion : string option
    PlaudMarksCheckedAt : int64 option
    /// Imported highlights still analyzing in Plaud.
    PendingMarks : int64
}

[<CLIMutable>]
type AttachmentsChangedRow = {
    Id : string
    AttachmentsChangedAt : int64 option
}

let private recordingsTable = table'<RecordingRow> "Recordings"
let private connectionTable = table'<PlaudConnectionRow> "PlaudConnection"
let private attachmentsTable = table'<AttachmentRow> "Attachments"
let private marksTable = table'<MarkRow> "Marks"
let private importsTable = table'<PlaudMarkImportRow> "PlaudMarkImports"

type RecordingsRepository(factory: DbConnectionFactory) =

    member _.GetAll() =
        task {
            use conn = factory.Open()
            let! rows =
                select {
                    for r in recordingsTable do
                    orderByDescending r.StartTime
                } |> conn.SelectAsync<RecordingRow>
            return List.ofSeq rows
        }

    member _.TryGet(id: string) =
        task {
            use conn = factory.Open()
            let! rows =
                select {
                    for r in recordingsTable do
                    where (r.Id = id)
                } |> conn.SelectAsync<RecordingRow>
            return Seq.tryHead rows
        }

    /// Live (not tombstoned) recordings in the given status, oldest step first.
    member _.GetByStatus(status: string) =
        task {
            use conn = factory.Open()
            let! rows =
                select {
                    for r in recordingsTable do
                    where (r.Status = status && isNullValue r.DeletedAt)
                    orderBy r.UpdatedAt
                } |> conn.SelectAsync<RecordingRow>
            return List.ofSeq rows
        }

    /// Recordings that need local audio and a waveform, newest first - any status but deleted, so
    /// processed ones stay playable too. Ids only (no transcript / result columns), polled often.
    member _.GetAudioCandidates() =
        task {
            use conn = factory.Open()
            let! rows =
                conn.QueryAsync<AudioCandidateRow>(
                    "SELECT Id, Source, Peaks IS NULL AS MissingPeaks FROM Recordings WHERE Status <> 'deleted' ORDER BY StartTime DESC")
            return rows |> Seq.map (fun r -> {| Id = r.Id; Source = r.Source; MissingPeaks = r.MissingPeaks <> 0L |}) |> List.ofSeq
        }

    /// Last attachment change per recording (AttachmentsChangedAt is not part of RecordingRow, see 008_Attachments.sql).
    member _.GetAttachmentsChangedAt() =
        task {
            use conn = factory.Open()
            let! rows = conn.QueryAsync<AttachmentsChangedRow>("SELECT Id, AttachmentsChangedAt FROM Recordings WHERE AttachmentsChangedAt IS NOT NULL")
            return rows |> Seq.choose (fun r -> r.AttachmentsChangedAt |> Option.map (fun at -> r.Id, at)) |> Map.ofSeq
        }

    /// Records that the recording's attachments changed (only this column, never a concurrent status change).
    member _.TouchAttachments(id: string) =
        task {
            use conn = factory.Open()
            let! _ = conn.ExecuteAsync("UPDATE Recordings SET AttachmentsChangedAt = @Now WHERE Id = @Id", {| Id = id; Now = now () |})
            return ()
        }

    /// The Plaud list's is_markmemo (only this column).
    member _.SetHasPlaudMarks(id: string, hasMarks: bool) =
        task {
            use conn = factory.Open()
            let! _ = conn.ExecuteAsync("UPDATE Recordings SET HasPlaudMarks = @HasMarks WHERE Id = @Id", {| Id = id; HasMarks = (if hasMarks then 1 else 0) |})
            return ()
        }

    /// Live Plaud recordings with marks, with what the marks import needs to decide whether they are due.
    member _.GetPlaudMarksCandidates() =
        task {
            use conn = factory.Open()
            let! rows =
                conn.QueryAsync<PlaudMarksCandidateRow>(
                    "SELECT r.Id, r.VersionMs, r.StartTime, r.DurationMs, r.PlaudMarksVersion, r.PlaudMarksCheckedAt,
                            (SELECT COUNT(*) FROM PlaudMarkImports i WHERE i.RecordingId = r.Id AND i.Pending = 1) AS PendingMarks
                     FROM Recordings r
                     WHERE r.Source = 'plaud' AND r.Status <> 'deleted' AND r.HasPlaudMarks = 1
                     ORDER BY r.StartTime DESC")
            return List.ofSeq rows
        }

    /// Marks fetched for this `version_ms` (only these columns).
    member _.SetPlaudMarksChecked(id: string, version: string) =
        task {
            use conn = factory.Open()
            let! _ = conn.ExecuteAsync("UPDATE Recordings SET PlaudMarksVersion = @Version, PlaudMarksCheckedAt = @Now WHERE Id = @Id", {| Id = id; Version = version; Now = now () |})
            return ()
        }

    /// Done recordings without a stored result (processed before results were stored).
    member _.GetDoneWithoutResult() =
        task {
            use conn = factory.Open()
            let! rows =
                select {
                    for r in recordingsTable do
                    where (r.Status = "done" && isNullValue r.Result)
                } |> conn.SelectAsync<RecordingRow>
            return List.ofSeq rows
        }

    /// Sets only the Result column.
    member _.SetResult(id: string, result: string) =
        task {
            use conn = factory.Open()
            let! _ =
                update {
                    for r in recordingsTable do
                    setColumn r.Result (Some result)
                    where (r.Id = id)
                } |> conn.UpdateAsync
            return ()
        }

    /// Sets only the Peaks column, so it never overwrites a concurrent status change.
    member _.SetPeaks(id: string, peaks: string) =
        task {
            use conn = factory.Open()
            let! _ =
                update {
                    for r in recordingsTable do
                    setColumn r.Peaks (Some peaks)
                    where (r.Id = id)
                } |> conn.UpdateAsync
            return ()
        }

    /// Plaud rows that may be removed once their Plaud file is gone: processed (done), never-processed
    /// (synced) and deleted-in-Loopback ones. Failed / in-progress rows and uploads are kept.
    member _.GetRemovableIfGoneFromPlaud() =
        task {
            use conn = factory.Open()
            let! rows =
                select {
                    for r in recordingsTable do
                    where (r.Source = "plaud" && (r.Status = "done" || r.Status = "synced" || r.Status = "deleted"))
                } |> conn.SelectAsync<RecordingRow>
            return List.ofSeq rows
        }

    /// Deletes the row only if it is still done / synced / deleted (never a recording queued meanwhile).
    member _.DeleteIfRemovable(id: string) =
        task {
            use conn = factory.Open()
            let! deleted =
                delete {
                    for r in recordingsTable do
                    where (r.Id = id && r.Source = "plaud" && (r.Status = "done" || r.Status = "synced" || r.Status = "deleted"))
                } |> conn.DeleteAsync
            return deleted > 0
        }

    member _.Delete(id: string) =
        task {
            use conn = factory.Open()
            let! _ =
                delete {
                    for r in recordingsTable do
                    where (r.Id = id)
                } |> conn.DeleteAsync
            return ()
        }

    member _.Insert(row: RecordingRow) =
        task {
            use conn = factory.Open()
            let! _ = insert { into recordingsTable; value row } |> conn.InsertAsync
            return ()
        }

    member _.Update(row: RecordingRow) =
        task {
            use conn = factory.Open()
            let! _ =
                update {
                    for r in recordingsTable do
                    set { row with UpdatedAt = now () }
                    where (r.Id = row.Id)
                } |> conn.UpdateAsync
            return ()
        }

type AttachmentsRepository(factory: DbConnectionFactory) =

    member _.GetAll() =
        task {
            use conn = factory.Open()
            let! rows = select { for a in attachmentsTable do selectAll } |> conn.SelectAsync<AttachmentRow>
            return List.ofSeq rows
        }

    member _.GetByRecording(recordingId: string) =
        task {
            use conn = factory.Open()
            let! rows =
                select {
                    for a in attachmentsTable do
                    where (a.RecordingId = recordingId)
                } |> conn.SelectAsync<AttachmentRow>
            return List.ofSeq rows
        }

    member _.TryGet(id: string) =
        task {
            use conn = factory.Open()
            let! rows =
                select {
                    for a in attachmentsTable do
                    where (a.Id = id)
                } |> conn.SelectAsync<AttachmentRow>
            return Seq.tryHead rows
        }

    member _.Insert(row: AttachmentRow) =
        task {
            use conn = factory.Open()
            let! _ = insert { into attachmentsTable; value row } |> conn.InsertAsync
            return ()
        }

    member _.Update(row: AttachmentRow) =
        task {
            use conn = factory.Open()
            let! _ =
                update {
                    for a in attachmentsTable do
                    set { row with UpdatedAt = now () }
                    where (a.Id = row.Id)
                } |> conn.UpdateAsync
            return ()
        }

    member _.Delete(id: string) =
        task {
            use conn = factory.Open()
            let! _ =
                delete {
                    for a in attachmentsTable do
                    where (a.Id = id)
                } |> conn.DeleteAsync
            return ()
        }

    member _.DeleteByRecording(recordingId: string) =
        task {
            use conn = factory.Open()
            let! _ =
                delete {
                    for a in attachmentsTable do
                    where (a.RecordingId = recordingId)
                } |> conn.DeleteAsync
            return ()
        }

    /// Claude's description of the attachment (only these columns - not a user change, so AttachmentsChangedAt stays).
    member _.SetDescription(id: string, description: string, model: string) =
        task {
            use conn = factory.Open()
            let! _ = conn.ExecuteAsync("UPDATE Attachments SET Description = @Description, DescriptionModel = @Model WHERE Id = @Id", {| Id = id; Description = description; Model = model |})
            return ()
        }

    /// A cached description of this file content by this model.
    member _.TryGetCachedDescription(sha256: string, model: string) =
        task {
            use conn = factory.Open()
            let! rows = conn.QueryAsync<string>("SELECT Description FROM AttachmentDescriptions WHERE Sha256 = @Sha256 AND Model = @Model", {| Sha256 = sha256; Model = model |})
            return Seq.tryHead rows
        }

    member _.CacheDescription(sha256: string, model: string, description: string) =
        task {
            use conn = factory.Open()
            let! _ =
                conn.ExecuteAsync(
                    "INSERT OR REPLACE INTO AttachmentDescriptions (Sha256, Model, Description, CreatedAt) VALUES (@Sha256, @Model, @Description, @Now)",
                    {| Sha256 = sha256; Model = model; Description = description; Now = now () |})
            return ()
        }

    /// Rows whose recording is gone or deleted (e.g. removed while the server was down mid-delete).
    member _.DeleteOrphans() =
        task {
            use conn = factory.Open()
            return! conn.ExecuteAsync("DELETE FROM Attachments WHERE RecordingId NOT IN (SELECT Id FROM Recordings WHERE Status <> 'deleted')")
        }

type MarksRepository(factory: DbConnectionFactory) =

    member _.GetAll() =
        task {
            use conn = factory.Open()
            let! rows = select { for m in marksTable do selectAll } |> conn.SelectAsync<MarkRow>
            return List.ofSeq rows
        }

    member _.GetByRecording(recordingId: string) =
        task {
            use conn = factory.Open()
            let! rows =
                select {
                    for m in marksTable do
                    where (m.RecordingId = recordingId)
                } |> conn.SelectAsync<MarkRow>
            return List.ofSeq rows
        }

    member _.TryGet(id: string) =
        task {
            use conn = factory.Open()
            let! rows =
                select {
                    for m in marksTable do
                    where (m.Id = id)
                } |> conn.SelectAsync<MarkRow>
            return Seq.tryHead rows
        }

    member _.Insert(row: MarkRow) =
        task {
            use conn = factory.Open()
            let! _ = insert { into marksTable; value row } |> conn.InsertAsync
            return ()
        }

    member _.Update(row: MarkRow) =
        task {
            use conn = factory.Open()
            let! _ =
                update {
                    for m in marksTable do
                    set { row with UpdatedAt = now () }
                    where (m.Id = row.Id)
                } |> conn.UpdateAsync
            return ()
        }

    member _.Delete(id: string) =
        task {
            use conn = factory.Open()
            let! _ =
                delete {
                    for m in marksTable do
                    where (m.Id = id)
                } |> conn.DeleteAsync
            return ()
        }

    /// Marks and Plaud import keys of the recording.
    member _.DeleteByRecording(recordingId: string) =
        task {
            use conn = factory.Open()
            let! _ = conn.ExecuteAsync("DELETE FROM Marks WHERE RecordingId = @Id; DELETE FROM PlaudMarkImports WHERE RecordingId = @Id", {| Id = recordingId |})
            return ()
        }

    /// Marks and import keys whose recording is gone or deleted.
    member _.DeleteOrphans() =
        task {
            use conn = factory.Open()
            let live = "SELECT Id FROM Recordings WHERE Status <> 'deleted'"
            let! marks = conn.ExecuteAsync($"DELETE FROM Marks WHERE RecordingId NOT IN ({live})")
            let! imports = conn.ExecuteAsync($"DELETE FROM PlaudMarkImports WHERE RecordingId NOT IN ({live})")
            return marks + imports
        }

    member _.GetImports(recordingId: string) =
        task {
            use conn = factory.Open()
            let! rows =
                select {
                    for i in importsTable do
                    where (i.RecordingId = recordingId)
                } |> conn.SelectAsync<PlaudMarkImportRow>
            return List.ofSeq rows
        }

    member _.InsertImport(row: PlaudMarkImportRow) =
        task {
            use conn = factory.Open()
            let! _ = insert { into importsTable; value row } |> conn.InsertAsync
            return ()
        }

    member _.ResolveImport(recordingId: string, markKey: string) =
        task {
            use conn = factory.Open()
            let! _ = conn.ExecuteAsync("UPDATE PlaudMarkImports SET Pending = 0 WHERE RecordingId = @RecordingId AND MarkKey = @MarkKey", {| RecordingId = recordingId; MarkKey = markKey |})
            return ()
        }

type PlaudConnectionRepository(factory: DbConnectionFactory) =

    member _.TryGet() =
        task {
            use conn = factory.Open()
            let! rows =
                select {
                    for c in connectionTable do
                    where (c.Id = 1L)
                } |> conn.SelectAsync<PlaudConnectionRow>
            return Seq.tryHead rows
        }

    member this.Save(row: PlaudConnectionRow) =
        task {
            let row = { row with Id = 1L; UpdatedAt = now () }
            let! existing = this.TryGet()
            use conn = factory.Open()
            match existing with
            | Some _ ->
                let! _ =
                    update {
                        for c in connectionTable do
                        set row
                        where (c.Id = 1L)
                    } |> conn.UpdateAsync
                return ()
            | None ->
                let! _ = insert { into connectionTable; value row } |> conn.InsertAsync
                return ()
        }
