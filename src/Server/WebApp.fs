module Loopback.Server.WebApp

open Giraffe.EndpointRouting
open Loopback.Server.Features

let api : Endpoint list =
    [
        subRoute "/api" [
            Recordings.API.api
        ]
    ]
