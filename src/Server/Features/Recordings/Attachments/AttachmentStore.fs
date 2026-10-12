/// Files attached to recordings, in <data>/attachments/{recordingId}/{attachmentId}.bin. The original
/// file name is never part of a path; the type is stored in the database (docs/attachments.md).
module Loopback.Server.Features.Recordings.Attachments.AttachmentStore

open System
open System.IO
open Loopback.Server.Configuration

/// Largest accepted attachment.
let maxFileBytes = 50L * 1024L * 1024L
let maxFileMegabytes = maxFileBytes / 1024L / 1024L
/// Most attachments one recording can have.
let maxPerRecording = 100

type AttachmentStore(cfg: Configuration) =

    let folder = Path.Combine(cfg.Paths.Data, "attachments")

    let checkId (name: string) (id: string) =
        if id = "" || id |> Seq.exists (fun c -> not (Char.IsLetterOrDigit c || c = '-' || c = '_')) then
            invalidArg name $"unexpected id '{id}'"

    let folderOf (recordingId: string) =
        checkId (nameof recordingId) recordingId
        Path.Combine(folder, recordingId)

    member _.PathOf(recordingId: string, attachmentId: string) =
        checkId (nameof attachmentId) attachmentId
        Path.Combine(folderOf recordingId, attachmentId + ".bin")

    /// Copies an uploaded file into the store (via a temp file, so a crash never leaves a partial file).
    member this.Import(recordingId: string, attachmentId: string, sourcePath: string) =
        let path = this.PathOf(recordingId, attachmentId)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temp = $"{path}.{Guid.NewGuid():N}.tmp"
        try
            File.Copy(sourcePath, temp)
            File.Move(temp, path, true)
        finally
            if File.Exists temp then File.Delete temp

    member this.Delete(recordingId: string, attachmentId: string) =
        let path = this.PathOf(recordingId, attachmentId)
        if File.Exists path then File.Delete path
        let dir = folderOf recordingId
        if Directory.Exists dir && Seq.isEmpty (Directory.EnumerateFileSystemEntries dir) then Directory.Delete dir

    /// Removes every attachment file of the recording.
    member _.DeleteAll(recordingId: string) =
        let dir = folderOf recordingId
        if Directory.Exists dir then Directory.Delete(dir, true)

    /// Deletes files (and folders) without an attachment row - e.g. a crash between storing the
    /// file and inserting its row. `known` = (recordingId, attachmentId) of every row. Returns the count.
    member _.DeleteOrphans(known: Set<string * string>) =
        if not (Directory.Exists folder) then 0
        else
            let mutable removed = 0
            for dir in Directory.EnumerateDirectories folder |> List.ofSeq do
                let recordingId = Path.GetFileName dir
                for file in Directory.EnumerateFiles dir |> List.ofSeq do
                    let isKnown =
                        Path.GetExtension file = ".bin" && known.Contains(recordingId, Path.GetFileNameWithoutExtension file)
                    if not isKnown then
                        File.Delete file
                        removed <- removed + 1
                if Seq.isEmpty (Directory.EnumerateFileSystemEntries dir) then Directory.Delete dir
            removed
