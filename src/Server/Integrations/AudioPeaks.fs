/// Waveform envelope of a recording (Ogg/Opus file), computed server-side.
/// Browsers would have to decode the whole file to PCM (~1 GB for 90 minutes), so the
/// backend streams it through Concentus instead: ~900x realtime, constant memory.
module Loopback.Server.Integrations.AudioPeaks

open System
open System.IO
open Concentus
open Concentus.Oggfile

/// Number of values stored per recording; the UI aggregates them to the bars it can show.
let buckets = 600

/// Decodes the audio file and returns `buckets` peak values normalised to [0, 1].
let compute (audioPath: string) =
    task {
        // The Ogg reader needs a seekable stream (on a network stream it fails to initialise
        // and silently yields no packets) - a file stream is one.
        use stream = File.OpenRead audioPath
        // Peaks are only an envelope, so decode at 8 kHz mono (cheapest the decoder offers).
        let decoder = OpusCodecFactory.CreateDecoder(8000, 1)
        let ogg = OpusOggReadStream(decoder, stream)
        // One value per Opus packet (~20 ms), aggregated into buckets at the end.
        let packetPeaks = ResizeArray<float32>()
        while ogg.HasNextPacket do
            match ogg.DecodeNextPacket() with
            | null -> ()
            | pcm ->
                let mutable peak = 0
                for s in pcm do
                    let a = abs (int s)
                    if a > peak then peak <- a
                packetPeaks.Add(float32 peak / 32768.f)
        if packetPeaks.Count = 0 then
            return failwith $"no audio decoded ({ogg.LastError})"
        else
            let perBucket = float packetPeaks.Count / float buckets
            let raw =
                Array.init buckets (fun b ->
                    let start = int (float b * perBucket)
                    let finish = max (start + 1) (int (float (b + 1) * perBucket)) |> min packetPeaks.Count
                    let mutable m = 0.f
                    for i in start .. finish - 1 do
                        if packetPeaks[i] > m then m <- packetPeaks[i]
                    m)
            let max' = Array.max raw
            return
                raw
                |> Array.map (fun v -> if max' > 0.f then Math.Round(float (v / max'), 3) else 0.)
    }
