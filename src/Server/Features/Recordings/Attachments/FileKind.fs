/// Detects how an attachment is previewed from the file's first bytes - never from the extension
/// or the browser's MIME type alone (docs/attachments.md, Previews).
module Loopback.Server.Features.Recordings.Attachments.FileKind

open System
open System.IO
open System.Text
open Loopback.Server.Features.Recordings.Domain

/// Extensions previewed as plain text, when the content is valid UTF-8.
let private textExtensions = set [ ".txt"; ".md"; ".markdown"; ".csv"; ".json"; ".log" ]

/// Content types of files that are not previewed (download only), so exports keep a meaningful type.
let private otherTypes =
    dict [
        ".heic", "image/heic"
        ".heif", "image/heif"
        ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"
        ".zip", "application/zip"
        ".mp3", "audio/mpeg"
        ".ogg", "audio/ogg"
        ".m4a", "audio/mp4"
        ".mp4", "video/mp4"
    ]

let private startsWith (prefix: byte[]) (head: byte[]) =
    head.Length >= prefix.Length && head.AsSpan(0, prefix.Length).SequenceEqual(ReadOnlySpan prefix)

let private ascii (s: string) = Encoding.ASCII.GetBytes s

/// Image / PDF type from the file's first bytes. SVG is XML text and never matches - it can carry scripts.
let private sniff (head: byte[]) =
    if startsWith [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |] head then Some(Image, "image/png")
    elif startsWith [| 0xFFuy; 0xD8uy; 0xFFuy |] head then Some(Image, "image/jpeg")
    elif startsWith (ascii "GIF87a") head || startsWith (ascii "GIF89a") head then Some(Image, "image/gif")
    elif startsWith (ascii "RIFF") head && head.Length >= 12 && Encoding.ASCII.GetString(head, 8, 4) = "WEBP" then Some(Image, "image/webp")
    elif startsWith (ascii "%PDF-") head then Some(Pdf, "application/pdf")
    else None

let private isUtf8Text (path: string) =
    try
        let bytes = File.ReadAllBytes path
        // NUL never appears in text files, but often early in binary ones.
        not (Array.contains 0uy bytes) && (UTF8Encoding(false, true).GetString bytes |> ignore; true)
    with :? DecoderFallbackException -> false

/// (kind, content type) of a stored file; `fileName` is the original name (only its extension is used, for text).
let detect (path: string) (fileName: string) =
    let head =
        use s = File.OpenRead path
        let buffer = Array.zeroCreate<byte> 16
        let n = s.Read(buffer, 0, buffer.Length)
        buffer[.. n - 1]
    let extension = Path.GetExtension(fileName).ToLowerInvariant()
    match sniff head with
    | Some found -> found
    | None when textExtensions.Contains extension && isUtf8Text path -> Text, "text/plain; charset=utf-8"
    | None ->
        match otherTypes.TryGetValue extension with
        | true, contentType -> Other, contentType
        | _ -> Other, "application/octet-stream"
