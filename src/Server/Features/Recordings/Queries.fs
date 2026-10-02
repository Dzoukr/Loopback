module Loopback.Server.Features.Recordings.Queries

open System
open Loopback.Server.Features.Recordings.Domain
open Loopback.Server.Features.Recordings.Database
open Loopback.Server.Features.Recordings.Sync.PlaudSession
open Loopback.Server.Features.Recordings.Audio.AudioStore
open Loopback.Server.Features.Recordings.Processing.Workflows

let private toDate (ms: int64) = DateTimeOffset.FromUnixTimeMilliseconds ms

let private toRecording (r: RecordingRow) : Queries.Recording =
    {
        Id = r.Id
        Filename = r.Filename
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
    }

/// SQLite-backed implementation of RecordingsQueries
type StorageQueries(recordings: RecordingsRepository, workflows: WorkflowCatalog, session: PlaudSession, audio: AudioStore) =

    interface RecordingsQueries with
        member _.GetRecordings() =
            task {
                let! rows = recordings.GetAll()
                return rows |> List.filter (fun r -> r.Status <> RecordingStatus.toKey Deleted) |> List.map toRecording
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
                    let! path = audio.Ensure r.Id
                    return Some path
                | _ -> return None
            }
