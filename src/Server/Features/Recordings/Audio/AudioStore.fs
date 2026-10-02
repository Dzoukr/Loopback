/// Local copies of the recordings' audio (Ogg/Opus, as Plaud stores it) in <data>/audio/{id}.ogg.
/// Downloaded once right after sync (AudioBackgroundService), so playback, waveform and
/// transcription need no Plaud connection afterwards.
module Loopback.Server.Features.Recordings.Audio.AudioStore

open System
open System.IO
open System.Net.Http
open System.Threading
open Loopback.Server.Configuration
open Loopback.Server.Integrations.Plaud
open Loopback.Server.Features.Recordings.Sync.PlaudSession

type AudioStore(session: PlaudSession, plaud: PlaudClient, cfg: Configuration) =

    let folder = Path.Combine(cfg.Paths.Data, "audio")
    // ~2 MB per 8 minutes, a 90-minute recording is ~22 MB.
    let http = new HttpClient(Timeout = TimeSpan.FromMinutes 10.)
    // One download at a time, so the background job and an on-demand request never fetch the same file twice.
    let gate = new SemaphoreSlim(1, 1)

    member _.PathOf(recordingId: string) =
        if recordingId = "" || recordingId |> Seq.exists (fun c -> not (Char.IsLetterOrDigit c || c = '-' || c = '_')) then
            invalidArg (nameof recordingId) $"unexpected recording id '{recordingId}'"
        Path.Combine(folder, $"{recordingId}.ogg")

    member this.Exists(recordingId: string) = File.Exists(this.PathOf recordingId)

    /// Path of the stored audio; downloads it from Plaud first if it is not stored yet.
    member this.Ensure(recordingId: string) =
        task {
            let path = this.PathOf recordingId
            if File.Exists path then return path
            else
                do! gate.WaitAsync()
                try
                    if not (File.Exists path) then
                        Directory.CreateDirectory folder |> ignore
                        let! url = session.Use(fun s -> plaud.GetAudioUrl(s.ApiBase, s.WorkspaceToken, recordingId))
                        let temp = $"{path}.{Guid.NewGuid():N}.tmp"
                        try
                            use! resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                            if not resp.IsSuccessStatusCode then
                                raise (PlaudError $"audio download for {recordingId} -> HTTP {int resp.StatusCode}")
                            do! task {
                                use file = File.Create temp
                                do! resp.Content.CopyToAsync file
                            }
                            File.Move(temp, path, true)
                        finally
                            if File.Exists temp then File.Delete temp
                    return path
                finally
                    gate.Release() |> ignore
        }

    member this.Delete(recordingId: string) =
        let path = this.PathOf recordingId
        if File.Exists path then File.Delete path
