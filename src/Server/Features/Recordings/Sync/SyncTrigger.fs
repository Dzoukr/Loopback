module Loopback.Server.Features.Recordings.Sync.SyncTrigger

open System
open System.Threading

/// Lets the API wake the sync background service before its next scheduled run.
type SyncTrigger() =
    let signal = new SemaphoreSlim(0, 1)

    member _.Request() =
        // Max count 1: repeated requests while a sync is pending collapse into one.
        try signal.Release() |> ignore with :? SemaphoreFullException -> ()

    /// Completes after `timeout`, or earlier when a sync was requested.
    member _.Wait(timeout: TimeSpan, ct: CancellationToken) =
        task {
            let! _ = signal.WaitAsync(timeout, ct)
            return ()
        }
