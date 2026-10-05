module Loopback.Server.Configuration

open System
open System.IO
open Microsoft.Extensions.Configuration

// All settings are environment variables (template: .env.example in the repo root).
// LoopbackDocker's docker-compose.yml passes them from .env via `env_file`; under `dotnet run` the repo's
// .env is loaded too (see Program.fs), real environment variables win.
// Paths (set by docker-compose.yml inside the container; under `dotnet run` also from .env, relative to
// the repo root; defaults relative to src/Server):
//   LOOPBACK_WORKFLOWS workflows folder         (default ../../workflows)
//   LOOPBACK_DATA     folder for loopback.db     (default ../../data)
//   LOOPBACK_OUTPUT   folder for result files    (default ../../output)
//   LOOPBACK_ENV_FILE .env loaded under `dotnet run` (default ../../.env, optional)

type PlaudConfiguration = {
    /// PLAUD_EMAIL / PLAUD_PASSWORD - Loopback logs in with them itself (own session, not the
    /// browser's). The password is only used when the stored session cannot be renewed.
    Email : string
    Password : string
    SyncIntervalMinutes : int
}

type SpeechmaticsConfiguration = {
    ApiKey : string
    Model : string
    Language : string
    /// A transcription job older than this is marked failed.
    MaxJobMinutes : int
}

/// Bridge connection only - model, effort, passes and timeout are per workflow (workflows/default.claude.json, workflows/<Name>/claude.json).
type ClaudeConfiguration = {
    BridgeUrl : string
    Token : string
}

type ProcessingConfiguration = {
    PollSeconds : int
}

type PathsConfiguration = {
    EnvFile : string
    Workflows : string
    Data : string
    Output : string
}

type Configuration = {
    Plaud : PlaudConfiguration
    Speechmatics : SpeechmaticsConfiguration
    Claude : ClaudeConfiguration
    Processing : ProcessingConfiguration
    Paths : PathsConfiguration
}

/// Minimal .env parser: KEY=VALUE lines, `#` comments, optional surrounding quotes.
let readDotEnv (path: string) : (string * string) list =
    if not (File.Exists path) then []
    else
        File.ReadAllLines path
        |> Array.map _.Trim()
        |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#") && l.Contains "=")
        |> Array.map (fun l ->
            let i = l.IndexOf '='
            let key = l.Substring(0, i).Trim()
            let value = l.Substring(i + 1).Trim()
            let value =
                if value.Length >= 2 && ((value.StartsWith "\"" && value.EndsWith "\"") || (value.StartsWith "'" && value.EndsWith "'"))
                then value.Substring(1, value.Length - 2)
                else value
            key, value)
        |> List.ofArray

/// Paths come from environment variables, then from .env (relative values resolve against the .env
/// folder, i.e. the repo root), then the defaults (relative to `contentRoot` - src/Server under `dotnet run`).
let paths (contentRoot: string) =
    let fromEnv (name: string) =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> None
        | p -> Some (Path.GetFullPath p)
    let envFile =
        fromEnv "LOOPBACK_ENV_FILE"
        |> Option.defaultWith (fun () -> Path.GetFullPath(Path.Combine(contentRoot, "../../.env")))
    let dotEnv = readDotEnv envFile |> Map.ofList
    let path (name: string) (fallback: string) =
        fromEnv name
        |> Option.orElseWith (fun () ->
            dotEnv |> Map.tryFind name
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map (fun p -> Path.GetFullPath(Path.Combine(Path.GetDirectoryName envFile, p))))
        |> Option.defaultWith (fun () -> Path.GetFullPath(Path.Combine(contentRoot, fallback)))
    {
        EnvFile = envFile
        Workflows = path "LOOPBACK_WORKFLOWS" "../../workflows"
        Data = path "LOOPBACK_DATA" "../../data"
        Output = path "LOOPBACK_OUTPUT" "../../output"
    }

let read (paths: PathsConfiguration) (config: IConfiguration) : Configuration =
    let str (key: string) (fallback: string) =
        match config[key] with
        | null | "" -> fallback
        | v -> v
    let int' (key: string) (fallback: int) =
        match Int32.TryParse(config[key]) with
        | true, v -> v
        | _ -> fallback
    {
        Plaud = {
            Email = (str "PLAUD_EMAIL" "").Trim()
            Password = str "PLAUD_PASSWORD" ""
            SyncIntervalMinutes = int' "PLAUD_SYNC_INTERVAL_MINUTES" 5 |> max 1
        }
        Speechmatics = {
            ApiKey = str "SPEECHMATICS_API_KEY" ""
            Model = str "SPEECHMATICS_MODEL" "melia-1"
            Language = str "SPEECHMATICS_LANGUAGE" "multi"
            MaxJobMinutes = int' "SPEECHMATICS_MAX_JOB_MINUTES" 60
        }
        Claude = {
            BridgeUrl = (str "CLAUDE_BRIDGE_URL" "http://host.docker.internal:9100").TrimEnd('/')
            Token = str "CLAUDE_BRIDGE_TOKEN" ""
        }
        Processing = {
            PollSeconds = int' "PROCESSING_POLL_SECONDS" 10 |> max 2
        }
        Paths = paths
    }
