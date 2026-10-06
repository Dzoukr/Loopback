/// The audio formats Loopback stores, all decoded in pure .NET: Ogg/Opus (what Plaud stores,
/// Concentus), plus uploaded MP3 (NLayer) and Ogg/Vorbis (NVorbis) - kept as uploaded.
/// The waveform envelope is computed server-side: browsers would have to decode the whole file
/// to PCM (~1 GB for 90 minutes), the decoders stream it in constant memory.
module Loopback.Server.Integrations.AudioFiles

open System
open System.IO
open System.Text
open Concentus
open Concentus.Oggfile
open NLayer
open NVorbis

/// The file is not audio Loopback can decode.
exception UnsupportedAudio of string

type AudioFormat =
    | Opus
    | Vorbis
    | Mp3

module AudioFormat =
    /// Opus and Vorbis are both Ogg files.
    let extension =
        function
        | Opus | Vorbis -> ".ogg"
        | Mp3 -> ".mp3"

    /// File extensions of stored audio, Plaud's first.
    let extensions = [ ".ogg"; ".mp3" ]

    let contentTypeOf (path: string) =
        if Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase) then "audio/mpeg" else "audio/ogg"

/// Format from the file's first bytes (the extension of an upload means nothing).
let detect (path: string) : AudioFormat option =
    let head =
        use stream = File.OpenRead path
        let buffer = Array.zeroCreate<byte> 512
        let n = stream.Read(buffer, 0, buffer.Length)
        buffer[.. n - 1]
    let text = Encoding.ASCII.GetString head
    if text.StartsWith "OggS" then
        // The first Ogg page holds the codec's identification header.
        if text.Contains "OpusHead" then Some Opus
        elif text.Contains "\001vorbis" then Some Vorbis
        else None
    elif text.StartsWith "ID3" then Some Mp3
    // Bare MPEG audio frame sync (11 set bits).
    elif head.Length >= 2 && head[0] = 0xFFuy && head[1] &&& 0xE0uy = 0xE0uy then Some Mp3
    else None

/// Duration from the container / frame headers (no full decode). Fails for a file the decoder
/// cannot read.
let duration (path: string) (format: AudioFormat) : TimeSpan =
    let total =
        try
            match format with
            | Opus ->
                use stream = File.OpenRead path
                OpusOggReadStream(OpusCodecFactory.CreateDecoder(48000, 1), stream).TotalTime
            | Vorbis ->
                use reader = new VorbisReader(path)
                reader.TotalTime
            | Mp3 ->
                use reader = new MpegFile(path)
                reader.Duration
        with ex -> raise (UnsupportedAudio $"the audio cannot be read ({ex.Message})")
    if total <= TimeSpan.Zero then raise (UnsupportedAudio "the file contains no audio")
    total

/// Number of values stored per recording; the UI aggregates them to the bars it can show.
let buckets = 600

/// Peak of each ~20 ms window, normalised to [0, 1] (Opus packets are 20 ms, so one per packet).
let private windowPeaks (path: string) (format: AudioFormat) =
    let peaks = ResizeArray<float32>()
    /// Interleaved float samples read by `read` into windows of 20 ms.
    let readFloats (sampleRate: int) (channels: int) (read: float32[] -> int) =
        let buffer = Array.zeroCreate<float32> (max channels (sampleRate * channels / 50))
        let mutable n = read buffer
        while n > 0 do
            let mutable peak = 0.f
            for i in 0 .. n - 1 do
                let a = abs buffer[i]
                if a > peak then peak <- a
            peaks.Add(min 1.f peak)
            n <- read buffer
    match format with
    | Opus ->
        // The Ogg reader needs a seekable stream (on a network stream it fails to initialise
        // and silently yields no packets) - a file stream is one.
        use stream = File.OpenRead path
        // Peaks are only an envelope, so decode at 8 kHz mono (cheapest the decoder offers).
        let ogg = OpusOggReadStream(OpusCodecFactory.CreateDecoder(8000, 1), stream)
        while ogg.HasNextPacket do
            match ogg.DecodeNextPacket() with
            | null -> ()
            | pcm ->
                let mutable peak = 0
                for s in pcm do
                    let a = abs (int s)
                    if a > peak then peak <- a
                peaks.Add(float32 peak / 32768.f)
        if peaks.Count = 0 then failwith $"no audio decoded ({ogg.LastError})"
    | Vorbis ->
        use reader = new VorbisReader(path)
        readFloats reader.SampleRate reader.Channels (fun b -> reader.ReadSamples(b, 0, b.Length))
    | Mp3 ->
        use reader = new MpegFile(path)
        readFloats reader.SampleRate reader.Channels (fun b -> reader.ReadSamples(b, 0, b.Length))
    peaks

/// Decodes the audio file and returns `buckets` peak values normalised to [0, 1]
/// (Opus ~900x realtime: 91 minutes in ~6 s; MP3 / Vorbis decode at full quality, slower).
let computePeaks (audioPath: string) =
    task {
        let format =
            match detect audioPath with
            | Some f -> f
            | None -> failwith "unknown audio format"
        let packetPeaks = windowPeaks audioPath format
        if packetPeaks.Count = 0 then
            return failwith "no audio decoded"
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
