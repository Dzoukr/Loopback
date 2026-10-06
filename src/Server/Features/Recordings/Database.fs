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

let private recordingsTable = table'<RecordingRow> "Recordings"
let private connectionTable = table'<PlaudConnectionRow> "PlaudConnection"

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
