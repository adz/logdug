module LogDug.Tests.RenderTests

open System
open Xunit
open LogDug
open LogDug.Tests.Support

[<Fact>]
let ``highlight marks exactly the matched characters across segment boundaries`` () =
    let segments = [ Segment.make Plain "status="; Segment.make NumberLiteral "502"; Segment.make Plain " Bad" ]
    let marked = Render.highlight (Some(searchPattern "=502 b" PlainSearch false)) segments
    let hits = marked |> List.filter _.Hit |> List.map _.Text |> String.concat ""
    Assert.Equal("=502 B", hits)
    Assert.Equal("status=502 Bad", marked |> List.map _.Text |> String.concat "")

[<Fact>]
let ``message tokens get tones`` () =
    let tones =
        Render.message "user=\"ada\" took 120ms TimeoutException"
        |> List.map (fun segment -> segment.Text, segment.Tone)

    Assert.Contains(("user", PropertyKey), tones)
    Assert.Contains(("\"ada\"", StringLiteral), tones)
    Assert.Contains(("120ms", NumberLiteral), tones)
    Assert.Contains(("TimeoutException", ExceptionName), tones)

[<Fact>]
let ``invalid regex is rejected with the parser's reason`` () =
    match SearchPattern.create { Text = "(unclosed"; Mode = RegexSearch; MatchCase = false } with
    | Ok _ -> failwith "expected a rejection"
    | Error message -> Assert.StartsWith("Invalid regex:", message)

[<Fact>]
let ``blank queries are rejected`` () =
    Assert.True(Result.isError (SearchPattern.create { Text = "  "; Mode = PlainSearch; MatchCase = false }))

[<Fact>]
let ``plain search respects match case`` () =
    Assert.Equal(1, (SearchPattern.find (searchPattern "Error" PlainSearch true) "Error error").Length)
    Assert.Equal(2, (SearchPattern.find (searchPattern "Error" PlainSearch false) "Error error").Length)

[<Fact>]
let ``times convert into the chosen zone`` () =
    let instant = DateTimeOffset(2026, 10, 6, 21, 30, 0, TimeSpan.Zero)
    let adelaide = Time.context TimeZoneInfo.Utc instant (Zone "Australia/Adelaide")
    Assert.Equal("2026-10-07 08:00:00.000", Time.format adelaide instant)
    Assert.Equal("2026-10-06 21:30:00.000", Time.format Time.utcContext instant)
    Assert.Contains("UTC+10:30", adelaide.Caption)

[<Fact>]
let ``highlight handles adjacent and repeated matches`` () =
    let segments = [ Segment.make Plain "abab"; Segment.make PropertyKey "ab" ]
    let marked = Render.highlight (Some(searchPattern "ab" PlainSearch true)) segments
    Assert.All(marked, fun segment -> Assert.True segment.Hit)
    Assert.Equal("ababab", marked |> List.map _.Text |> String.concat "")
    let partial = Render.highlight (Some(searchPattern "b" PlainSearch true)) [ Segment.make Plain "abc" ]
    Assert.Equal<(string * bool) list>([ "a", false; "b", true; "c", false ], partial |> List.map (fun s -> s.Text, s.Hit))
