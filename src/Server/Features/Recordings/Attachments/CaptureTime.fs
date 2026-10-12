/// When an attached file came into existence, and where that places it in the recording
/// (docs/attachments.md, Capture Time). Detection order: file name, EXIF, last-modified time.
module Loopback.Server.Features.Recordings.Attachments.CaptureTime

open System
open System.Globalization
open System.Text.RegularExpressions
open MetadataExtractor
open MetadataExtractor.Formats.Exif
open Loopback.Server.Features.Recordings.Domain

/// A capture time this far outside the recording still counts as during the call (clamped to it).
let tolerance = TimeSpan.FromSeconds 60.

// A date (yyyy-MM-dd or yyyyMMdd), a separator (space, _, -, T or " at "), then a time HH mm ss whose
// parts share one separator (_ - . : or none). Anything around it is ignored:
// "2026-10-09 11_52_44-Teams", "Screenshot 2026-10-09 at 14.35.12", "Screenshot_20261009-143512", "PXL_20261009_143512123".
let private fileNamePattern =
    Regex(
        @"(?<!\d)(?:(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})|(?<y>\d{4})(?<mo>\d{2})(?<d>\d{2}))(?:[ _]at[ _]|[ _T-])(?<h>\d{2})(?<sep>[_.:-]?)(?<mi>\d{2})\k<sep>(?<s>\d{2})",
        RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// Plausible capture times only: not before 2000, not more than a day in the future.
let private plausible (now: DateTimeOffset) (t: DateTimeOffset) =
    t.Year >= 2000 && t <= now.AddDays 1.

/// Local date and time from the file name (no offset - the name holds the local time of the capturing device).
let fromFileName (fileName: string) : DateTime option =
    fileNamePattern.Matches fileName
    |> Seq.tryPick (fun m ->
        let n (group: string) = Int32.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture)
        try Some(DateTime(n "y", n "mo", n "d", n "h", n "mi", n "s", DateTimeKind.Unspecified))
        with :? ArgumentOutOfRangeException -> None)

/// EXIF DateTimeOriginal, with OffsetTimeOriginal when the file has it.
let fromExif (path: string) : (DateTime * TimeSpan option) option =
    try
        let directories = ImageMetadataReader.ReadMetadata path
        directories
        |> Seq.tryPick (fun d ->
            match d with
            | :? ExifSubIfdDirectory as exif ->
                match exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal) with
                | true, t ->
                    let offset =
                        match exif.GetString(ExifDirectoryBase.TagTimeZoneOriginal) with
                        | null -> None
                        | s ->
                            // "+02:00" / "-05:00"; TimeSpan parses a leading minus, not a plus.
                            match TimeSpan.TryParse(s.Trim().TrimStart('+'), CultureInfo.InvariantCulture) with
                            | true, o -> Some o
                            | _ -> None
                    Some(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), offset)
                | _ -> None
            | _ -> None)
    with _ -> None

/// Capture time and its source, first plausible match wins. A time `reported` by the source (Plaud) is
/// exact and wins over everything. `localOffset` is the recording's UTC offset: the file name and EXIF
/// hold the local time of where the call happened.
let detect (reported: DateTimeOffset option) (fileName: string) (path: string) (fileModifiedAt: DateTimeOffset option) (localOffset: TimeSpan) (now: DateTimeOffset) =
    let local (t: DateTime) (offset: TimeSpan) = DateTimeOffset(t, offset)
    let candidates =
        seq {
            match reported with
            | Some t -> yield t, FromPlaud
            | None -> ()
            match fromFileName fileName with
            | Some t -> yield local t localOffset, FromFileName
            | None -> ()
            match fromExif path with
            | Some(t, offset) -> yield local t (defaultArg offset localOffset), FromExif
            | None -> ()
            match fileModifiedAt with
            | Some t -> yield t, FromModified
            | None -> ()
        }
    candidates
    |> Seq.tryFind (fst >> plausible now)
    |> Option.map (fun (t, source) -> Some t, source)
    |> Option.defaultValue (None, NoTime)

/// During call (with the offset, clamped to the recording) when the capture time is within the recording
/// plus the tolerance on both ends; Reference otherwise.
let placement (recordingStart: DateTimeOffset) (duration: TimeSpan) (capturedAt: DateTimeOffset option) =
    match capturedAt with
    | Some t ->
        let offset = t - recordingStart
        if offset >= -tolerance && offset <= duration + tolerance then
            DuringCall(Math.Clamp(offset.TotalSeconds, 0., max 0. duration.TotalSeconds))
        else
            Reference
    | None -> Reference
