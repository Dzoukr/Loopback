/// Advances queued recordings through the pipeline every `PROCESSING_POLL_SECONDS`.
/// Transcription (quick status polls) and summarization (minutes of Claude work) run in
/// separate loops, so a long summary never delays other recordings' transcription.
module Loopback.Server.Features.Recordings.Processing.ProcessingBackgroundService

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Loopback.Server.Configuration
open Loopback.Server.Features.Recordings.Domain
open Loopback.Server.Features.Recordings.Database
open Loopback.Server.Features.Recordings.Processing.Pipeline

type ProcessingBackgroundService(
    recordings: RecordingsRepository,
    pipeline: Pipeline,
    cfg: Configuration,
    logger: ILogger<ProcessingBackgroundService>) =
    inherit BackgroundService()

    let step (status: RecordingStatus) (run: RecordingRow -> Task) =
        task {
            let! rows = recordings.GetByStatus(RecordingStatus.toKey status)
            for r in rows do
                try
                    do! run r
                with ex ->
                    do! pipeline.Fail(r, status, ex)
        }

    let loop (name: string) (body: unit -> Task) (ct: CancellationToken) =
        task {
            while not ct.IsCancellationRequested do
                try
                    do! body ()
                with ex ->
                    logger.LogError(ex, "Processing loop {Loop} failed", name)
                try
                    do! Task.Delay(TimeSpan.FromSeconds(float cfg.Processing.PollSeconds), ct)
                with :? OperationCanceledException -> ()
        } :> Task

    /// Recordings processed before results were stored in SQLite (Migrations/005_Result.sql) get
    /// their result from the output file, if it is still there - so the UI can show it.
    let backfillResults () =
        task {
            let! rows = recordings.GetDoneWithoutResult()
            for r in rows do
                match r.OutputFile with
                | Some file when File.Exists(Path.Combine(cfg.Paths.Output, file)) ->
                    try
                        let json = File.ReadAllText(Path.Combine(cfg.Paths.Output, file))
                        // Only store it if it is valid JSON.
                        Text.Json.Nodes.JsonNode.Parse json |> ignore
                        do! recordings.SetResult(r.Id, json)
                        logger.LogInformation("Recording {Id}: result restored from {File}", r.Id, file)
                    with ex -> logger.LogWarning("Recording {Id}: could not restore the result from {File} ({Error})", r.Id, file, ex.Message)
                | _ -> ()
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            logger.LogInformation("Processing background service started (poll every {Seconds} s)", cfg.Processing.PollSeconds)
            try
                do! backfillResults ()
            with ex -> logger.LogError(ex, "Restoring results from output files failed")
            let transcription () =
                task {
                    do! step Queued (fun r -> pipeline.StartTranscription r :> Task)
                    do! step Transcribing (fun r -> pipeline.CheckTranscription r :> Task)
                } :> Task
            let summarization () = step Summarizing (fun r -> pipeline.Summarize r :> Task) :> Task
            let! _ = Task.WhenAll(loop "transcription" transcription stoppingToken, loop "summarization" summarization stoppingToken)
            return ()
        } :> Task
