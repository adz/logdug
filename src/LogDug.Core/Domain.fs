namespace LogDug

open System
open LogDug.Files

[<RequireQualifiedAccess>]
type Level =
    | Trace
    | Debug
    | Info
    | Warn
    | Error
    | Fatal
    | NoLevel

module Level =
    let all = [ Level.Trace; Level.Debug; Level.Info; Level.Warn; Level.Error; Level.Fatal; Level.NoLevel ]

    let label level =
        match level with
        | Level.Trace -> "TRACE"
        | Level.Debug -> "DEBUG"
        | Level.Info -> "INFO"
        | Level.Warn -> "WARN"
        | Level.Error -> "ERROR"
        | Level.Fatal -> "FATAL"
        | Level.NoLevel -> "—"

    let name level =
        match level with
        | Level.Trace -> "Trace"
        | Level.Debug -> "Debug"
        | Level.Info -> "Info"
        | Level.Warn -> "Warn"
        | Level.Error -> "Error"
        | Level.Fatal -> "Fatal"
        | Level.NoLevel -> "Other"

    let tryParse (text: string) =
        match text.Trim().ToUpperInvariant() with
        | "TRACE" | "TRC" | "VERBOSE" | "VRB" | "FINEST" | "FINER" -> Some Level.Trace
        | "DEBUG" | "DBG" | "FINE" -> Some Level.Debug
        | "INFO" | "INF" | "INFORMATION" | "NOTICE" | "I" -> Some Level.Info
        | "WARN" | "WARNING" | "WRN" | "W" -> Some Level.Warn
        | "ERROR" | "ERR" | "EROR" | "E" | "SEVERE" -> Some Level.Error
        | "FATAL" | "FTL" | "CRITICAL" | "CRIT" | "CRT" | "EMERG" | "EMERGENCY" | "ALERT" | "PANIC" -> Some Level.Fatal
        | _ -> None

    /// Numeric levels used by pino and bunyan.
    let ofNumber (value: float) =
        if value >= 60.0 then Level.Fatal
        elif value >= 50.0 then Level.Error
        elif value >= 40.0 then Level.Warn
        elif value >= 30.0 then Level.Info
        elif value >= 20.0 then Level.Debug
        else Level.Trace

type ValueKind =
    | TextValue
    | NumberValue
    | BoolValue
    | NullValue
    | StructuredValue

type Field =
    { Key: string
      Value: string
      Kind: ValueKind }

type EntryFormat =
    | PlainEntry
    | JsonEntry
    /// A row of a delimited (CSV/TSV) file. Cells are padded to `widths` so the columns line up in a mono font.
    | TableEntry of widths: int array * isHeader: bool

type LogEntry =
    { Index: int
      /// 1-based line number of the entry's first line.
      Line: int
      /// The entry exactly as written, one element per physical line.
      Lines: string array
      Timestamp: DateTimeOffset option
      Level: Level
      Message: string
      /// Lines after the first: stack traces, wrapped output, or a JSON entry's exception text.
      Continuation: string array
      Fields: Field list
      Format: EntryFormat }

type DocumentKind =
    | PlainText
    | JsonLines
    /// A CSV/TSV table; `hasHeader` says whether its first row is column names.
    | Delimited of hasHeader: bool
    /// A JSON, XML or YAML file, shown line by line with collapsible regions.
    | Structured of label: string
    | Binary

type LogDocument =
    { Entries: LogEntry array
      LineCount: int
      Kind: DocumentKind
      LevelCounts: Map<Level, int>
      /// Collapsible regions: the entry that opens one, to the last entry it hides.
      Folds: Map<int, int> }

type TimeDisplay =
    | Utc
    | Local
    | Zone of zoneId: string

type SearchMode =
    | PlainSearch
    | RegexSearch

type SearchQuery =
    { Text: string
      Mode: SearchMode
      MatchCase: bool }

type Hit =
    { Line: int
      Preview: string
      MatchStart: int
      MatchLength: int }

type FileHits =
    { /// Position of the file in the walk, so results can be shown in tree order whatever order they finish in.
      Order: int
      Node: Node
      Hits: Hit list
      Truncated: bool }

module LogDocument =
    /// The entries a set of collapsed regions hides, as a predicate on entry index.
    let hiddenBy (document: LogDocument) (collapsed: Set<int>) : (int -> bool) =
        if collapsed.IsEmpty then
            fun _ -> false
        else
            // Outermost collapsed regions first; anything inside one is hidden whether or not it is collapsed itself.
            let ranges =
                collapsed
                |> Seq.choose (fun header -> document.Folds.TryFind header |> Option.map (fun last -> header + 1, last))
                |> Seq.sort
                |> Array.ofSeq

            fun index -> ranges |> Array.exists (fun (first, last) -> index >= first && index <= last)

    /// Whether any entry has a timestamp, so the viewer can leave out an always-empty time column.
    let hasTimestamps (document: LogDocument) =
        document.Entries |> Array.exists (fun entry -> entry.Timestamp.IsSome)

    let hasLevels (document: LogDocument) =
        document.Entries |> Array.exists (fun entry -> entry.Level <> Level.NoLevel)

    /// The entry that contains `line`: the last entry starting at or before it.
    let entryAtLine (document: LogDocument) (line: int) =
        let entries = document.Entries
        let mutable low = 0
        let mutable high = entries.Length - 1
        let mutable found = None

        while low <= high do
            let middle = (low + high) / 2

            if entries[middle].Line <= line then
                found <- Some middle
                low <- middle + 1
            else
                high <- middle - 1

        found

type SearchSummary =
    { FilesScanned: int
      FilesMatched: int
      HitCount: int
      Truncated: bool }
