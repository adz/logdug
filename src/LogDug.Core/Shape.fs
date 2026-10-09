namespace LogDug

open System
open LogDug.Files

type TreeRow =
    { Key: string
      Node: Node
      Name: string
      Depth: int
      IsContainer: bool
      IsExpanded: bool
      IsLoading: bool
      IsSelected: bool
      Detail: string
      Error: string option }

type ResultRow =
    | FileHeader of order: int * path: string * count: string
    | HitLine of order: int * hitIndex: int * line: int * before: string * matched: string * after: string * isActive: bool

type LevelChip =
    { Level: Level
      Count: int
      IsOn: bool }

/// Pure projections of the model into the shapes the screen shows. The UI layer only binds them.
module Shape =
    let size (bytes: int64) =
        if bytes < 1024L then $"{bytes} B"
        elif bytes < 1024L * 1024L then $"{float bytes / 1024.0:F1} KB"
        elif bytes < 1024L * 1024L * 1024L then $"{float bytes / 1024.0 / 1024.0:F1} MB"
        else $"{float bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"

    /// The tree flattened into the rows currently visible, depth first.
    let treeRows (model: Model) : TreeRow list =
        let tree = model.Tree

        let rec rows depth (nodes: Node list) =
            nodes
            |> List.collect (fun node ->
                let key = Node.key node
                let expanded = tree.Expanded.Contains key

                let row =
                    { Key = key
                      Node = node
                      Name = node.Name
                      Depth = depth
                      IsContainer = Node.isContainer node
                      IsExpanded = expanded
                      IsLoading = tree.Loading.Contains key
                      IsSelected = model.SelectedNode = Some key
                      Detail = if node.Kind = NodeKind.File then size node.Size else ""
                      Error = tree.Failed.TryFind key }

                let children =
                    if expanded then
                        tree.Children.TryFind key |> Option.map (rows (depth + 1)) |> Option.defaultValue []
                    else
                        []

                row :: children)

        tree.Children.TryFind(Node.key tree.Root) |> Option.map (rows 0) |> Option.defaultValue []

    /// The entries to list: those at a shown level and, when `filter` is set, those the search matches.
    let visibleEntries (levels: Set<Level>) (filter: SearchPattern option) (collapsed: Set<int>) (document: LogDocument) =
        let hidden = LogDocument.hiddenBy document collapsed

        let atLevel =
            if levels.Count = Level.all.Length && collapsed.IsEmpty then
                document.Entries
            else
                document.Entries |> Array.filter (fun entry -> levels.Contains entry.Level && not (hidden entry.Index))

        match filter with
        | Some pattern -> atLevel |> Array.filter (fun entry -> Render.countMatches (Some pattern) entry > 0)
        | None -> atLevel

    let levelChips (model: Model) =
        match model.Viewer with
        | Showing file ->
            let chips =
                Level.all
                |> List.choose (fun level ->
                    match file.Document.LevelCounts.TryFind level with
                    | Some count when count > 0 -> Some { Level = level; Count = count; IsOn = model.Levels.Contains level }
                    | _ -> None)

            // A file with no levels at all (CSV, JSON, YAML) has nothing to filter by.
            if chips |> List.forall (fun chip -> chip.Level = Level.NoLevel) then [] else chips
        | _ -> []

    let resultRows (model: Model) : ResultRow list =
        let search = model.Search

        search.Results
        |> List.map (fun file ->
            let count =
                if file.Truncated then $"{file.Hits.Length}+" else string file.Hits.Length

            FileHeader(file.Order, Location.display model.RootPath file.Node.Location, count)
            :: (file.Hits
                |> List.mapi (fun hitIndex hit ->
                    let start = min hit.MatchStart hit.Preview.Length
                    let length = min hit.MatchLength (hit.Preview.Length - start)
                    let before = hit.Preview.Substring(0, start).TrimStart()
                    // Keep the match near the left edge so a narrow results panel still shows it.
                    let before = if before.Length > 28 then "…" + before.Substring(before.Length - 28) else before

                    HitLine(
                        file.Order,
                        hitIndex,
                        hit.Line,
                        before,
                        hit.Preview.Substring(start, length),
                        hit.Preview.Substring(start + length),
                        search.Cursor = Some { Order = file.Order; Hit = hitIndex }
                    ))))
        |> List.concat

    let searchStatus (model: Model) =
        let plural count (one: string) (many: string) = if count = 1 then $"{count} {one}" else $"{count:N0} {many}"

        match model.Search.Status with
        | Idle -> ""
        | Invalid message -> message
        | Running summary ->
            let matches = plural summary.HitCount "match" "matches"
            let files = plural summary.FilesMatched "file" "files"
            $"Searching… {matches} in {files} · {summary.FilesScanned} scanned"
        | Finished summary when summary.HitCount = 0 ->
            let files = plural summary.FilesScanned "file" "files"
            $"No matches · {files} scanned"
        | Finished summary ->
            let more = if summary.Truncated then "+" else ""
            let matches = plural summary.HitCount "match" "matches"
            let files = plural summary.FilesMatched "file" "files"
            $"{matches}{more} in {files} · {summary.FilesScanned} scanned"

    let cursorText (model: Model) =
        let total = model.Search.Results |> List.sumBy _.Hits.Length

        match model.Search.Cursor with
        | _ when total = 0 -> ""
        | None -> $"– / {total}"
        | Some cursor ->
            let before =
                model.Search.Results |> List.takeWhile (fun file -> file.Order <> cursor.Order) |> List.sumBy _.Hits.Length

            $"{before + cursor.Hit + 1} / {total}"

    let viewerTitle (model: Model) =
        match model.Viewer with
        | NothingOpen -> ""
        | Opening node
        | Showing { Node = node }
        | Unreadable(node, _) -> Location.display model.RootPath node.Location

    let viewerSummary (model: Model) =
        match model.Viewer with
        | Showing { Node = node; Document = document } ->
            let kind =
                match document.Kind with
                | JsonLines -> "JSON lines"
                | Delimited -> "CSV"
                | Structured label -> label
                | PlainText -> "Text"
                | Binary -> "Binary"

            $"{kind} · {document.LineCount:N0} lines · {document.Entries.Length:N0} entries · {size node.Size}"
        | Opening _ -> "Loading…"
        | Unreadable(_, message) -> message
        | NothingOpen -> ""
