module Loopback.Server.OpenAPI

open Giraffe.OpenApi

// ============================================================================
// OpenAPI Documentation Helpers
// ============================================================================

let doc<'inp,'out> opId types = addOpenApi(OpenApiConfig(
    requestBody = RequestBody(typeof<'inp>, types),
    responseBodies = [| ResponseBody(typeof<'out>, types) |],
    configureOperation = (fun o _ _ ->
        task {
            o.OperationId <- opId
        })))

let docOut<'out> opId types = addOpenApi(OpenApiConfig(
    responseBodies = [| ResponseBody(typeof<'out>, types) |],
    configureOperation = (fun o _ _ ->
        task {
            o.OperationId <- opId
        })))

let jsonInOut<'inp,'out> opId = doc<'inp,'out> opId [| "application/json" |]
let jsonOut<'out> opId = docOut<'out> opId [| "application/json" |]
