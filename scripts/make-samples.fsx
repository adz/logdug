// Generates deterministic sample logs under ./samples (or the folder given as the first argument).
// Run: dotnet fsi scripts/make-samples.fsx [target]
#r "System.Formats.Tar"
open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Text

let target =
    match fsi.CommandLineArgs |> Array.tryItem 1 with
    | Some path -> Path.GetFullPath path
    | None -> Path.Combine(__SOURCE_DIRECTORY__, "..", "samples") |> Path.GetFullPath

let random = Random 4711
let pick (items: 'a array) = items[random.Next items.Length]
let start = DateTimeOffset(2026, 10, 6, 21, 58, 12, TimeSpan.Zero)

let users = [| "ada"; "grace"; "linus"; "barbara"; "edsger"; "margaret"; "alan" |]
let routes = [| "/api/orders"; "/api/orders/{id}"; "/api/payments"; "/api/customers/{id}"; "/health"; "/api/search" |]
let services = [| "OrderService"; "PaymentGateway"; "InventorySync"; "Scheduler"; "AuthProvider" |]

let stackTrace (exceptionType: string) (message: string) =
    [ $"{exceptionType}: {message}"
      "   at Contoso.Orders.PaymentGateway.ChargeAsync(Order order, CancellationToken token) in C:\\src\\Orders\\PaymentGateway.cs:line 118"
      "   at Contoso.Orders.OrderService.CompleteAsync(Guid orderId) in C:\\src\\Orders\\OrderService.cs:line 64"
      "   at Contoso.Web.Endpoints.OrdersEndpoint.Post(HttpContext context) in C:\\src\\Web\\OrdersEndpoint.cs:line 37"
      "   at Microsoft.AspNetCore.Routing.EndpointMiddleware.Invoke(HttpContext httpContext)"
      "--- End of stack trace from previous location ---"
      "   at Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddlewareImpl.Invoke(HttpContext context)" ]

let textLog (lines: int) (offset: TimeSpan) =
    let builder = StringBuilder()
    let mutable time = start + offset

    for i in 1 .. lines do
        time <- time.AddMilliseconds(float (random.Next(5, 2400)))
        let stamp = time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")
        let roll = random.Next 100
        let user = pick users
        let orderId = Guid(random.Next(), 1s, 2s, 3uy, 4uy, 5uy, 6uy, 7uy, 8uy, 9uy, byte (i % 255))

        if roll < 4 then
            builder.AppendLine($"{stamp} [ERR] Payment failed for order {orderId} user=\"{user}\" amount={random.Next(10, 900)}.{random.Next(10, 99)} attempt=3") |> ignore
            for line in stackTrace "System.Net.Http.HttpRequestException" "Response status code does not indicate success: 502 (Bad Gateway)." do
                builder.AppendLine line |> ignore
        elif roll < 5 then
            builder.AppendLine($"{stamp} [FTL] Unhandled exception in {pick services}; the host is shutting down") |> ignore
            for line in stackTrace "System.InvalidOperationException" "Sequence contains no elements" do
                builder.AppendLine line |> ignore
        elif roll < 14 then
            builder.AppendLine($"{stamp} [WRN] Slow request {pick routes} took {random.Next(1200, 9000)}ms (threshold 1000ms) user=\"{user}\"") |> ignore
        elif roll < 34 then
            builder.AppendLine($"{stamp} [DBG] Cache lookup key=\"order:{orderId}\" hit={random.Next 2 = 1} elapsed={random.Next(1, 40)}ms") |> ignore
        elif roll < 40 then
            builder.AppendLine($"{stamp} [VRB] Dispatching {pick services}.Tick sequence={i}") |> ignore
        elif roll < 43 then
            builder.AppendLine(
                $"{stamp} [INF] Reconciled inventory batch: warehouse=\"MEL-02\" skus={random.Next(100, 4000)} adjusted={random.Next(0, 80)} "
                + "notes=\"Batch includes back-ordered items carried over from the previous cycle; the reconciliation rules treat partially "
                + "shipped lines as open until the courier confirms delivery, which can take several hours for regional addresses.\""
            )
            |> ignore
        else
            let verb = pick [| "GET"; "POST"; "PUT" |]
            let status = pick [| 200; 200; 200; 201; 204; 404 |]

            builder.AppendLine(
                $"{stamp} [INF] HTTP {verb} {pick routes} responded {status} in {random.Next(3, 600)}ms user=\"{user}\" trace={orderId}"
            )
            |> ignore

    builder.ToString()

let jsonEscape (text: string) = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "")

let serilogLog (lines: int) =
    let builder = StringBuilder()
    let mutable time = start.AddMinutes 3.0

    for i in 1 .. lines do
        time <- time.AddMilliseconds(float (random.Next(20, 3000)))
        let stamp = time.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
        let roll = random.Next 100
        let elapsed = random.Next(2, 900)
        let route = pick routes

        let verb = pick [| "GET"; "POST" |]
        let status = pick [| 200; 200; 201; 404; 500 |]
        let cached = (random.Next 2 = 1).ToString().ToLowerInvariant()

        let line =
            if roll < 5 then
                let exn = String.Join("\n", stackTrace "System.TimeoutException" "The operation has timed out after 30000ms.")
                $"{{\"@t\":\"{stamp}\",\"@mt\":\"Request {{RequestPath}} failed after {{Elapsed}} ms\",\"@l\":\"Error\",\"@x\":\"{jsonEscape exn}\",\"RequestPath\":\"{route}\",\"Elapsed\":{elapsed},\"RequestId\":\"0HN{i:X5}\",\"SourceContext\":\"Contoso.Web.RequestLogging\"}}"
            elif roll < 15 then
                $"{{\"@t\":\"{stamp}\",\"@mt\":\"Retrying {{Operation}} in {{Delay}}\",\"@l\":\"Warning\",\"Operation\":\"{pick services}.Flush\",\"Delay\":\"00:00:0{random.Next(1, 9)}\",\"Attempt\":{random.Next(1, 5)}}}"
            elif roll < 30 then
                $"{{\"@t\":\"{stamp}\",\"@mt\":\"Loaded {{Count}} rows from {{Table}}\",\"@l\":\"Debug\",\"Count\":{random.Next(0, 5000)},\"Table\":\"orders_{random.Next(1, 9)}\",\"Cached\":{cached}}}"
            else
                $"{{\"@t\":\"{stamp}\",\"@mt\":\"HTTP {{RequestMethod}} {{RequestPath}} responded {{StatusCode}} in {{Elapsed:0.0000}} ms\",\"RequestMethod\":\"{verb}\",\"RequestPath\":\"{route}\",\"StatusCode\":{status},\"Elapsed\":{elapsed}.{random.Next(1000, 9999)},\"User\":{{\"Name\":\"{pick users}\",\"Roles\":[\"admin\",\"ops\"]}}}}"

        builder.AppendLine line |> ignore

    builder.ToString()

let pinoLog (lines: int) =
    let builder = StringBuilder()
    let mutable time = start.AddMinutes 7.0

    for i in 1 .. lines do
        time <- time.AddMilliseconds(float (random.Next(10, 4000)))
        let level = pick [| 20; 30; 30; 30; 30; 40; 50; 60 |]

        let msg =
            match level with
            | 60 -> "worker crashed: heap out of memory"
            | 50 -> $"job {i} failed: ECONNRESET talking to redis://cache.internal:6379"
            | 40 -> $"queue depth {random.Next(500, 5000)} above soft limit"
            | 20 -> $"polled queue in {random.Next(1, 40)}ms"
            | _ -> $"processed job {i} for tenant {pick users}"

        builder.AppendLine(
            $"{{\"level\":{level},\"time\":{time.ToUnixTimeMilliseconds()},\"pid\":4242,\"hostname\":\"worker-7f9c\",\"queue\":\"emails\",\"jobId\":{i},\"msg\":\"{msg}\"}}"
        )
        |> ignore

    builder.ToString()

let syslog (lines: int) =
    let builder = StringBuilder()
    let mutable time = start.AddMinutes 1.0
    let processes = [| "sshd[1123]"; "kernel"; "systemd[1]"; "cron[884]"; "nginx[2210]" |]

    for _ in 1 .. lines do
        time <- time.AddSeconds(float (random.Next(1, 90)))
        let message =
            match random.Next 10 with
            | 0 -> "error: maximum authentication attempts exceeded for invalid user admin from 203.0.113.9 port 51022 ssh2"
            | 1 -> "warning: Out of memory: Killed process 3112 (java) total-vm:8123452kB"
            | 2 -> "Started Daily apt download activities."
            | _ -> $"Accepted publickey for deploy from 198.51.100.{random.Next(2, 250)} port {random.Next(40000, 60000)} ssh2"

        let stamp = time.ToString("MMM dd HH:mm:ss", Globalization.CultureInfo.InvariantCulture)
        builder.AppendLine($"{stamp} web-01 {pick processes}: {message}") |> ignore

    builder.ToString()

let nginxAccess (lines: int) =
    let builder = StringBuilder()
    let mutable time = start

    for _ in 1 .. lines do
        time <- time.AddSeconds(float (random.Next(0, 20)))
        let status = pick [| 200; 200; 200; 304; 404; 502 |]
        let stamp = time.ToString("dd/MMM/yyyy:HH:mm:ss +0000", Globalization.CultureInfo.InvariantCulture)
        builder.AppendLine($"203.0.113.{random.Next(1, 254)} - - [{stamp}] \"GET {pick routes} HTTP/1.1\" {status} {random.Next(200, 9000)} \"-\" \"Mozilla/5.0\"") |> ignore

    builder.ToString()

let utf8 (text: string) = Encoding.UTF8.GetBytes text

let zipOf (entries: (string * byte array) list) =
    use buffer = new MemoryStream()

    do
        use zip = new ZipArchive(buffer, ZipArchiveMode.Create, true)

        for name, bytes in entries do
            let entry = zip.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- start
            use stream = entry.Open()
            stream.Write(bytes, 0, bytes.Length)

    buffer.ToArray()

let tarGzOf (entries: (string * byte array) list) =
    use buffer = new MemoryStream()

    do
        use gzip = new GZipStream(buffer, CompressionLevel.Optimal, true)
        use writer = new TarWriter(gzip, TarEntryFormat.Pax, false)

        for name, bytes in entries do
            let entry = PaxTarEntry(TarEntryType.RegularFile, name)
            entry.DataStream <- new MemoryStream(bytes)
            entry.ModificationTime <- start
            writer.WriteEntry entry

    buffer.ToArray()

let gzipOf (bytes: byte array) =
    use buffer = new MemoryStream()

    do
        use gzip = new GZipStream(buffer, CompressionLevel.Optimal, true)
        gzip.Write(bytes, 0, bytes.Length)

    buffer.ToArray()

let write (relative: string) (bytes: byte array) =
    let path = Path.Combine(target, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllBytes(path, bytes)
    printfn "  %s (%d bytes)" relative bytes.Length

printfn "Writing samples to %s" target

if Directory.Exists target then
    Directory.Delete(target, true)

write "app/orders-api.log" (utf8 (textLog 2400 TimeSpan.Zero))
write "app/orders-api.jsonl" (utf8 (serilogLog 1800))
write "workers/email-worker.ndjson" (utf8 (pinoLog 900))
write "system/syslog" (utf8 (syslog 600))
write "rotated/orders-api.2026-10-05.log.gz" (gzipOf (utf8 (textLog 1500 (TimeSpan.FromDays -1.0))))

let nightly =
    zipOf
        [ "logs/orders-api.log", utf8 (textLog 900 (TimeSpan.FromHours 2.0))
          "logs/events.jsonl", utf8 (serilogLog 700)
          "logs/workers/email-worker.ndjson", utf8 (pinoLog 400)
          "README.txt", utf8 "Nightly log bundle exported by the ops pipeline.\n" ]

write "archives/nightly-2026-10-06.zip" nightly

write
    "archives/incident-4711.tar.gz"
    (tarGzOf
        [ "incident-4711/nginx/access.log", utf8 (nginxAccess 1200)
          "incident-4711/app/orders-api.log", utf8 (textLog 1200 (TimeSpan.FromMinutes 30.0))
          "incident-4711/app/orders-api.jsonl", utf8 (serilogLog 900)
          "incident-4711/host/syslog", utf8 (syslog 400)
          "incident-4711/attachments/previous-nightly.zip", nightly ])
