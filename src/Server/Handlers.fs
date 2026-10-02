module Loopback.Server.Handlers

open System.Threading.Tasks
open Giraffe
open Microsoft.AspNetCore.Http

/// Runs `fn` and writes its result as JSON. Handlers expect happy-path input;
/// anything unexpected throws and surfaces as HTTP 500.
let simpleJson<'out> (fn: HttpContext -> Task<'out>) : HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let! result = fn ctx
        return! json result next ctx
    }

/// Like `simpleJson`, for routes with one string route parameter (routef "%s").
let simpleJsonWith<'out> (fn: string -> HttpContext -> Task<'out>) (arg: string) : HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let! result = fn arg ctx
        return! json result next ctx
    }
