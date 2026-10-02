/// Workflows = subfolders of workflows/ (see docs/loopback-foundation.md, "Processing").
///   workflows/default.schema.json      schema used when a workflow has no own schema.json
///   workflows/default.claude.json      Claude settings for all workflows (model, effort, passes, timeoutSeconds)
///   workflows/<Name>/title.md          system prompt for the title (plain text)
///   workflows/<Name>/summary.md        system prompt for transcript -> information
///   workflows/<Name>/merge.md          system prompt for merging the ensemble passes
///   workflows/<Name>/schema.json       optional, overrides default.schema.json
///   workflows/<Name>/claude.json       optional, overrides single keys of default.claude.json
/// Files are read on every call, so edits apply without a restart.
module Loopback.Server.Features.Recordings.Processing.Workflows

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Json.Schema
open Microsoft.Extensions.Logging

/// How the Claude bridge runs a workflow: one model + effort for title, summary passes and merge
/// (the merge must never run on a weaker model than the passes it merges).
type ClaudeSettings = {
    Model : string
    Effort : string
    /// Identical summary passes run in parallel and merged; 1 = single pass, no merge.
    Passes : int
    /// Max seconds per claude call (the bridge caps it at 3600).
    TimeoutSeconds : int
}

/// Used when workflows/default.claude.json is absent or does not set a key.
let defaultClaudeSettings = { Model = "sonnet"; Effort = "high"; Passes = 3; TimeoutSeconds = 300 }

// Same pattern as default.schema.json / schema.json.
let private defaultClaudeFile = "default.claude.json"
let private claudeFile = "claude.json"
let private modelAliases = set [ "haiku"; "sonnet"; "opus" ]
let private fullModelId = Regex(@"^claude-[a-z0-9][a-z0-9.\-]*$")
let private efforts = set [ "low"; "medium"; "high"; "xhigh"; "max" ]
let private maxPasses = 10
let private maxTimeoutSeconds = 3600

type Workflow = {
    Name : string
    TitlePrompt : string
    SummaryPrompt : string
    MergePrompt : string
    /// Compact JSON Schema text for summary + merge runs.
    Schema : string
    HasCustomSchema : bool
    Claude : ClaudeSettings
}

let private read (path: string) =
    if File.Exists path then (File.ReadAllText path).Trim() else ""

/// Parses and compacts a schema; None when the file is missing or not a valid JSON Schema.
let private loadSchema (path: string) =
    match read path with
    | "" -> None
    | text ->
        try
            JsonSchema.FromText text |> ignore
            Some(JsonNode.Parse(text).ToJsonString())
        with _ -> None

/// Applies the keys set in a Claude settings file on top of `baseline` (missing file = no change).
/// Unknown keys and invalid values are errors, so a typo is caught when workflows are listed -
/// not after the recording was already transcribed (and paid for).
let private applyClaudeConfig (path: string) (baseline: ClaudeSettings) : Result<ClaudeSettings, string> =
    match read path with
    | "" -> Ok baseline
    | text ->
        match (try Ok(JsonNode.Parse(text, documentOptions = JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true))) with ex -> Error ex.Message) with
        | Error e -> Error $"{path} is not valid JSON ({e})"
        | Ok(:? JsonObject as o) ->
            let str (n: JsonNode) = if n.GetValueKind() = JsonValueKind.String then Some(n.GetValue<string>()) else None
            o
            |> Seq.fold (fun (acc: Result<ClaudeSettings, string>) kv ->
                acc
                |> Result.bind (fun s ->
                    match kv.Key, kv.Value with
                    | "model", v when not (isNull v) ->
                        (match str v with
                         | Some m when modelAliases.Contains m || fullModelId.IsMatch m -> Ok { s with Model = m }
                         | _ -> Error $"{path}: model must be haiku | sonnet | opus or a full id like claude-sonnet-5-5")
                    | "effort", v when not (isNull v) ->
                        (match str v with
                         | Some e when efforts.Contains e -> Ok { s with Effort = e }
                         | _ -> Error $"""{path}: effort must be one of {String.Join(" | ", efforts)}""")
                    | "passes", v when not (isNull v) ->
                        (match (try Some(v.GetValue<int>()) with _ -> None) with
                         | Some p when p >= 1 && p <= maxPasses -> Ok { s with Passes = p }
                         | _ -> Error $"{path}: passes must be a number from 1 to {maxPasses}")
                    | "timeoutSeconds", v when not (isNull v) ->
                        (match (try Some(v.GetValue<int>()) with _ -> None) with
                         | Some t when t >= 1 && t <= maxTimeoutSeconds -> Ok { s with TimeoutSeconds = t }
                         | _ -> Error $"{path}: timeoutSeconds must be a number from 1 to {maxTimeoutSeconds}")
                    | key, _ -> Error $"{path}: unknown key '{key}' (allowed: model, effort, passes, timeoutSeconds)")) (Ok baseline)
        | Ok _ -> Error $"{path} must contain a JSON object"

/// Validates a claude result against the workflow's schema.
let validate (schema: string) (result: JsonNode) : Result<unit, string> =
    let evaluation = JsonSchema.FromText(schema).Evaluate(JsonDocument.Parse(result.ToJsonString()).RootElement)
    if evaluation.IsValid then Ok() else Error "result does not match the workflow's JSON schema"

type WorkflowCatalog(workflowsPath: string, logger: ILogger<WorkflowCatalog>) =

    let tryLoad (folder: string) =
        let name = Path.GetFileName folder
        let title, summary, merge =
            read (Path.Combine(folder, "title.md")), read (Path.Combine(folder, "summary.md")), read (Path.Combine(folder, "merge.md"))
        let custom = Path.Combine(folder, "schema.json")
        let hasCustom = File.Exists custom
        let schema = loadSchema (if hasCustom then custom else Path.Combine(workflowsPath, "default.schema.json"))
        // Shared config first, then the workflow's own overrides (key by key).
        let claude =
            applyClaudeConfig (Path.Combine(workflowsPath, defaultClaudeFile)) defaultClaudeSettings
            |> Result.bind (applyClaudeConfig (Path.Combine(folder, claudeFile)))
        if title = "" || summary = "" || merge = "" then
            logger.LogWarning("Workflow {Name} skipped: title.md, summary.md and merge.md are all required", name)
            None
        else
            match schema, claude with
            | None, _ ->
                logger.LogWarning("Workflow {Name} skipped: its schema is missing or not a valid JSON Schema", name)
                None
            | _, Error e ->
                logger.LogWarning("Workflow {Name} skipped: {Error}", name, e)
                None
            | Some s, Ok c ->
                Some { Name = name; TitlePrompt = title; SummaryPrompt = summary; MergePrompt = merge; Schema = s; HasCustomSchema = hasCustom; Claude = c }

    member _.List() =
        if Directory.Exists workflowsPath then
            Directory.GetDirectories workflowsPath
            |> Array.sort
            |> Array.choose tryLoad
            |> List.ofArray
        else
            logger.LogWarning("Workflows folder {Path} does not exist", workflowsPath)
            []

    member _.TryLoad(name: string) =
        let folder = Path.Combine(workflowsPath, name)
        // Only plain folder names - never a path out of workflows/.
        if name <> Path.GetFileName name || not (Directory.Exists folder) then None
        else tryLoad folder
