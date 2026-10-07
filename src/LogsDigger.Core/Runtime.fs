namespace LogsDigger

open System
open System.Threading
open System.Threading.Tasks
open Axial
open LogsDigger.Files

/// The running Axial application behind the UI. Its root scope owns the long-lived services: the `Files`
/// service (and its archive cache), the search command queue, the search event hub, and the search pipeline.
/// Disposing it stops the pipeline, interrupts any running search, and closes the scope.
[<Sealed>]
type Runtime internal (env: AppEnv, handle: AppHandle<Never, unit>) =
    member _.Env = env

    interface IDisposable with
        member _.Dispose() =
            // Stop on the thread pool: disposal usually happens on the UI thread, whose synchronization
            // context would otherwise receive Stop's continuation while blocked waiting for it.
            Task.Run<unit>(fun () -> Async.StartAsTask(handle.Stop() |> Async.Ignore)).Wait(TimeSpan.FromSeconds 5.0)
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
                |> Flow.fold (Ok >> Flow.succeed) (fun cause -> Flow.succeed (Error(Cause.prettyPrint string cause)))

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
                commands
                |> FlowStream.fromDequeue
                |> FlowStream.debounce debounce
                |> FlowStream.switchMapFlow (function
                    | StartSearch request -> runSearch request
                    | StopSearch -> Flow.succeed ())
                |> FlowStream.runDrain
        }

    /// Starts the application root and returns once its services exist.
    let start (platform: PlatformEnv) (localZone: TimeZoneInfo) (post: (unit -> unit) -> unit) : Runtime =
        let ready = TaskCompletionSource<AppEnv>(TaskCreationOptions.RunContinuationsAsynchronously)

        let root: Flow<PlatformEnv, Never, unit> =
            flow {
                let! files = Files.make
                let! (commands: Queue<SearchCommand>) = Queue.sliding 1
                let! (events: Hub<SearchEvent>) = Hub.make ()

                let env =
                    { FileSystem = platform.FileSystem
                      Clock = platform.Clock
                      EnvironmentVariables = platform.EnvironmentVariables
                      Files = files
                      SearchCommands = commands
                      SearchEvents = events
                      LocalZone = localZone
                      Post = post }

                do! Flow.delay (fun () -> ready.SetResult env; Flow.succeed ())
                return! searchPipeline |> Flow.localEnv (fun _ -> env)
            }

        let handle = App.start platform root
        new Runtime(ready.Task.Result, handle)

    /// The runtime over the live platform services and the OS time zone.
    let live (post: (unit -> unit) -> unit) =
        start (PlatformEnv.live ()) TimeZoneInfo.Local post

    /// Runs a flow against the runtime's environment and delivers each value of a stream through `post`,
    /// until the returned handle is disposed. This is how Elmish subscriptions follow Axial streams.
    let follow (env: AppEnv) (stream: FlowStream<AppEnv, 'error, 'value>) (deliver: 'value -> unit) : IDisposable =
        let stop = new CancellationTokenSource()
        let work = stream |> FlowStream.runForEach (fun value -> env.Post(fun () -> deliver value))
        work.StartAsTask(env, stop.Token) |> ignore

        { new IDisposable with
            member _.Dispose() =
                stop.Cancel()
                stop.Dispose() }
