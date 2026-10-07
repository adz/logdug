// Service records for the app. `PlatformEnv.live` is the one place that names the live platform services.
namespace LogDug

open System
open Axial
open Axial.FileSystem
open Axial.PlatformService
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

/// The platform services the runtime starts from.
type PlatformEnv =
    { FileSystem: IFileSystem
      Clock: IClock
      EnvironmentVariables: IEnvironmentVariables }

    interface IHasFileSystem with
        member this.FileSystem = this.FileSystem

    interface IHasClock with
        member this.Clock = this.Clock

    interface IHasEnvironmentVariables with
        member this.EnvironmentVariables = this.EnvironmentVariables

module PlatformEnv =
    let live () =
        { FileSystem = FileSystem.live
          Clock = Clock.live
          EnvironmentVariables = EnvironmentVariables.live }

/// Everything the Elmish program's effects run against. The runtime services (`Files`, the search queue and
/// hub) live in the Axial application root started by `Runtime.start`; this record only refers to them.
type AppEnv =
    { FileSystem: IFileSystem
      Clock: IClock
      EnvironmentVariables: IEnvironmentVariables
      Files: IFiles
      SearchCommands: Queue<SearchCommand>
      SearchEvents: Hub<SearchEvent>
      LocalZone: TimeZoneInfo
      /// Delivers a callback to the thread that owns the Elmish loop (the UI thread in the app).
      Post: (unit -> unit) -> unit }

    interface IHasFileSystem with
        member this.FileSystem = this.FileSystem

    interface IHasClock with
        member this.Clock = this.Clock

    interface IHasEnvironmentVariables with
        member this.EnvironmentVariables = this.EnvironmentVariables

    interface IHasFiles with
        member this.Files = this.Files
