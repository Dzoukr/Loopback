module Loopback.Server.Program

open System
open System.Collections.Generic
open System.Net.Http
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Giraffe
open Giraffe.EndpointRouting
open Giraffe.OpenApi
open Loopback.Server.Configuration
open Loopback.Server.Db
open Loopback.Server.Serialization
open Loopback.Server.Integrations.Plaud
open Loopback.Server.Integrations.Speechmatics
open Loopback.Server.Integrations.ClaudeBridge
open Loopback.Server.Features.Recordings

let private getConfiguration (builder: WebApplicationBuilder) =
    let paths = Configuration.paths builder.Environment.ContentRootPath
    // Settings are environment variables (Configuration.fs). Under `dotnet run` the repo's .env
    // is loaded as well; environment variables still win (e.g. CLAUDE_BRIDGE_URL=http://127.0.0.1:9100
    // outside Docker, where host.docker.internal resolves to the LAN IP the bridge does not listen on).
    let dotEnv = Configuration.readDotEnv paths.EnvFile |> List.map (fun (k, v) -> KeyValuePair<string, string>(k, v))
    builder.Configuration.AddInMemoryCollection(dotEnv).AddEnvironmentVariables() |> ignore
    Configuration.read paths builder.Configuration

let private configureDatabase (cfg: Configuration) (builder: WebApplicationBuilder) =
    Dapper.FSharp.SQLite.OptionTypes.register ()
    let factory = DbConnectionFactory(cfg.Paths.Data)
    builder.Services.AddSingleton<DbConnectionFactory>(factory) |> ignore
    builder.Services.AddSingleton<Database.RecordingsRepository>() |> ignore
    builder.Services.AddSingleton<Database.PlaudConnectionRepository>() |> ignore
    builder

let private configureServices (cfg: Configuration) (builder: WebApplicationBuilder) =
    builder.Services.AddSingleton<Configuration>(cfg) |> ignore

    // Integrations (one HttpClient each; the bridge waits for long Claude runs)
    let http (timeout: TimeSpan) = new HttpClient(Timeout = timeout)
    // No cookie container: PlaudClient sends the `pld_urt` refresh cookie explicitly.
    builder.Services.AddSingleton<PlaudClient>(
        PlaudClient(new HttpClient(new HttpClientHandler(UseCookies = false), Timeout = TimeSpan.FromSeconds 60.))) |> ignore
    // Long timeout: the audio is uploaded with the job (~22 MB for 90 minutes).
    builder.Services.AddSingleton<SpeechmaticsClient>(
        SpeechmaticsClient(http (TimeSpan.FromMinutes 10.), cfg.Speechmatics.ApiKey, cfg.Speechmatics.Model, cfg.Speechmatics.Language)) |> ignore
    builder.Services.AddSingleton<ClaudeBridgeClient>(
        ClaudeBridgeClient(http Threading.Timeout.InfiniteTimeSpan, cfg.Claude.BridgeUrl, cfg.Claude.Token)) |> ignore

    // Recordings: sync + processing
    builder.Services.AddSingleton<Sync.PlaudSession.PlaudSession>() |> ignore
    builder.Services.AddSingleton<Sync.SyncTrigger.SyncTrigger>() |> ignore
    builder.Services.AddSingleton<Audio.AudioStore.AudioStore>() |> ignore
    builder.Services.AddSingleton<Processing.Workflows.WorkflowCatalog>(fun sp ->
        Processing.Workflows.WorkflowCatalog(cfg.Paths.Workflows, sp.GetRequiredService<ILogger<Processing.Workflows.WorkflowCatalog>>())) |> ignore
    builder.Services.AddSingleton<Processing.Pipeline.Pipeline>() |> ignore
    builder.Services.AddScoped<Domain.RecordingsQueries, Queries.StorageQueries>() |> ignore
    builder.Services.AddScoped<Domain.RecordingsCommandHandler, CommandHandler.StorageCommandHandler>() |> ignore
    builder.Services.AddHostedService<Sync.SyncBackgroundService.SyncBackgroundService>() |> ignore
    builder.Services.AddHostedService<Processing.ProcessingBackgroundService.ProcessingBackgroundService>() |> ignore
    builder.Services.AddHostedService<Audio.AudioBackgroundService.AudioBackgroundService>() |> ignore
    builder

let private configureWeb (builder: WebApplicationBuilder) =
    builder.Services.AddRouting() |> ignore
    builder.Services.AddGiraffe() |> ignore
    builder.Services.AddSingleton<Json.ISerializer>(Json.FsharpFriendlySerializer(jsonOptions = Serialization.options)) |> ignore
    if builder.Environment.IsDevelopment() then
        builder.Services.AddOpenApi("v1", fun options ->
            options.AddSchemaTransformer<FSharpLuLikeSchemaTransformer>() |> ignore
            options.AddSchemaTransformer<FSharpOptionSchemaTransformer>() |> ignore
            options.AddSchemaTransformer<NumericPatternRemoverTransformer>() |> ignore
            options.AddSchemaTransformer<NonNullableStringTransformer>() |> ignore
        ) |> ignore
    builder

let private configureApp (cfg: Configuration) (app: WebApplication) =
    let logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Loopback")
    migrate (app.Services.GetRequiredService<DbConnectionFactory>()) logger
    logger.LogInformation("Workflows {Workflows}, data {Data}, output {Output}", cfg.Paths.Workflows, cfg.Paths.Data, cfg.Paths.Output)
    if String.IsNullOrWhiteSpace cfg.Plaud.Email || String.IsNullOrWhiteSpace cfg.Plaud.Password || String.IsNullOrWhiteSpace cfg.Speechmatics.ApiKey || String.IsNullOrWhiteSpace cfg.Claude.Token then
        logger.LogWarning("PLAUD_EMAIL, PLAUD_PASSWORD, SPEECHMATICS_API_KEY and CLAUDE_BRIDGE_TOKEN must all be set (see .env.example)")
    app.UseRouting() |> ignore
    if app.Environment.IsDevelopment() then
        app.MapOpenApi() |> ignore
        app.UseDeveloperExceptionPage() |> ignore
        app.UseSwaggerUI(_.SwaggerEndpoint("/openapi/v1.json", "Loopback API V1")) |> ignore
    app.UseEndpoints(_.MapGiraffeEndpoints(WebApp.api)) |> ignore
    app

let private builder = WebApplication.CreateBuilder(WebApplicationOptions())
let private cfg = getConfiguration builder

let app =
    builder
    |> configureDatabase cfg
    |> configureServices cfg
    |> configureWeb
    |> _.Build()
    |> configureApp cfg

app.Run()
