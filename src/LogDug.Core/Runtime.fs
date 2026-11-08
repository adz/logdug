namespace LogDug

open System
open Axial
open Axial.ElmishFlows
open LogDug.Files


/// The running Axial application behind the UI. Its Elmish runtime owns the long-lived services: the `Files`
/// service (and its archive cache), the search command inbox, the search event hub, and the search pipeline.
/// Disposing it stops the pipeline, interrupts any running search, and closes the runtime.
[<Sealed>]
type Runtime internal (env: AppEnv, runtime: ElmishRuntime<AppEnv>) =
    member _.Env = env
    member _.Elmish = runtime

    interface IDisposable with
        member _.Dispose() =
            // Disposal usually happens on the UI thread; StopAndWait is safe there and honours its timeout.
            runtime.StopAndWait(TimeSpan.FromSeconds 5.0)
            |> ignore

module Runtime =
    let debounce = TimeSpan.FromMilliseconds 200.0
    let private batchWindow = TimeSpan.FromMilliseconds 120.0

    /// One search, published to the hub in batches so a large tree does not flood the UI with one message per file.
    let private runSearch (request: SearchRequest) : Flow<AppEnv, Never, unit> =
        flow {
            let! events = Flow.envWith _.SearchEvents

            let publish event =
                Hub.publish event events |> Flow.ignore

            let! outcome =
                Search.run request.Pattern request.Root
                |> FlowStream.groupedWithin 64 batchWindow
                |> FlowStream.mapFlow (fun steps ->
                    let summary = (List.last steps).Summary
                    let found = steps |> List.choose _.Found
                    publish (SearchProgress(request.Id, found, summary)) |> Flow.map (fun () -> summary))
                |> FlowStream.runFold (fun _ summary -> summary) Search.emptySummary
                |> Flow.foldCause (Ok >> Flow.succeed) (fun cause -> Flow.succeed (Error(Cause.prettyPrint string cause)))

            match outcome with
            | Ok summary -> do! publish (SearchEnded(request.Id, summary))
            | Error message -> do! publish (SearchFailed(request.Id, message))
        }

    /// Search-as-you-type: wait for commands to settle, then run only the newest. A newer command interrupts
    /// the running search, which closes its open files and stops its walk before the next one starts.
    let searchPipeline: Flow<AppEnv, Never, unit> =
        flow {
            let! commands = Flow.envWith _.SearchCommands

            return!
                Inbox.stream commands
                |> FlowStream.debounce debounce
                |> FlowStream.switchMapFlow (function
                    | StartSearch request -> runSearch request
                    | StopSearch -> Flow.succeed ())
                |> FlowStream.runDrain
        }

    /// Starts the application: provisions the services, starts the Elmish runtime over them, and runs the
    /// search pipeline as a long-lived service fiber inside the runtime's scope.
    let start (platform: PlatformEnv) (localZone: TimeZoneInfo) (post: (unit -> unit) -> unit) (onDefect: CommandDefect -> unit) : Runtime =
        let layer : Layer<PlatformEnv, Never, AppEnv> =
            Layer.fromFlow (flow {
                // Read the platform's clock inside the runtime's scope, not before it exists: the start time is one
                // reading of the clock the environment actually runs with.
                let! startedAt = Clock.now
                // A newest-wins mailbox: an offer made while a command is pending evicts that pending command, so a
                // burst of keystrokes leaves one search to run. The inbox is shut down with the runtime's scope.
                let! commands = Inbox.scopedSliding 1
                let! events = Hub.make ()
                let! files = Files.make platform.FileSystem
                return
                    { FileSystem = platform.FileSystem
                      Config = platform.Config
                      Files = files
                      SearchCommands = commands
                      SearchEvents = events
                      LocalZone = localZone
                      StartedAt = startedAt }
            })

        let runtime =
            ElmishRuntime.startLayer
                { Post = post
                  OnDefect = onDefect
                  HistoryCapacity = 128
                  Clock = None
                  LogSink = Some(LogSink.console LogLevel.Warning) }
                layer platform
            // axial-allow-AXG001: host startup blocks until the Elmish runtime layer is provisioned
            |> Async.RunSynchronously
            |> function
                | Ok runtime -> runtime
                | Error (never: Never) -> Never.absurd never

        let env = runtime.Environment

        // The search pipeline never completes; it runs as a service fiber owned by the runtime's scope, which stops it
        // when the runtime stops and reports it if it ever ends early.
        match ElmishRuntime.startService "search-pipeline" searchPipeline runtime with
        | Ok () -> ()
        | Error ServiceStartError.DuplicateName ->
            // Both cases are programming errors at startup, not conditions the user can reach.
            failwith "The search pipeline did not start: a service with that name is already running."
        | Error ServiceStartError.RuntimeStopping -> failwith "The search pipeline did not start: the runtime is stopping."

        new Runtime(env, runtime)

    /// The runtime over the live platform services and the host-supplied time zone.
    let live (localZone: TimeZoneInfo) (post: (unit -> unit) -> unit) (onDefect: CommandDefect -> unit) =
        start (PlatformEnv.live ()) localZone post onDefect
