namespace LogDug

open System
open System.Text.RegularExpressions
open Reified
open Reified.Refinements

/// A search query that is known to be usable: non-blank, and a valid regex when in regex mode.
/// The only way to build one is `SearchPattern.create`, so search code never re-checks the query.
type SearchPattern = private SearchPattern of query: SearchQuery * find: (string -> struct (int * int) list)

module SearchPattern =
    let private regexTimeout = TimeSpan.FromMilliseconds 250.0

    let private regexOptions query =
        RegexOptions.CultureInvariant
        ||| (if query.MatchCase then RegexOptions.None else RegexOptions.IgnoreCase)

    let private plainFinder (query: SearchQuery) =
        let comparison =
            if query.MatchCase then StringComparison.Ordinal else StringComparison.OrdinalIgnoreCase

        fun (text: string) ->
            let mutable results = []
            let mutable index = text.IndexOf(query.Text, comparison)

            while index >= 0 do
                results <- struct (index, query.Text.Length) :: results
                index <- if index + query.Text.Length >= text.Length then -1 else text.IndexOf(query.Text, index + query.Text.Length, comparison)

            List.rev results

    let private regexFinder (regex: Regex) =
        fun (text: string) ->
            try
                [ for found in regex.Matches text do
                      if found.Length > 0 then struct (found.Index, found.Length) ]
            with :? RegexMatchTimeoutException ->
                []

    let private regexError (query: SearchQuery) =
        match query.Mode with
        | PlainSearch -> None
        | RegexSearch ->
            try
                Regex(query.Text, regexOptions query, regexTimeout) |> ignore
                None
            with :? ArgumentException as error ->
                Some error.Message

    let private usable =
        Constraint.customWith "a non-blank query that compiles in its search mode" (fun (query: SearchQuery) ->
            if String.IsNullOrWhiteSpace query.Text then
                Error(Violation.Atomic(AtomicViolation.Described("Type something to search for.", None)))
            else
                match regexError query with
                | Some message -> Error(Violation.Atomic(AtomicViolation.Described($"Invalid regex: {message}", None)))
                | None -> Ok())

    let private construct (query: SearchQuery) =
        let find =
            match query.Mode with
            | PlainSearch -> plainFinder query
            | RegexSearch -> regexFinder (Regex(query.Text, regexOptions query ||| RegexOptions.Compiled, regexTimeout))

        SearchPattern(query, find)

    let refinement =
        Refinement.define usable construct (fun (SearchPattern(query, _)) -> query)

    let create (query: SearchQuery) : Result<SearchPattern, string> =
        Refinement.create refinement query |> Result.mapError Violation.render

    let query (SearchPattern(query, _)) = query

    /// Every match in `text`, as (start, length) pairs in order.
    let find (SearchPattern(_, find)) (text: string) = find text
