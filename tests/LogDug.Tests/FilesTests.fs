module LogDug.Tests.FilesTests

open System
open System.IO
open System.Threading.Tasks
open Axial
open Xunit
open LogDug
open LogDug.Files
open LogDug.Tests.Support

let private child name (nodes: Node list) = nodes |> List.find (fun node -> node.Name = name)

[<Fact>]
let ``a tar.gz opens like a folder, including a zip nested inside it`` () =
    use runtime = runtime ()
    let archive = diskNode (Path.Combine(samples (), "archives", "incident-4711.tar.gz"))
    Assert.Equal(NodeKind.Archive TarGz, archive.Kind)

    let incident = runIn runtime (Files.children archive) |> child "incident-4711"
    Assert.Equal(NodeKind.Folder, incident.Kind)

    let attachments = runIn runtime (Files.children incident) |> child "attachments"
    let nested = runIn runtime (Files.children attachments) |> child "previous-nightly.zip"
    Assert.Equal(NodeKind.Archive Zip, nested.Kind)

    let logs = runIn runtime (Files.children nested) |> child "logs"
    let file = runIn runtime (Files.children logs) |> child "orders-api.log"
    let text = runIn runtime (Files.readText file)
    Assert.True(text.IsSome)
    Assert.Contains("[INF]", text.Value)

[<Fact>]
let ``gzipped files are decompressed when read`` () =
    let file = diskNode (Path.Combine(samples (), "rotated", "orders-api.2026-10-05.log.gz"))
    let text = run (Files.readText file)
    Assert.StartsWith("2026-10-05", text.Value)

[<Fact>]
let ``folders list containers first, alphabetically`` () =
    let nodes = run (Files.children (diskNode (samples ())))
    Assert.All(nodes, fun node -> Assert.True(Node.isContainer node))
    let names = nodes |> List.map _.Name
    Assert.Equal<string list>(List.sortBy (fun (name: string) -> name.ToLowerInvariant()) names, names)

[<Fact>]
let ``binary content reads as nothing`` () =
    let folder = Directory.CreateTempSubdirectory "logdug-binary"
    let path = Path.Combine(folder.FullName, "blob.log")
    File.WriteAllBytes(path, [| 0x4Duy; 0x5Auy; 0uy; 1uy |])
    Assert.Equal(None, run (Files.readText (diskNode path)))
    Assert.Empty(collect (Files.lines (diskNode path)))
    Assert.False(Files.isBinary ("plain text"B) 10)

[<Fact>]
let ``lines stream lazily and stopping early closes the file`` () =
    let path = Path.Combine(samples (), "app", "orders-api.log")
    let first = collect (Files.lines (diskNode path) |> FlowStream.take 2)
    Assert.Equal(2, first.Length)
    Assert.StartsWith("2026-10-06", first[0])

    // The file was released: it can be opened exclusively straight away.
    use exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)
    Assert.True(exclusive.CanRead)

[<Fact>]
let ``lines match the file exactly`` () =
    let path = Path.Combine(samples (), "system", "syslog")
    Assert.Equal<string list>(List.ofArray (File.ReadAllLines path), collect (Files.lines (diskNode path)))

[<Fact>]
let ``the walk is depth first, enters archives, and is lazy`` () =
    let walked = collect (Files.walk (fun _ -> true) (diskNode (samples ())) |> FlowStream.map (fun node -> Location.key node.Location))
    let index (suffix: string) = walked |> List.findIndex (fun key -> key.EndsWith suffix)
    Assert.True(index "archives" < index "incident-4711.tar.gz")
    Assert.True(index "incident-4711.tar.gz" < index "incident-4711.tar.gz!/incident-4711")
    Assert.Contains(walked, fun key -> key.EndsWith "previous-nightly.zip!/logs/workers/email-worker.ndjson")

    let firstThree = collect (Files.walk (fun _ -> true) (diskNode (samples ())) |> FlowStream.take 3)
    Assert.Equal(3, firstThree.Length)

[<Fact>]
let ``watching a file reports a change`` () =
    use runtime = runtime ()
    let folder = Directory.CreateTempSubdirectory "logdug-watch"
    let path = Path.Combine(folder.FullName, "live.log")
    File.WriteAllText(path, "first\n")

    let firstChange = (Files.watch (diskNode path) |> FlowStream.runTryHead).StartAsTask(runtime.Env)

    let rec writeUntilSeen attempt =
        File.AppendAllText(path, $"line {attempt}\n")
        if not (firstChange.Wait 250) && attempt < 40 then writeUntilSeen (attempt + 1)

    writeUntilSeen 0

    match firstChange.Result with
    | Exit.Success(Some change) -> Assert.Equal(path, change.Path)
    | other -> failwith $"no change observed: {other}"

[<Fact>]
let ``search finds matches on disk and inside nested archives`` () =
    let pattern = searchPattern "ECONNRESET" PlainSearch true
    let steps = collect (Search.run pattern (diskNode (samples ())))
    let found = steps |> List.choose _.Found
    let summary = (List.last steps).Summary

    let keys = found |> List.map (fun hits -> Location.key hits.Node.Location)
    Assert.Contains(keys, fun key -> key.EndsWith "previous-nightly.zip!/logs/workers/email-worker.ndjson")
    Assert.Contains(keys, fun key -> key.EndsWith "email-worker.ndjson" && not (key.Contains "!/"))
    Assert.Equal(summary.HitCount, found |> List.sumBy _.Hits.Length)
    Assert.Equal(summary.FilesScanned, steps.Length)
    Assert.All(found |> List.collect _.Hits, fun hit -> Assert.Contains("ECONNRESET", hit.Preview))

[<Fact>]
let ``the pipeline runs only the newest of several quick requests`` () =
    use runtime = runtime ()
    let env = runtime.Env
    let root = diskNode (samples ())

    let subscribed =
        flow {
            let! events = Hub.subscribe QueueStrategy.Unbounded env.SearchEvents
            let request id text = StartSearch { Id = id; Pattern = searchPattern text PlainSearch false; Root = root }
            Queue.tryOffer (request 1 "ECONNRESET") env.SearchCommands |> ignore
            Queue.tryOffer (request 2 "heap out of memory") env.SearchCommands |> ignore

            return!
                FlowStream.fromDequeue events
                |> FlowStream.takeWhile (function
                    | SearchEnded _
                    | SearchFailed _ -> false
                    | SearchProgress _ -> true)
                |> FlowStream.runCollect
        }
        |> Flow.scoped

    let progress = runIn runtime subscribed
    Assert.NotEmpty progress

    Assert.All(progress, fun event ->
        match event with
        | SearchProgress(id, _, _) -> Assert.Equal(2, id)
        | _ -> ())

[<Fact>]
let ``archive entries with leading or doubled slashes can be listed and read`` () =
    use buffer = new MemoryStream()

    do
        use zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, true)
        use stream = zip.CreateEntry("/var//log/app.log").Open()
        stream.Write("hello"B, 0, 5)

    let index = Archive.ofZip (buffer.ToArray())
    let var = index.Children "" |> List.exactlyOne
    let log = index.Children var.Path |> List.exactlyOne
    let file = index.Children log.Path |> List.exactlyOne
    Assert.Equal("var/log/app.log", file.Path)
    Assert.Equal<byte array>("hello"B, (index.TryRead file.Path).Value)
