/// Keeps Loopback's own Plaud session alive (verified 2026-10-02, see
/// docs/riffado-plaud-sync-analysis.md section 8).
///
/// Loopback logs in with PLAUD_EMAIL / PLAUD_PASSWORD, so it has a login session of its own -
/// the browser using web.plaud.ai can no longer revoke it. Three layers, each renewing the
/// one below, cheapest first:
///   workspace token (data calls)  <- workspace refresh token (30 days, no user token)
///   user token (24 h, mints)      <- `pld_urt` refresh cookie (30 days, no password)
///   login session                 <- password login (only when everything else failed)
/// All of it is stored in SQLite.
module Loopback.Server.Features.Recordings.Sync.PlaudSession

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open Microsoft.Extensions.Logging
open Loopback.Server
open Loopback.Server.Configuration
open Loopback.Server.Integrations.Plaud
open Loopback.Server.Features.Recordings.Database

type ActiveSession = {
    ApiBase : string
    WorkspaceToken : string
}

let private hash (s: string) =
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Trim().ToLowerInvariant())))

let private ms (d: DateTimeOffset) = d.ToUnixTimeMilliseconds()

/// After a refused login, wait this long before trying again (Plaud throttles logins per
/// hour, and a wrong password stays wrong until .env changes - which needs a restart anyway).
let private loginBackoff = TimeSpan.FromMinutes 60.

type PlaudSession(connections: PlaudConnectionRepository, plaud: PlaudClient, cfg: Configuration, logger: ILogger<PlaudSession>) =
    let gate = new SemaphoreSlim(1, 1)
    let mutable lastError : string option = None
    let mutable loginBlocked : (DateTimeOffset * string) option = None
    let accountHash = hash cfg.Plaud.Email

    let userOf (row: PlaudConnectionRow) : UserSession = {
        ApiBase = row.ApiBase
        UserToken = row.UserToken
        UserTokenExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds row.UserTokenExpiresAt
        RefreshToken = row.UserRefreshToken
        RefreshExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds row.UserRefreshExpiresAt
    }

    let save (existing: PlaudConnectionRow option) (u: UserSession) (w: WorkspaceSession) =
        task {
            let row = {
                Id = 1L
                ApiBase = u.ApiBase
                WorkspaceId = w.WorkspaceId
                WorkspaceToken = w.WorkspaceToken
                WorkspaceTokenExpiresAt = ms w.WorkspaceTokenExpiresAt
                RefreshToken = w.RefreshToken
                RefreshExpiresAt = if w.RefreshToken = "" then 0L else ms w.RefreshExpiresAt
                AccountHash = accountHash
                LastSyncAt = existing |> Option.bind _.LastSyncAt
                LastError = None
                UpdatedAt = Db.now ()
                UserToken = u.UserToken
                UserTokenExpiresAt = ms u.UserTokenExpiresAt
                UserRefreshToken = u.RefreshToken
                UserRefreshExpiresAt = if u.RefreshToken = "" then 0L else ms u.RefreshExpiresAt
            }
            do! connections.Save row
            return row
        }

    let login () =
        task {
            if cfg.Plaud.Email = "" || cfg.Plaud.Password = "" then
                raise (PlaudLoginFailed "PLAUD_EMAIL and PLAUD_PASSWORD must be set in .env")
            match loginBlocked with
            | Some(until, reason) when until > DateTimeOffset.UtcNow ->
                let next = until.ToLocalTime().ToString "HH:mm"
                raise (PlaudLoginFailed $"{reason} - next login attempt at {next} (or fix .env and restart)")
            | _ -> ()
            logger.LogInformation("Logging in to Plaud as {Email}", cfg.Plaud.Email)
            try
                let! u = plaud.Login(cfg.Plaud.Email, cfg.Plaud.Password)
                loginBlocked <- None
                return u
            with PlaudLoginFailed m | PlaudError m ->
                // Any answer from Plaud that is not a session (only network errors retry right away).
                loginBlocked <- Some(DateTimeOffset.UtcNow.Add loginBackoff, m)
                return raise (PlaudLoginFailed m)
        }

    /// A user token to mint with: the stored one, else renewed via `pld_urt`, else a new login.
    /// `true` when it comes from a login just now (a rejection then is final).
    let userSession (existing: PlaudConnectionRow option) =
        task {
            let now = DateTimeOffset.UtcNow
            match existing with
            | Some row when row.AccountHash = accountHash && row.UserToken <> "" && row.UserTokenExpiresAt > ms (now.AddMinutes 5.) ->
                return userOf row, false
            | Some row when row.AccountHash = accountHash && row.UserRefreshToken <> "" && row.UserRefreshExpiresAt > ms now ->
                let! renewed =
                    task {
                        try
                            logger.LogInformation("Renewing the Plaud user token")
                            let! u = plaud.RefreshUserToken(row.ApiBase, row.UserRefreshToken)
                            return Some u
                        with PlaudUnauthorized m | PlaudSessionExpired m | PlaudError m ->
                            logger.LogWarning("Plaud user token renewal rejected ({Error}), logging in again", m)
                            return None
                    }
                match renewed with
                | Some u -> return u, false
                | None ->
                    let! u = login ()
                    return u, true
            | _ ->
                let! u = login ()
                return u, true
        }

    /// New workspace token from a user token (stored, renewed or freshly logged in).
    let mint (existing: PlaudConnectionRow option) =
        task {
            let start (u: UserSession) =
                task {
                    let! workspaceId = plaud.GetPersonalWorkspaceId(u.ApiBase, u.UserToken)
                    let! w = plaud.MintWorkspaceToken(u.ApiBase, u.UserToken, workspaceId)
                    return! save existing u w
                }
            let! u, fresh = userSession existing
            if fresh then return! start u
            else
                let! started =
                    task {
                        try
                            let! r = start u
                            return Ok r
                        with PlaudUnauthorized m | PlaudSessionExpired m -> return Error m
                    }
                match started with
                | Ok r -> return r
                | Error m ->
                    logger.LogWarning("Plaud login session revoked ({Error}), logging in again", m)
                    let! u = login ()
                    return! start u
        }

    let refreshWorkspace (row: PlaudConnectionRow) =
        task {
            logger.LogInformation("Refreshing Plaud workspace token")
            let! refreshed =
                task {
                    try
                        let! w = plaud.RefreshWorkspaceToken(row.ApiBase, row.RefreshToken, row.WorkspaceId)
                        return Some w
                    with PlaudSessionExpired m | PlaudUnauthorized m ->
                        logger.LogWarning("Plaud workspace refresh token rejected ({Error}), minting a new workspace token", m)
                        return None
                }
            match refreshed with
            | Some w -> return! save (Some row) (userOf row) w
            | None -> return! mint (Some row)
        }

    let resolve () =
        task {
            let! existing = connections.TryGet()
            let now = DateTimeOffset.UtcNow
            let isValid (r: PlaudConnectionRow) = r.WorkspaceTokenExpiresAt > ms (now.AddMinutes 5.)
            let isRefreshable (r: PlaudConnectionRow) = r.RefreshToken <> "" && r.RefreshExpiresAt > ms now
            match existing with
            | Some row when row.AccountHash = accountHash && isValid row -> return row
            | Some row when row.AccountHash = accountHash && isRefreshable row -> return! refreshWorkspace row
            | _ -> return! mint existing
        }

    /// A usable workspace token, renewing the session (or logging in) when needed.
    member _.GetSession() =
        task {
            do! gate.WaitAsync()
            try
                let! row = resolve ()
                return { ApiBase = row.ApiBase; WorkspaceToken = row.WorkspaceToken }
            finally
                gate.Release() |> ignore
        }

    /// Runs `f` with a session; if Plaud says the session was revoked, marks it dead
    /// (so the next resolve refreshes or re-mints) and retries once.
    member this.Use<'a>(f: ActiveSession -> System.Threading.Tasks.Task<'a>) : System.Threading.Tasks.Task<'a> =
        task {
            let! s = this.GetSession()
            try
                return! f s
            with PlaudSessionExpired m ->
                logger.LogWarning("Plaud workspace session revoked ({Error}), renewing", m)
                let! existing = connections.TryGet()
                match existing with
                | Some row -> do! connections.Save { row with WorkspaceTokenExpiresAt = 0L }
                | None -> ()
                let! renewed = this.GetSession()
                return! f renewed
        }

    /// Records the outcome of a sync run (shown in the UI).
    member _.RecordSync(error: string option) =
        task {
            lastError <- error
            let! existing = connections.TryGet()
            match existing with
            | Some row ->
                do! connections.Save { row with LastError = error; LastSyncAt = if error.IsNone then Some(Db.now ()) else row.LastSyncAt }
            | None -> ()
        }

    member _.LastError = lastError

    /// The Plaud account Loopback logs in with (PLAUD_EMAIL).
    member _.Account = cfg.Plaud.Email

    member _.GetConnection() = connections.TryGet()
