module LogDug.Tests.ParserTests

open System
open Xunit
open LogDug
open LogDug.Tests.Support

[<Fact>]
let ``stack trace lines join the entry above them`` () =
    let document =
        parse (
            String.Join(
                "\n",
                [ "2026-10-06 21:58:14.093 +00:00 [ERR] Payment failed"
                  "System.Net.Http.HttpRequestException: 502"
                  "   at Contoso.Orders.PaymentGateway.ChargeAsync() in C:\\src\\PaymentGateway.cs:line 118"
                  "2026-10-06 21:58:15.000 +00:00 [INF] Next" ]
            )
        )

    Assert.Equal(2, document.Entries.Length)
    let first = document.Entries[0]
    Assert.Equal(Level.Error, first.Level)
    Assert.Equal("Payment failed", first.Message)
    Assert.Equal(2, first.Continuation.Length)
    Assert.Equal(4, document.Entries[1].Line)
    Assert.Equal(Some(DateTimeOffset(2026, 10, 6, 21, 58, 14, 93, TimeSpan.Zero)), first.Timestamp)

[<Fact>]
let ``timestamps with offsets are normalised to the same instant`` () =
    let document = parse "2026-10-07T08:00:00+10:30 WARN Late\n2026-10-06 21:30:00Z INFO Early"
    let late = document.Entries[0].Timestamp.Value
    let early = document.Entries[1].Timestamp.Value
    Assert.Equal(DateTime(2026, 10, 6, 21, 30, 0), late.UtcDateTime)
    Assert.Equal(late.UtcDateTime, early.UtcDateTime)
    Assert.Equal(Level.Warn, document.Entries[0].Level)

[<Fact>]
let ``serilog compact json renders its message template and keeps unused properties`` () =
    let line =
        """{"@t":"2026-10-06T22:01:13.2570000Z","@mt":"Loaded {Count} rows from {Table}","@l":"Debug","Count":2872,"Table":"orders_8","Cached":true}"""

    let document = parse line
    let entry = document.Entries[0]
    Assert.Equal(JsonEntry, entry.Format)
    Assert.Equal("Loaded 2872 rows from orders_8", entry.Message)
    Assert.Equal(Level.Debug, entry.Level)
    Assert.Equal<string list>([ "Cached" ], entry.Fields |> List.map _.Key)
    Assert.Equal(JsonLines, document.Kind)

[<Fact>]
let ``serilog events without a level are information`` () =
    let entry = (parse """{"@t":"2026-10-06T22:01:13Z","@mt":"Started"}""").Entries[0]
    Assert.Equal(Level.Info, entry.Level)

[<Fact>]
let ``pino numeric levels and epoch milliseconds are understood`` () =
    let entry = (parse """{"level":50,"time":1791324000000,"msg":"job failed"}""").Entries[0]
    Assert.Equal(Level.Error, entry.Level)
    Assert.Equal("job failed", entry.Message)
    Assert.Equal(Some(DateTimeOffset.FromUnixTimeMilliseconds 1791324000000L), entry.Timestamp)

[<Fact>]
let ``exception text in json becomes continuation lines`` () =
    let entry =
        (parse """{"@t":"2026-10-06T22:01:13Z","@mt":"Boom","@l":"Error","@x":"System.Exception: bad\n   at A.B()"}""").Entries[0]

    Assert.Equal<string array>([| "System.Exception: bad"; "   at A.B()" |], entry.Continuation)

[<Fact>]
let ``syslog lines use the reference year`` () =
    let entry = (parse "Oct  6 22:00:01 web-01 sshd[1123]: error: too many attempts").Entries[0]
    Assert.Equal(Some(DateTimeOffset(2026, 10, 6, 22, 0, 1, TimeSpan.Zero)), entry.Timestamp)

[<Fact>]
let ``a file without markers shows every line as its own entry`` () =
    let document = parse "first line\n  indented second\nthird"
    Assert.Equal(3, document.Entries.Length)
    Assert.All(document.Entries, fun entry -> Assert.Equal(Level.NoLevel, entry.Level))

[<Fact>]
let ``level counts cover every entry`` () =
    let document = parse "2026-10-06 21:58:14 [ERR] a\n2026-10-06 21:58:15 [ERR] b\n2026-10-06 21:58:16 [INF] c"
    Assert.Equal(2, document.LevelCounts[Level.Error])
    Assert.Equal(1, document.LevelCounts[Level.Info])

[<Fact>]
let ``nanosecond epochs parse and impossible epochs are ignored`` () =
    let nanos = (parse """{"level":"info","ts":1791324000000000000,"msg":"zap"}""").Entries[0]
    Assert.Equal(Some(DateTimeOffset.FromUnixTimeMilliseconds 1791324000000L), nanos.Timestamp)
    let absurd = (parse """{"level":"info","ts":1e30,"msg":"nope"}""").Entries[0]
    Assert.Equal(None, absurd.Timestamp)

[<Fact>]
let ``words that start like a level are not levels`` () =
    let document = parse "2026-10-06 21:58:14 [ERR] boom\nInfrastructure check complete\nErrors: 0"
    Assert.Equal(1, document.Entries.Length)
    Assert.Equal(2, document.Entries[0].Continuation.Length)

[<Fact>]
let ``an empty file has no entries`` () =
    Assert.Empty((parse "").Entries)

[<Fact>]
let ``csv files become an aligned table with the header first`` () =
    let document = parse "name,qty,note\nwidget,12,\"has, comma\"\ngadget,3,plain"
    Assert.Equal(Delimited, document.Kind)
    Assert.Equal(3, document.Entries.Length)
    Assert.Equal<string list>([ "widget"; "12"; "has, comma" ], document.Entries[1].Fields |> List.map _.Value)

    match document.Entries[1].Format with
    | TableEntry(widths, false) -> Assert.Equal<int array>([| 6; 3; 10 |], widths)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``quoted csv cells can span lines`` () =
    let document = parse "a,b\n1,\"two\nlines\"\n3,4"
    Assert.Equal(3, document.Entries.Length)
    Assert.Equal(2, document.Entries[1].Lines.Length)
    Assert.Equal(4, document.Entries[2].Line)

[<Fact>]
let ``log4j timestamps with a comma are not mistaken for csv`` () =
    let document = parse "2026-10-06 21:58:14,093 INFO a\n2026-10-06 21:58:15,000 WARN b\n2026-10-06 21:58:16,000 INFO c"
    Assert.NotEqual(Delimited, document.Kind)
    Assert.Equal(Level.Warn, document.Entries[1].Level)
