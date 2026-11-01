// Service records for the app. `PlatformEnv.live` is the one place that names the live platform services.
namespace LogDug

open System
open Axial
open Axial.Configuration
open Axial.FileSystem
open LogDug.Files

type SearchRequest =
    { Id: int
      Pattern: SearchPattern
      Root: Node }

/// What the UI asks of the search pipeline. Only the newest command matters.
type SearchCommand =
    | StartSearch of SearchRequest
    | StopSearch

/// What the search pipeline reports, tagged with the request it belongs to.
type SearchEvent =
    | SearchProgress of id: int * found: FileHits list * summary: SearchSummary
    | SearchEnded of id: int * summary: SearchSummary
    | SearchFailed of id: int * message: string

/// The platform authority the runtime starts from. A merged configuration snapshot is loaded once here so the
/// application reads settings from a plain value rather than from ambient environment variables.
type PlatformEnv =
    { FileSystem: IFileSystem
      Config: IConfig }

    interface IHasFileSystem with
        member this.FileSystem = this.FileSystem

    interface IHasConfig with
        member this.Config = this.Config

module PlatformEnv =
    let private noSources =
        { new IConfig with
            member _.Data = Reified.Data.Object []
            member _.Sources = [] }

    /// The host boundary: read the process environment once through the configuration source.
    let live () =
        let config =
            match Flow.run () (Config.loadData [ ConfigSource.environment None ]) with
            | Exit.Success config -> config
            | Exit.Failure cause ->
                Cause.prettyPrint
                    (List.map (ConfigError.describe noSources) >> String.concat "; ")
                    cause
                |> failwith

        { FileSystem = FileSystem.live
          Config = config }

/// Everything the Elmish program's effects run against. The runtime services (`Files`, the search queue and
/// hub) are owned by the Elmish runtime started by `Runtime.start`; this record only refers to them.
type AppEnv =
    { FileSystem: IFileSystem
      Config: IConfig
      Files: IFiles
      SearchCommands: Queue<SearchCommand>
      SearchEvents: Hub<SearchEvent>
      LocalZone: TimeZoneInfo
      StartedAt: DateTimeOffset }

    interface IHasFileSystem with
        member this.FileSystem = this.FileSystem

    interface IHasConfig with
        member this.Config = this.Config

    interface IHasFiles with
        member this.Files = this.Files
