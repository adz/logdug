namespace LogDug

open System
open System.IO
open Axial
open LogDug.Files

/// One step of a running search: the totals so far, and the file that just finished if it matched.
type SearchStep =
    { Summary: SearchSummary
      Found: FileHits option }

/// Search as a stream: walk the tree lazily, search files in parallel by streaming their lines, and stop
/// everything (the walk, open files, in-flight searches) as soon as the hit cap is reached or the consumer stops.
module Search =
    let skippedFolders = set [ ".git"; ".vs"; ".idea"; "node_modules" ]

    let private binaryExtensions =
        set
            [ ".dll"; ".exe"; ".pdb"; ".so"; ".dylib"; ".a"; ".o"; ".obj"; ".lib"; ".class"; ".jar"
              ".png"; ".jpg"; ".jpeg"; ".gif"; ".bmp"; ".ico"; ".webp"; ".svgz"
              ".ttf"; ".otf"; ".woff"; ".woff2"; ".pdf"; ".mp3"; ".mp4"; ".mov"; ".avi"; ".wav"
              ".nupkg"; ".snupkg"; ".7z"; ".rar"; ".xz"; ".bz2"; ".db"; ".sqlite"; ".cache" ]

    let maxHitsPerFile = 500
    let maxHits = 20_000
    let parallelism = Parallelism.bounded 4

    let private previewWidth = 180

    let emptySummary =
        { FilesScanned = 0
          FilesMatched = 0
          HitCount = 0
          Truncated = false }

    /// Trims a long line to a window around the match, adjusting the match offset to suit.
    let preview (line: string) (start: int) =
        let expanded = line.Replace('\t', ' ')

        if expanded.Length <= previewWidth then
            expanded, start
        else
            let from = max 0 (min (start - 24) (expanded.Length - previewWidth))
            let prefix = if from > 0 then "…" else ""
            prefix + expanded.Substring(from, min previewWidth (expanded.Length - from)), start - from + prefix.Length

    let hitOf (pattern: SearchPattern) (lineNumber: int) (line: string) =
        match SearchPattern.find pattern line with
        | struct (start, length) :: _ ->
            let text, shifted = preview line start

            Some
                { Line = lineNumber
                  Preview = text
                  MatchStart = shifted
                  MatchLength = min length (text.Length - shifted) }
        | [] -> None

    let isSearchable (node: Node) =
        node.Kind = NodeKind.File
        && not (binaryExtensions.Contains(Path.GetExtension(node.Name).ToLowerInvariant()))

    let descend (node: Node) =
        not (node.Kind = NodeKind.Folder && skippedFolders.Contains node.Name)

    /// The hits in one file, read line by line. Reading stops after one hit past the per-file cap.
    /// A file that cannot be read counts as scanned with no hits.
    let fileHits (pattern: SearchPattern) (order: int) (node: Node) : Flow<'env, Never, FileHits> when 'env :> IHasFiles =
        Files.lines node
        |> FlowStream.indexed
        |> FlowStream.choose (fun (index, line) -> hitOf pattern (index + 1) line)
        |> FlowStream.take (maxHitsPerFile + 1)
        |> FlowStream.runCollect
        |> Flow.fold Flow.succeed (fun _ -> Flow.succeed [])
        |> Flow.map (fun hits ->
            { Order = order
              Node = node
              Hits = List.truncate maxHitsPerFile hits
              Truncated = hits.Length > maxHitsPerFile })

    let private record (summary: SearchSummary) (found: FileHits) =
        let hitCount = summary.HitCount + found.Hits.Length

        { FilesScanned = summary.FilesScanned + 1
          FilesMatched = summary.FilesMatched + (if found.Hits.IsEmpty then 0 else 1)
          HitCount = hitCount
          Truncated = summary.Truncated || found.Truncated || hitCount >= maxHits }

    /// Every searchable file under `root`, as a stream of steps in completion order. Each step carries the
    /// running totals; `Found` is set for files that matched. The stream ends with the step that reaches
    /// `maxHits`, whose summary is marked truncated; ending it stops the walk and any in-flight files.
    let run (pattern: SearchPattern) (root: Node) : FlowStream<'env, FilesError, SearchStep> when 'env :> IHasFiles =
        Files.walk descend root
        |> FlowStream.filter isSearchable
        |> FlowStream.indexed
        |> FlowStream.mapFlowPar parallelism (fun (order, node) -> fileHits pattern order node |> Flow.widenError)
        |> FlowStream.scan
            (fun (step, _) found ->
                { Summary = record step.Summary found
                  Found = if found.Hits.IsEmpty then None else Some found },
                step.Summary.HitCount >= maxHits)
            ({ Summary = emptySummary; Found = None }, false)
        |> FlowStream.takeWhile (fun (_, alreadyCapped) -> not alreadyCapped)
        |> FlowStream.map fst
