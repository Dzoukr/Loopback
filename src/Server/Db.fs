module Loopback.Server.Db

open System
open System.IO
open System.Reflection
open Dapper
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging

let now () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

/// Opens connections to the SQLite file in the data folder.
type DbConnectionFactory(dataFolder: string) =
    let path = Path.Combine(dataFolder, "loopback.db")
    let connectionString = SqliteConnectionStringBuilder(DataSource = path, Pooling = true).ToString()

    member _.Path = path

    member _.Open() =
        let conn = new SqliteConnection(connectionString)
        conn.Open()
        // Background jobs and API requests write concurrently; wait instead of failing on a lock.
        conn.Execute("PRAGMA busy_timeout = 5000;") |> ignore
        conn

/// Runs embedded Migrations/*.sql scripts (ordered by name) that have not run yet.
/// A script is recorded in the SchemaVersions table only after it succeeds.
let migrate (factory: DbConnectionFactory) (logger: ILogger) =
    Directory.CreateDirectory(Path.GetDirectoryName factory.Path) |> ignore
    use conn = factory.Open()
    conn.Execute("PRAGMA journal_mode = WAL;") |> ignore
    conn.Execute("CREATE TABLE IF NOT EXISTS SchemaVersions (Name TEXT PRIMARY KEY, AppliedAt INTEGER NOT NULL);") |> ignore
    let applied = conn.Query<string>("SELECT Name FROM SchemaVersions") |> Set.ofSeq
    let assembly = Assembly.GetExecutingAssembly()
    let scripts =
        assembly.GetManifestResourceNames()
        |> Array.filter (fun n -> n.EndsWith ".sql")
        |> Array.sort
    for name in scripts do
        if not (applied.Contains name) then
            use stream = assembly.GetManifestResourceStream name
            use reader = new StreamReader(stream)
            let sql = reader.ReadToEnd()
            use tx = conn.BeginTransaction()
            conn.Execute(sql, transaction = tx) |> ignore
            conn.Execute("INSERT INTO SchemaVersions (Name, AppliedAt) VALUES (@Name, @AppliedAt)",
                         {| Name = name; AppliedAt = now () |}, tx) |> ignore
            tx.Commit()
            logger.LogInformation("Applied migration {Name}", name)
