namespace LogsDigger

open System.IO
open Axial
open Axial.FileSystem
open Axial.PlatformService
open Reified
open Reified.ConstraintDSL
open Reified.SchemaDSL

/// What the app remembers between runs. The record is the wire shape; `Settings.schema` is its one declaration,
/// used both to validate the file on load and to write it back.
type Settings =
    { TimeMode: string
      Zone: string
      Regex: bool
      MatchCase: bool
      DarkTheme: bool }

module Settings =
    let defaults =
        { TimeMode = "local"
          Zone = "Europe/London"
          Regex = false
          MatchCase = false
          DarkTheme = true }

    let schema =
        schema<Settings> {
            field _.TimeMode { constrain (oneOf [ "utc"; "local"; "zone" ]) }
            field _.Zone { constraints [ present; maxLength 64 ] }
            field _.Regex
            field _.MatchCase
            field _.DarkTheme
            construct (fun timeMode zone regex matchCase darkTheme ->
                { TimeMode = timeMode
                  Zone = zone
                  Regex = regex
                  MatchCase = matchCase
                  DarkTheme = darkTheme })
        }

    let private codec = Json.compile schema

    let timeDisplay settings =
        match settings.TimeMode with
        | "utc" -> Utc
        | "zone" -> Zone settings.Zone
        | _ -> Local

    let withTimeDisplay display settings =
        match display with
        | Utc -> { settings with TimeMode = "utc" }
        | Local -> { settings with TimeMode = "local" }
        | Zone id -> { settings with TimeMode = "zone"; Zone = id }

    /// Parses settings text. A file that fails the schema falls back to defaults rather than blocking startup.
    let parse (text: string) : Result<Settings, string> =
        try
            match Schema.parse schema (Json.parseData text) with
            | Ok settings -> Ok settings
            | Error errors ->
                errors
                |> SchemaErrors.toList
                |> List.map (fun issue -> $"{SchemaPath.format issue.Path}: {SchemaError.render issue.Error}")
                |> String.concat "; "
                |> Error
        with error ->
            Error error.Message

    let serialize (settings: Settings) = Json.serializeIndented codec settings

    let private directory<'env when 'env :> IHasEnvironmentVariables> : Flow<'env, Never, string> =
        flow {
            let! appData = EnvironmentVariables.tryGet "APPDATA"
            let! xdg = EnvironmentVariables.tryGet "XDG_CONFIG_HOME"
            let! home = EnvironmentVariables.tryGet "HOME"

            let baseDirectory =
                appData
                |> Option.orElse xdg
                |> Option.orElse (home |> Option.map (fun home -> Path.Combine(home, ".config")))
                |> Option.defaultValue "."

            return Path.Combine(baseDirectory, "LogsDigger")
        }

    let load<'env when 'env :> IHasEnvironmentVariables and 'env :> IHasFileSystem> : Flow<'env, Never, Settings> =
        flow {
            let! folder = directory
            let path = Path.Combine(folder, "settings.json")

            let! text =
                FileSystem.readAllText path
                |> Flow.fold (Some >> Flow.succeed) (fun _ -> Flow.succeed None)

            return
                text
                |> Option.bind (parse >> Result.toOption)
                |> Option.defaultValue defaults
        }

    let save (settings: Settings) : Flow<'env, FileSystemError, unit> when 'env :> IHasEnvironmentVariables and 'env :> IHasFileSystem =
        flow {
            let! folder = directory |> Flow.widenError
            do! FileSystem.createDirectory folder
            do! FileSystem.writeAllText (Path.Combine(folder, "settings.json")) (serialize settings)
        }
