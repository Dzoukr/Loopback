/// Right after sync (and for any older recording still missing them) downloads the recording's
/// audio into the local store and computes its waveform peaks from it, so the player works
/// immediately and without Plaud. One recording at a time; ~10 s for 90 minutes.
module Loopback.Server.Features.Recordings.Audio.AudioBackgroundService

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Loopback.Server.Configuration
open Loopback.Server.Integrations
open Loopback.Server.Features.Recordings.Database
open Loopback.Server.Features.Recordings.Audio.AudioStore
open Loopback.Server.Features.Recordings.Processing.Pipeline

/// A recording whose download or peaks failed is retried only after this long.
let private retryAfter = TimeSpan.FromMinutes 30.

type AudioBackgroundService(
    recordings: RecordingsRepository,
    audio: AudioStore,
    cfg: Configuration,
    logger: ILogger<AudioBackgroundService>) =
    inherit BackgroundService()

    let failedAt = Dictionary<string, DateTimeOffset>()

    let processNext () =
        task {
            let! candidates = recordings.GetAudioCandidates()
            let due =
                candidates
                |> List.tryFind (fun c ->
                    (c.MissingPeaks || not (audio.Exists c.Id))
                    && match failedAt.TryGetValue c.Id with
                       | true, at -> DateTimeOffset.UtcNow - at > retryAfter
                       | _ -> true)
            match due with
            | None -> return false
            | Some c ->
                try
                    let downloaded = not (audio.Exists c.Id)
                    let! path = audio.Ensure c.Id
                    if downloaded then
                        logger.LogInformation("Recording {Id}: audio stored ({Size:N1} MB)", c.Id, float (IO.FileInfo(path).Length) / 1048576.)
                    if c.MissingPeaks then
                        let! peaks = AudioPeaks.compute path
                        do! recordings.SetPeaks(c.Id, JsonSerializer.Serialize peaks)
                        logger.LogInformation("Recording {Id}: waveform peaks computed", c.Id)
                    failedAt.Remove c.Id |> ignore
                with ex ->
                    failedAt[c.Id] <- DateTimeOffset.UtcNow
                    logger.LogWarning("Recording {Id}: audio download / waveform peaks failed ({Error})", c.Id, describe ex)
                return true
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    // Drain the backlog, then wait for the next poll.
                    let mutable more = true
                    while more && not stoppingToken.IsCancellationRequested do
                        let! worked = processNext ()
                        more <- worked
                with ex ->
                    logger.LogError(ex, "Audio loop failed")
                try
                    do! Task.Delay(TimeSpan.FromSeconds(float cfg.Processing.PollSeconds), stoppingToken)
                with :? OperationCanceledException -> ()
        } :> Task
