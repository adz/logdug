module LogsDigger.Tests.Support

open System
open System.IO
open Axial
open LogsDigger
open LogsDigger.Files

let repoRoot () =
    let rec find (directory: DirectoryInfo) =
        if isNull directory then failwith "LogsDigger.slnx not found above the test output folder"
        elif File.Exists(Path.Combine(directory.FullName, "LogsDigger.slnx")) then directory.FullName
        else find directory.Parent

    find (DirectoryInfo AppContext.BaseDirectory)

let samples () = Path.Combine(repoRoot (), "samples")

let options () : LogParser.Options = { ReferenceYear = 2026 }

/// A fresh runtime (Axial application root with the Files service and search pipeline) for one test.
let runtime () =
    Runtime.start (PlatformEnv.live ()) TimeZoneInfo.Utc (fun callback -> callback ())

let runIn (runtime: Runtime) (flow: Flow<AppEnv, 'error, 'value>) : 'value =
    match Flow.run runtime.Env flow with
    | Exit.Success value -> value
    | Exit.Failure cause -> failwith (Cause.prettyPrint (fun error -> error.ToString()) cause)

let run (flow: Flow<AppEnv, 'error, 'value>) : 'value =
    use runtime = runtime ()
    runIn runtime flow

let collect (stream: FlowStream<AppEnv, 'error, 'value>) : 'value list = run (FlowStream.runCollect stream)

let diskNode (path: string) =
    let isDirectory = Directory.Exists path
    Node.ofName (Disk path) (Path.GetFileName path) (if isDirectory then 0L else FileInfo(path).Length) isDirectory

let parse (text: string) = LogParser.parse (options ()) text

let searchPattern text mode matchCase =
    match SearchPattern.create { Text = text; Mode = mode; MatchCase = matchCase } with
    | Ok pattern -> pattern
    | Error message -> failwith message
