namespace LogDug

open System
open LogDug.Files

/// One file the quick-open box can jump to: the node and the path shown for it, relative to the root.
type QuickOpenEntry = { Node: Node; Display: string }

/// Fuzzy file matching for Ctrl+P. Pure, so the ranking is testable.
module QuickOpen =
    let maxResults = 50

    let private isSeparator (c: char) = c = '/' || c = '\\' || c = '.' || c = '-' || c = '_' || c = ' ' || c = '›'

    /// How well `query` matches `text` as an in-order subsequence (case-insensitive), or None when it doesn't.
    /// Consecutive characters, characters at word starts and matches inside the file name score higher.
    let score (query: string) (text: string) : int option =
        let query = String(query |> Seq.filter (Char.IsWhiteSpace >> not) |> Array.ofSeq)

        if query = "" then
            Some 0
        else
            let fileNameStart = max (text.LastIndexOfAny [| '/'; '\\' |] + 1) (text.LastIndexOf '›' + 1)
            let mutable total = 0
            let mutable position = 0
            let mutable previous = -2
            let mutable matched = true

            for wanted in query do
                if matched then
                    let found = text.IndexOf(string wanted, position, StringComparison.OrdinalIgnoreCase)

                    if found < 0 then
                        matched <- false
                    else
                        total <- total + 1
                        if found = previous + 1 then total <- total + 5
                        if found = 0 || isSeparator text[found - 1] then total <- total + 4
                        if found >= fileNameStart then total <- total + 2
                        previous <- found
                        position <- found + 1

            if matched then
                // Prefer shorter paths, and queries that sit wholly inside the file name.
                let inName = text.Substring(fileNameStart).Contains(query, StringComparison.OrdinalIgnoreCase)
                Some(total * 4 + (if inName then 60 else 0) - text.Length / 8)
            else
                None

    /// The best matches for `query`, highest score first. With no query, files in `recent` come first, then index order.
    let matches (index: QuickOpenEntry array) (recent: string list) (query: string) : QuickOpenEntry list =
        if String.IsNullOrWhiteSpace query then
            let recentFirst =
                recent |> List.choose (fun key -> index |> Array.tryFind (fun entry -> Node.key entry.Node = key))

            let others = index |> Array.filter (fun entry -> not (List.contains entry recentFirst))
            recentFirst @ List.ofArray others |> List.truncate maxResults
        else
            index
            |> Array.choose (fun entry -> score query entry.Display |> Option.map (fun value -> value, entry))
            |> Array.sortBy (fun (value, entry) -> -value, entry.Display.Length)
            |> Array.truncate maxResults
            |> Array.map snd
            |> List.ofArray
