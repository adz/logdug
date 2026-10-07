namespace LogsDigger

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Text
open Axial
open Axial.FileSystem
open LogsDigger.Files

/// Checks the shipped binary end to end without a window: `LogsDigger --self-test`. Unit tests run on the JIT;
/// this runs the paths that only fail under NativeAOT and trimming (Reified JSON and codecs, regex, time zones,
/// archive decoding, the Axial stream pipeline) inside the published executable.
module SelfTest =
    type Check = { Name: string; Passed: bool; Detail: string }

    let private textLog =
        "2026-10-06 21:58:14.093 +00:00 [ERR] Payment failed for order user=\"ada\"\n"
        + "System.Net.Http.HttpRequestException: 502 (Bad Gateway)\n"
        + "   at Contoso.Orders.PaymentGateway.ChargeAsync() in C:\\src\\PaymentGateway.cs:line 118\n"
        + "2026-10-06 21:58:15.000 +00:00 [INF] Recovered\n"

    let private jsonLog =
        "{\"@t\":\"2026-10-06T22:01:13.2570000Z\",\"@mt\":\"Loaded {Count} rows from {Table}\",\"@l\":\"Debug\",\"Count\":2872,\"Table\":\"orders_8\",\"Cached\":true}\n"
        + "{\"level\":50,\"time\":1791324000000,\"msg\":\"job failed: ECONNRESET\"}\n"

    let private zipOf (entries: (string * string) list) =
        use buffer = new MemoryStream()

        do
            use zip = new ZipArchive(buffer, ZipArchiveMode.Create, true)

            for name, text in entries do
                use stream = zip.CreateEntry(name).Open()
                let bytes = Encoding.UTF8.GetBytes text
                stream.Write(bytes, 0, bytes.Length)

        buffer.ToArray()

    let private tarGzOf (entries: (string * byte array) list) =
        use buffer = new MemoryStream()

        do
            use gzip = new GZipStream(buffer, CompressionLevel.Fastest, true)
            use writer = new TarWriter(gzip, TarEntryFormat.Pax, false)

            for name, bytes in entries do
                let entry = PaxTarEntry(TarEntryType.RegularFile, name)
                entry.DataStream <- new MemoryStream(bytes)
                writer.WriteEntry entry

        buffer.ToArray()

    /// Writes a small tree with plain, JSON-lines and archived logs, including a zip inside a tar.gz.
    let private fixture (root: string) : Flow<'env, FileSystemError, unit> when 'env :> IHasFileSystem =
        flow {
            do! FileSystem.createDirectory (Path.Combine(root, "logs"))
            do! FileSystem.writeAllText (Path.Combine(root, "logs", "app.log")) textLog
            do! FileSystem.writeAllText (Path.Combine(root, "logs", "api.jsonl")) jsonLog

            let inner = zipOf [ "nested/worker.ndjson", jsonLog ]
            let outer = tarGzOf [ "bundle/app.log", Encoding.UTF8.GetBytes textLog; "bundle/inner.zip", inner ]
            do! FileSystem.writeAllBytes (Path.Combine(root, "bundle.tar.gz")) outer
        }

    let private check name (passed: bool) detail = { Name = name; Passed = passed; Detail = detail }

    let private childNamed name (nodes: Node list) = nodes |> List.tryFind (fun node -> node.Name = name)

    let private checks (root: string) : Flow<AppEnv, FilesError, Check list> =
        flow {
            let rootNode = Node.ofName (Disk root) (Location.fileName root) 0L true
            let! walked = Files.walk (fun _ -> true) rootNode |> FlowStream.runCollect
            let keys = walked |> List.map (fun node -> Location.key node.Location)
            let nestedKey = keys |> List.tryFind (fun key -> key.EndsWith "inner.zip!/nested/worker.ndjson")

            let nested = walked |> List.tryFind (fun node -> Some(Location.key node.Location) = nestedKey)

            let! nestedLines =
                match nested with
                | Some node -> Files.lines node |> FlowStream.runCollect
                | None -> Flow.succeed []

            let document = LogParser.parse { ReferenceYear = 2026 } (String.Join("\n", nestedLines))
            let serilog = document.Entries |> Array.tryHead
            let pino = document.Entries |> Array.tryItem 1

            let textDocument = LogParser.parse { ReferenceYear = 2026 } textLog
            let pretty = serilog |> Option.bind LogParser.prettyJson |> Option.defaultValue ""

            let settings = { Settings.defaults with TimeMode = "zone"; Zone = "Australia/Adelaide"; Regex = true }
            let roundTrip = Settings.parse (Settings.serialize settings)

            let adelaide = Time.context TimeZoneInfo.Utc (DateTimeOffset(2026, 10, 6, 21, 30, 0, TimeSpan.Zero)) (Zone "Australia/Adelaide")
            let converted = Time.format adelaide (DateTimeOffset(2026, 10, 6, 21, 30, 0, TimeSpan.Zero))

            let segments =
                Render.body textDocument.Entries[0]
                |> Render.highlight (SearchPattern.create { Text = "50[0-9]"; Mode = RegexSearch; MatchCase = false } |> Result.toOption)

            let! steps =
                match SearchPattern.create { Text = "ECONNRESET"; Mode = PlainSearch; MatchCase = true } with
                | Ok pattern -> Search.run pattern rootNode |> FlowStream.runCollect
                | Error _ -> Flow.succeed []

            let hitFiles = steps |> List.choose _.Found |> List.length

            return
                [ check "walk enters a zip inside a tar.gz" nestedKey.IsSome (string walked.Length + " nodes")
                  check "lines stream from the nested archive" (nestedLines.Length = 2) (string nestedLines.Length + " lines")
                  check "Serilog template renders through Reified Data" (serilog |> Option.exists (fun e -> e.Message = "Loaded 2872 rows from orders_8")) (serilog |> Option.map _.Message |> Option.defaultValue "missing")
                  check "pino numeric level parses" (pino |> Option.exists (fun e -> e.Level = Level.Error)) (pino |> Option.map (fun e -> Level.name e.Level) |> Option.defaultValue "missing")
                  check "JSON reindents" (pretty.Contains "\n") (string pretty.Length + " chars")
                  check "stack trace joins its entry" (textDocument.Entries.Length = 2 && textDocument.Entries[0].Continuation.Length = 2) (string textDocument.Entries.Length + " entries")
                  check "settings round-trip through the Reified codec" (roundTrip = Ok settings) (match roundTrip with Ok _ -> "ok" | Error message -> message)
                  check "IANA time zone converts" (converted = "2026-10-07 08:00:00.000") converted
                  check "regex highlights a match" (segments |> List.exists _.Hit) (segments |> List.filter _.Hit |> List.map _.Text |> String.concat ",")
                  check "search streams across disk and archives" (hitFiles = 2) (string hitFiles + " files with hits") ]
        }

    /// Runs every check in a scratch folder and returns them; never throws for a failed check.
    let run (env: AppEnv) : Flow<AppEnv, string, Check list> =
        flow {
            let root = Path.Combine(env.FileSystem.GetTempPath(), "logs-digger-self-test-" + env.FileSystem.GetRandomFileName())
            do! fixture root |> Flow.mapError FileSystemError.describe

            let! results =
                checks root
                |> Flow.mapError string
                |> Flow.fold Flow.succeed (fun cause -> Flow.succeed [ check "self-test ran" false (Cause.prettyPrint id cause) ])

            do! FileSystem.deleteDirectory root true |> Flow.fold Flow.succeed (fun _ -> Flow.succeed ())
            return results
        }

    let render (results: Check list) =
        let lines =
            results
            |> List.map (fun result -> (if result.Passed then "PASS  " else "FAIL  ") + result.Name + "  (" + result.Detail + ")")

        let passed = results |> List.filter _.Passed |> List.length
        String.Join(Environment.NewLine, lines @ [ $"{passed}/{results.Length} checks passed" ])
