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

    /// A query is one or more terms joined by ` AND ` (the line must also match) or ` NOT ` (the line must not).
    /// A query without either is a single term, so ordinary searches behave as before.
    let private operator = Regex(@"\s+(AND|NOT)\s+", RegexOptions.CultureInvariant)

    let terms (text: string) : (bool * string) list =
        if not (operator.IsMatch(" " + text)) then [ true, text ] else
        let parts = operator.Split(" " + text.Trim())

        [ yield true, parts[0].Trim()
          for index in 1..2 .. parts.Length - 2 -> parts[index] = "AND", parts[index + 1].Trim() ]
        |> List.filter (fun (isInclude, term) -> not (isInclude && term = ""))

    /// Appends a term to a query text, with the operator that includes or excludes it.
    let addTerm (isInclude: bool) (text: string) (term: string) =
        let existing = text.Trim()

        if existing = "" then (if isInclude then term else "NOT " + term)
        else existing + (if isInclude then " AND " else " NOT ") + term

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
            terms query.Text
            |> List.tryPick (fun (_, term) ->
                try
                    Regex(term, regexOptions query, regexTimeout) |> ignore
                    None
                with :? ArgumentException as error ->
                    Some error.Message)

    let private usable =
        Constraint.customWith "a non-blank query that compiles in its search mode" (fun (query: SearchQuery) ->
            if String.IsNullOrWhiteSpace query.Text then
                Error(Violation.Atomic(AtomicViolation.Described("Type something to search for.", None)))
            elif terms query.Text |> List.forall (fun (isInclude, _) -> not isInclude) then
                Error(Violation.Atomic(AtomicViolation.Described("Add a term to search for; NOT only removes matches.", None)))
            else
                match regexError query with
                | Some message -> Error(Violation.Atomic(AtomicViolation.Described($"Invalid regex: {message}", None)))
                | None -> Ok())

    let private termFinder (query: SearchQuery) (term: string) =
        match query.Mode with
        | PlainSearch -> plainFinder { query with Text = term }
        | RegexSearch -> regexFinder (Regex(term, regexOptions query ||| RegexOptions.Compiled, regexTimeout))

    let private construct (query: SearchQuery) =
        let all = terms query.Text
        let includes = all |> List.filter fst |> List.map (snd >> termFinder query)
        let excludes = all |> List.filter (fst >> not) |> List.map (snd >> termFinder query)

        let find (text: string) =
            match includes, excludes with
            | [ only ], [] -> only text
            | _ ->
                if excludes |> List.exists (fun exclude -> not (exclude text).IsEmpty) then
                    []
                else
                    let found = includes |> List.map (fun include' -> include' text)

                    if found |> List.exists List.isEmpty then
                        []
                    else
                        // Highlights from every included term, in order and without overlap.
                        let mutable last = 0

                        found
                        |> List.concat
                        |> List.sortBy (fun struct (start, _) -> start)
                        |> List.filter (fun struct (start, length) ->
                            if start >= last then
                                last <- start + length
                                true
                            else
                                false)

        SearchPattern(query, find)

    let refinement =
        Refinement.define usable construct (fun (SearchPattern(query, _)) -> query)

    let create (query: SearchQuery) : Result<SearchPattern, string> =
        Refinement.create refinement query |> Result.mapError Violation.render

    let query (SearchPattern(query, _)) = query

    /// Every match in `text`, as (start, length) pairs in order.
    let find (SearchPattern(_, find)) (text: string) = find text
