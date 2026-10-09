module LogDug.Tests.QueryTests

open Xunit
open LogDug
open LogDug.Files
open LogDug.Tests.Support

let private find text (line: string) =
    SearchPattern.find (searchPattern text PlainSearch false) line |> List.map (fun struct (start, length) -> line.Substring(start, length))

[<Fact>]
let ``a query without operators is one literal term, spaces included`` () =
    Assert.Equal<(bool * string) list>([ true, "timed out" ], SearchPattern.terms "timed out")
    Assert.Equal<string list>([ "a b" ], find "a b" "x a b y")

[<Fact>]
let ``AND requires every term on the line and highlights them all`` () =
    Assert.Equal<string list>([ "timeout"; "retry" ], find "timeout AND retry" "timeout, will retry")
    Assert.Empty(find "timeout AND retry" "timeout only")

[<Fact>]
let ``NOT removes lines containing the term`` () =
    Assert.Equal<string list>([ "error" ], find "error NOT healthcheck" "error in payment")
    Assert.Empty(find "error NOT healthcheck" "error in healthcheck")

[<Fact>]
let ``a query of only exclusions is not searchable`` () =
    match SearchPattern.create { Text = "NOT noise"; Mode = PlainSearch; MatchCase = false } with
    | Error _ -> ()
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``adding terms builds the query additively`` () =
    Assert.Equal("foo", SearchPattern.addTerm true "" "foo")
    Assert.Equal("foo AND bar", SearchPattern.addTerm true "foo" "bar")
    Assert.Equal("foo NOT bar", SearchPattern.addTerm false "foo" "bar")

[<Fact>]
let ``regex terms are validated individually`` () =
    match SearchPattern.create { Text = "ok AND ("; Mode = RegexSearch; MatchCase = false } with
    | Error message -> Assert.Contains("Invalid regex", message)
    | Ok _ -> failwith "expected an error"

let private entry (display: string) =
    { Node = Node.ofName (Disk("/r/" + display)) (display.Substring(display.LastIndexOf '/' + 1)) 0L false
      Display = display }

[<Fact>]
let ``quick open prefers a file name match over a path match`` () =
    let index = [| entry "orders/logs/readme.txt"; entry "logs/orders-api.log"; entry "misc/other.log" |]
    let names = QuickOpen.matches index [] "orders" |> List.map _.Display
    Assert.Equal("logs/orders-api.log", names.Head)
    Assert.DoesNotContain("misc/other.log", names)

[<Fact>]
let ``quick open matches subsequences and ignores case`` () =
    let index = [| entry "app/Orders-Api.jsonl"; entry "app/startup.log" |]
    Assert.Equal<string list>([ "app/Orders-Api.jsonl" ], QuickOpen.matches index [] "oapijl" |> List.map _.Display)

[<Fact>]
let ``quick open with no query lists recent files first`` () =
    let index = [| entry "a.log"; entry "b.log"; entry "c.log" |]
    let recent = [ Node.key index[2].Node ]
    Assert.Equal<string list>([ "c.log"; "a.log"; "b.log" ], QuickOpen.matches index recent "" |> List.map _.Display)

[<Fact>]
let ``quick open ignores spaces in the query`` () =
    let index = [| entry "app/orders-api.log" |]
    Assert.Equal(1, QuickOpen.matches index [] "orders api" |> List.length)
