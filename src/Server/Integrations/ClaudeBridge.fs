/// Client for claude-bridge.fsx running on the host (see docs/loopback-foundation.md, "Claude Bridge").
module Loopback.Server.Integrations.ClaudeBridge

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

exception BridgeError of string

type RunResult = {
    /// Plain-text answer (for title runs).
    Result : string
    /// Schema-constrained object (for runs with a schema), else null.
    StructuredOutput : JsonNode
}

/// `http` should have an infinite timeout: each call gets its own deadline from `timeoutSeconds`.
type ClaudeBridgeClient(http: HttpClient, bridgeUrl: string, token: string) =
    let uri = Uri(bridgeUrl)

    let localRequest (meth: HttpMethod) (path: string) =
        let req = new HttpRequestMessage(meth, Uri(uri, path))
        // The bridge's HttpListener listens on 127.0.0.1; send a matching Host header
        // even though the request travels via host.docker.internal.
        req.Headers.Host <- $"127.0.0.1:{uri.Port}"
        req

    /// The bridge's MAX_CONCURRENCY, or None when the bridge is not reachable.
    member _.GetMaxConcurrency() =
        task {
            try
                use req = localRequest HttpMethod.Get "/health"
                use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds 5.)
                use! resp = http.SendAsync(req, cts.Token)
                let! text = resp.Content.ReadAsStringAsync()
                match JsonNode.Parse(text)["maxConcurrency"] with
                | null -> return None
                | n -> return Some(n.GetValue<int>())
            with _ -> return None
        }

    /// One claude call; `timeoutSeconds` is enforced by the bridge (it kills claude and answers 504),
    /// the client waits 60 s longer so the bridge's clear error always arrives first.
    member _.Run(system: string, prompt: string, model: string, effort: string, timeoutSeconds: int, schema: string option) =
        task {
            let body = JsonObject()
            body["system"] <- JsonValue.Create system
            body["prompt"] <- JsonValue.Create prompt
            body["model"] <- JsonValue.Create model
            body["effort"] <- JsonValue.Create effort
            body["timeoutSeconds"] <- JsonValue.Create timeoutSeconds
            schema |> Option.iter (fun s -> body["schema"] <- JsonNode.Parse s)
            use req = localRequest HttpMethod.Post "/run"
            req.Headers.TryAddWithoutValidation("X-Bridge-Token", token) |> ignore
            req.Content <- new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            let! resp =
                task {
                    // Waiting for a free MAX_CONCURRENCY slot in the bridge counts against this too.
                    use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(float timeoutSeconds + 60.))
                    try
                        return! http.SendAsync(req, cts.Token)
                    with
                    | :? OperationCanceledException ->
                        return raise (BridgeError $"Claude bridge did not answer within {timeoutSeconds + 60}s")
                    | :? HttpRequestException as ex ->
                        return raise (BridgeError $"Claude bridge not reachable at {bridgeUrl} - is claude-bridge.fsx running on the host? ({ex.Message})")
                }
            use resp = resp
            let! text = resp.Content.ReadAsStringAsync()
            let node = try JsonNode.Parse text with _ -> null
            if not resp.IsSuccessStatusCode then
                let msg = if isNull node || isNull node["error"] then text else node["error"].ToString()
                raise (BridgeError $"Claude bridge -> HTTP {int resp.StatusCode}: {msg}")
            match node with
            | null -> return raise (BridgeError "Claude bridge returned a non-JSON body")
            | n ->
                let result =
                    match n["result"] with
                    | null -> ""
                    | r when r.GetValueKind() = JsonValueKind.String -> r.GetValue<string>()
                    | r -> r.ToJsonString()
                return { Result = result; StructuredOutput = n["structuredOutput"] }
        }
