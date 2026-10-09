namespace LogDug

open System
open System.Globalization
open System.Text
open System.Text.RegularExpressions
open Reified

/// Turns raw file text into entries. Pure: no I/O and no ambient clock.
module LogParser =
    type Options =
        { /// Year assumed for formats that omit it, such as syslog's "Jan  2 03:04:05".
          ReferenceYear: int }

    let private options = RegexOptions.Compiled ||| RegexOptions.CultureInvariant

    let private timestampPattern =
        Regex(
            @"^\s*[\[(]?(?<ts>"
            + @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,9})?(?:\s?(?:Z|[+-]\d{2}:?\d{2}))?"
            + @"|\d{4}/\d{2}/\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,9})?(?:\s?(?:Z|[+-]\d{2}:?\d{2}))?"
            + @"|(?<syslog>(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+\d{1,2}\s\d{2}:\d{2}:\d{2})"
            + @")[\])]?",
            options
        )

    let private leadingLevelPattern =
        Regex(
            @"^\s*[-|:]?\s*[\[(<]?\s*(?<level>TRACE|TRC|VERBOSE|VRB|DEBUG|DBG|INFORMATION|INFO|INF|NOTICE|WARNING|WARN|WRN|ERROR|ERR|FATAL|FTL|CRITICAL|CRIT|CRT|EMERG|ALERT|PANIC|SEVERE)(?![A-Za-z])\s*[\])>]?\s*[-|:]?\s?",
            options ||| RegexOptions.IgnoreCase
        )

    let private embeddedLevelPattern =
        Regex(
            @"(?<![\w.])(?<level>TRACE|VERBOSE|DEBUG|INFO|NOTICE|WARN|WARNING|ERROR|FATAL|CRITICAL|SEVERE)(?![\w.])",
            options
        )

    let private normaliseTimestamp (text: string) =
        let builder = StringBuilder(text.Replace(',', '.').Replace('/', '-'))

        if builder.Length > 10 && builder[10] = ' ' then
            builder[10] <- 'T'

        let normalised = builder.ToString().Replace(" Z", "Z")
        // "+1000" and " +10:00" become "+10:00" so DateTimeOffset can parse them.
        Regex.Replace(normalised, @"\s?([+-])(\d{2}):?(\d{2})$", "$1$2:$3")

    let parseTimestamp (options: Options) (text: string) (isSyslog: bool) : DateTimeOffset option =
        let styles = DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal

        if isSyslog then
            let collapsed = Regex.Replace(text, @"\s+", " ")

            match
                DateTime.TryParseExact(
                    $"{options.ReferenceYear} {collapsed}",
                    "yyyy MMM d HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                )
            with
            | true, value -> Some(DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
            | false, _ -> None
        else
            match DateTimeOffset.TryParse(normaliseTimestamp text, CultureInfo.InvariantCulture, styles) with
            | true, value -> Some value
            | false, _ -> None

    type private Header =
        { Timestamp: DateTimeOffset option
          Level: Level option
          Message: string }

    /// Recognises a line that starts a new entry: one beginning with a timestamp or a level token.
    let private tryHeader (options: Options) (line: string) : Header option =
        let timestamp = timestampPattern.Match line

        if timestamp.Success then
            let rest = line.Substring(timestamp.Length)
            let level = leadingLevelPattern.Match rest

            if level.Success then
                Some
                    { Timestamp = parseTimestamp options timestamp.Groups["ts"].Value timestamp.Groups["syslog"].Success
                      Level = Level.tryParse level.Groups["level"].Value
                      Message = rest.Substring(level.Length).TrimStart() }
            else
                let embedded = embeddedLevelPattern.Match(rest, 0, min rest.Length 80)

                Some
                    { Timestamp = parseTimestamp options timestamp.Groups["ts"].Value timestamp.Groups["syslog"].Success
                      Level = if embedded.Success then Level.tryParse embedded.Groups["level"].Value else None
                      Message = rest.TrimStart(' ', '\t', '-', '|', ':') }
        else
            let level = leadingLevelPattern.Match line

            if level.Success && level.Index = 0 && not (Char.IsWhiteSpace(if line.Length > 0 then line[0] else ' ')) then
                Some
                    { Timestamp = None
                      Level = Level.tryParse level.Groups["level"].Value
                      Message = line.Substring(level.Length).TrimStart() }
            else
                None

    // ---- JSON lines ----------------------------------------------------------------------------------------

    let private timestampKeys = [ "@t"; "timestamp"; "@timestamp"; "time"; "ts"; "datetime"; "date"; "t"; "Timestamp" ]
    let private levelKeys = [ "@l"; "level"; "lvl"; "severity"; "loglevel"; "log.level"; "levelname"; "Level"; "LogLevel" ]
    let private messageKeys = [ "@m"; "message"; "msg"; "@mt"; "text"; "log"; "Message"; "MessageTemplate"; "RenderedMessage" ]
    let private exceptionKeys = [ "@x"; "exception"; "Exception"; "stack"; "stack_trace"; "stacktrace"; "exc_info"; "error.stack" ]

    let rec private compact (data: Data) =
        match data with
        | Data.Null -> "null"
        | Data.Text text -> "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\""
        | Data.Number token -> token
        | Data.Bool value -> if value then "true" else "false"
        | Data.List items -> "[" + String.Join(",", items |> List.map compact) + "]"
        | Data.Object fields -> "{" + String.Join(",", fields |> List.map (fun (key, value) -> $"\"{key}\":{compact value}")) + "}"

    let private scalarText (data: Data) =
        match data with
        | Data.Text text -> text
        | other -> compact other

    let private fieldOf (key: string) (data: Data) =
        let kind =
            match data with
            | Data.Text _ -> TextValue
            | Data.Number _ -> NumberValue
            | Data.Bool _ -> BoolValue
            | Data.Null -> NullValue
            | Data.List _
            | Data.Object _ -> StructuredValue

        { Key = key; Value = scalarText data; Kind = kind }

    let private jsonTimestamp (data: Data) =
        match data with
        | Data.Text text -> parseTimestamp { ReferenceYear = 2000 } text false
        | Data.Number token ->
            let milliseconds =
                match Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, value when value > 1e17 -> Some(value / 1e6)
                | true, value when value > 1e14 -> Some(value / 1e3)
                | true, value when value > 1e11 -> Some value
                | true, value when value > 1e8 -> Some(value * 1e3)
                | _ -> None

            milliseconds
            |> Option.filter (fun ms -> ms < 253402300799999.0)
            |> Option.map (fun ms -> DateTimeOffset.FromUnixTimeMilliseconds(int64 ms))
        | _ -> None

    let private jsonLevel (data: Data) =
        match data with
        | Data.Text text -> Level.tryParse text
        | Data.Number token ->
            match Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value -> Some(Level.ofNumber value)
            | false, _ -> None
        | _ -> None

    let private templateHole = Regex(@"\{(?<name>[@$]?[\w.]+)(?:[,:][^}]*)?\}", options)

    /// Renders a Serilog message template (`@mt`) by substituting property values into its holes.
    let private renderTemplate (template: string) (fields: (string * Data) list) =
        let used = Collections.Generic.HashSet<string>()

        let rendered =
            templateHole.Replace(
                template,
                MatchEvaluator(fun hole ->
                    let name = hole.Groups["name"].Value.TrimStart('@', '$')

                    match fields |> List.tryFind (fun (key, _) -> key = name) with
                    | Some(key, value) ->
                        used.Add key |> ignore
                        scalarText value
                    | None -> hole.Value)
            )

        rendered, used

    let private tryFirst keys (fields: (string * Data) list) =
        keys
        |> List.tryPick (fun key -> fields |> List.tryFind (fun (name, _) -> name = key))

    let private tryJsonEntry (line: string) =
        let trimmed = line.TrimStart()

        if trimmed.StartsWith "{" && line.TrimEnd().EndsWith "}" then
            try
                match Json.parseData line with
                | Data.Object fields -> Some fields
                | _ -> None
            with _ ->
                None
        else
            None

    let private jsonEntry index lineNumber (line: string) (fields: (string * Data) list) =
        let timestampField = tryFirst timestampKeys fields
        let levelField = tryFirst levelKeys fields
        let exceptionField = tryFirst exceptionKeys fields
        let messageField = tryFirst messageKeys fields

        let message, templateUsed =
            match messageField with
            | Some("@mt", Data.Text template) -> renderTemplate template fields
            | Some(_, value) -> scalarText value, Collections.Generic.HashSet()
            | None -> "", Collections.Generic.HashSet()

        let consumed =
            [ timestampField; levelField; exceptionField; messageField ]
            |> List.choose (Option.map fst)
            |> Set.ofList
            |> Set.union (Set.ofSeq templateUsed)
            |> Set.add "@mt"
            |> Set.add "@i"

        let level =
            match levelField with
            | Some(_, value) -> jsonLevel value |> Option.defaultValue Level.NoLevel
            // Serilog's compact format omits @l for Information.
            | None when fields |> List.exists (fun (key, _) -> key = "@t") -> Level.Info
            | None -> Level.NoLevel

        let continuation =
            match exceptionField with
            | Some(_, Data.Null)
            | None -> [||]
            | Some(_, value) -> (scalarText value).Replace("\r\n", "\n").Split('\n')

        { Index = index
          Line = lineNumber
          Lines = [| line |]
          Timestamp = timestampField |> Option.bind (snd >> jsonTimestamp)
          Level = level
          Message = message
          Continuation = continuation
          Fields =
            fields
            |> List.filter (fun (key, _) -> not (consumed.Contains key))
            |> List.map (fun (key, value) -> fieldOf key value)
          Format = JsonEntry }

    // ---- delimited tables ----------------------------------------------------------------------------------

    /// A row's cells with the 1-based line it starts on and the physical lines it spans (quoted cells can hold newlines).
    type private Row = { Line: int; Raw: string array; Cells: string array }

    /// RFC 4180 style parsing: quoted cells, doubled quotes, and newlines inside quotes.
    let private readRows (delimiter: char) (lines: string array) : Row array =
        let rows = ResizeArray<Row>()
        let mutable index = 0

        while index < lines.Length do
            let startLine = index + 1
            let raw = ResizeArray<string>()
            let cells = ResizeArray<string>()
            let cell = StringBuilder()
            let mutable inQuotes = false
            let mutable finished = false

            while not finished && index < lines.Length do
                let line = lines[index]
                raw.Add line
                index <- index + 1
                let mutable position = 0

                while position < line.Length do
                    let c = line[position]

                    if inQuotes then
                        if c = '"' && position + 1 < line.Length && line[position + 1] = '"' then
                            cell.Append '"' |> ignore
                            position <- position + 1
                        elif c = '"' then
                            inQuotes <- false
                        else
                            cell.Append c |> ignore
                    elif c = '"' && cell.Length = 0 then
                        inQuotes <- true
                    elif c = delimiter then
                        cells.Add(cell.ToString())
                        cell.Clear() |> ignore
                    else
                        cell.Append c |> ignore

                    position <- position + 1

                if inQuotes then cell.Append '\n' |> ignore else finished <- true

            cells.Add(cell.ToString().TrimEnd('\n'))
            rows.Add { Line = startLine; Raw = raw.ToArray(); Cells = cells.ToArray() }

        rows.ToArray()

    let private maxColumnWidth = 40

    /// Recognises comma, tab, semicolon or pipe separated tables: at least three non-empty lines whose rows
    /// nearly all have the same number of cells, and more than one.
    /// Whether the first row reads as column names rather than data: text in every cell, no repeats, and either
    /// numbers further down that column or names that look like identifiers.
    let private looksLikeHeader (rows: Row array) =
        let isNumber (cell: string) =
            Double.TryParse(cell.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) |> fst

        let first = rows[0].Cells
        let identifier = Regex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$", options)

        if rows.Length < 2 then false
        elif first |> Array.exists (fun cell -> cell.Trim() = "" || isNumber cell) then false
        elif (first |> Array.map (fun cell -> cell.Trim().ToLowerInvariant()) |> Array.distinct).Length <> first.Length then false
        else
            let below = rows |> Array.skip 1 |> Array.truncate 50
            let numericBelow =
                first |> Array.mapi (fun column _ -> below |> Array.exists (fun row -> column < row.Cells.Length && isNumber row.Cells[column]))

            Array.exists id numericBelow || first |> Array.forall (fun cell -> identifier.IsMatch(cell.Trim()))

    /// The table's entries and whether its first row is a header. `header` overrides the guess.
    let private tryTable (header: bool option) (lines: string array) : (LogEntry array * bool) option =
        let sample = lines |> Array.filter (fun line -> line.Trim() <> "") |> Array.truncate 200

        // "2026-10-06 21:58:14,093 INFO ..." has a comma too, so lines that start like a log entry (and JSON
        // objects) rule a table out. A timestamp followed by a delimiter is just a table's first cell.
        let looksLikeLog (delimiter: char) (line: string) =
            let timestamp = timestampPattern.Match line
            let rest = if timestamp.Success then line.Substring timestamp.Length else ""
            (timestamp.Success && not (rest.StartsWith(string delimiter))) || (tryJsonEntry line).IsSome

        let candidate =
            if sample.Length < 3 then
                None
            else
                [ ','; '\t'; ';'; '|' ]
                |> List.tryPick (fun delimiter ->
                    let counts = readRows delimiter sample |> Array.map _.Cells.Length
                    let common = counts |> Array.countBy id |> Array.maxBy snd |> fst

                    let logLike = sample |> Array.filter (looksLikeLog delimiter) |> Array.length

                    if common > 1 && logLike * 10 < sample.Length && (counts |> Array.filter ((=) common) |> Array.length) * 10 >= counts.Length * 9 then
                        Some(delimiter, common)
                    else
                        None)

        candidate
        |> Option.map (fun (delimiter, columns) ->
            let rows = readRows delimiter lines |> Array.filter (fun row -> row.Raw |> Array.exists (fun line -> line.Trim() <> ""))

            let widths =
                Array.init columns (fun column ->
                    rows
                    |> Array.map (fun row -> if column < row.Cells.Length then row.Cells[column].Length else 0)
                    |> Array.max
                    |> min maxColumnWidth)

            let hasHeader = match header with Some chosen -> chosen | None -> looksLikeHeader rows
            let names = if hasHeader then rows[0].Cells else Array.init columns (fun column -> string (column + 1))

            let entries =
              rows
              |> Array.mapi (fun index row ->
                { Index = index
                  Line = row.Line
                  Lines = row.Raw
                  Timestamp = None
                  Level = Level.NoLevel
                  Message = String.Join(", ", row.Cells)
                  Continuation = [||]
                  Fields =
                    row.Cells
                    |> Array.mapi (fun column value ->
                        { Key = if column < names.Length then names[column] else ""
                          Value = value
                          Kind = TextValue })
                    |> List.ofArray
                  Format = TableEntry(widths, hasHeader && index = 0) })

            entries, hasHeader)

    // ---- documents -----------------------------------------------------------------------------------------

    let private splitLines (text: string) =
        if text = "" then [||] else
        let lines = text.Replace("\r\n", "\n").Split('\n')
        // A trailing newline is a terminator, not an extra empty line.
        if lines.Length > 1 && lines[lines.Length - 1] = "" then Array.take (lines.Length - 1) lines else lines

    /// Width of a line's leading whitespace (a tab counts as four), or -1 for a blank line.
    let private indentOf (line: string) =
        if String.IsNullOrWhiteSpace line then
            -1
        else
            let mutable width = 0
            let mutable index = 0

            while index < line.Length && (line[index] = ' ' || line[index] = '\t') do
                width <- width + (if line[index] = '\t' then 4 else 1)
                index <- index + 1

            width

    /// Foldable regions by indentation: a line followed by more deeply indented lines folds those lines away.
    /// The closing line of a block (`}` or `</a>`) is not indented further, so it stays visible when folded.
    /// Line i is entry i, because a structured file shows each line as its own entry.
    let foldRegions (lines: string array) : Map<int, int> =
        let regions = Collections.Generic.Dictionary<int, int>()
        let open' = Collections.Generic.Stack<struct (int * int)>()
        let mutable lastContent = -1

        let close (belowIndent: int) =
            while open'.Count > 0 && (let struct (_, indent) = open'.Peek() in indent >= belowIndent) do
                let struct (header, _) = open'.Pop()
                if lastContent > header then regions[header] <- lastContent

        lines
        |> Array.iteri (fun index line ->
            let indent = indentOf line

            if indent >= 0 then
                close indent
                open'.Push(struct (index, indent))
                lastContent <- index)

        close 0
        regions |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq

    let private countLevels (entries: LogEntry array) =
        entries |> Array.countBy _.Level |> Map.ofArray

    let rec parseWith (options: Options) (csvHeader: bool option) (text: string) : LogDocument =
        let lines = splitLines text

        match tryTable csvHeader lines with
        | Some(entries, hasHeader) ->
            { Entries = entries
              LineCount = lines.Length
              Kind = Delimited hasHeader
              LevelCounts = countLevels entries
              Folds = Map.empty }
        | None -> parseLog options lines None

    /// How a file's own name and content change how it is read: JSON, XML and YAML files show as lines whose
    /// indentation can be folded, and a minified JSON file is first spread over several lines.
    and parse (options: Options) (text: string) : LogDocument = parseWith options None text

    and parseFile (options: Options) (name: string) (text: string) : LogDocument = parseFileWith options None name text

    and parseFileWith (options: Options) (csvHeader: bool option) (name: string) (text: string) : LogDocument =
        let lower = name.ToLowerInvariant()
        let lower = if lower.EndsWith ".gz" then lower.Substring(0, lower.Length - 3) else lower
        let extension = (let dot = lower.LastIndexOf '.' in if dot < 0 then "" else lower.Substring dot)

        let xml = set [ ".xml"; ".csproj"; ".fsproj"; ".vbproj"; ".props"; ".targets"; ".config"; ".xaml"; ".axaml"; ".svg"; ".nuspec"; ".resx"; ".xsd"; ".plist" ]
        let lines = splitLines text

        let structured label (lines: string array) = parseLog options lines (Some label)

        if extension = ".json" && lines.Length = 1 && (lines[0].TrimStart().StartsWith "{" || lines[0].TrimStart().StartsWith "[") then
            let pretty = try Json.reindent lines[0] with _ -> lines[0]
            structured "JSON" (splitLines pretty)
        elif extension = ".json" && lines.Length > 1 && (tryJsonEntry lines[0]).IsNone then
            structured "JSON" lines
        elif xml.Contains extension then
            structured "XML" lines
        elif extension = ".yaml" || extension = ".yml" then
            structured "YAML" lines
        else
            parseWith options csvHeader text

    and private parseLog (options: Options) (lines: string array) (structuredAs: string option) : LogDocument =
        let sample = lines |> Array.truncate 400

        // A file whose lines carry timestamps or levels groups unmarked lines (stack traces, wrapped output)
        // under the preceding entry. A file without any such markers shows each line as its own entry.
        let forceLines = structuredAs.IsSome

        let structured =
            not forceLines
            && sample |> Array.exists (fun line -> (tryHeader options line).IsSome || (tryJsonEntry line).IsSome)

        let entries = ResizeArray<LogEntry>()
        let mutable pending: (int * string * Header * ResizeArray<string>) option = None
        let mutable jsonCount = 0

        let flush () =
            match pending with
            | Some(lineNumber, first, header, continuation) ->
                while continuation.Count > 0 && String.IsNullOrWhiteSpace continuation[continuation.Count - 1] do
                    continuation.RemoveAt(continuation.Count - 1)

                entries.Add
                    { Index = entries.Count
                      Line = lineNumber
                      Lines = Array.append [| first |] (continuation.ToArray())
                      Timestamp = header.Timestamp
                      Level = header.Level |> Option.defaultValue Level.NoLevel
                      Message = header.Message
                      Continuation = continuation.ToArray()
                      Fields = []
                      Format = PlainEntry }

                pending <- None
            | None -> ()

        let plain line = { Timestamp = None; Level = None; Message = line }

        lines
        |> Array.iteri (fun i line ->
            let lineNumber = i + 1

            match (if forceLines then None else tryJsonEntry line) with
            | Some fields ->
                flush ()
                jsonCount <- jsonCount + 1
                entries.Add(jsonEntry entries.Count lineNumber line fields)
            | None when not structured ->
                flush ()
                pending <- Some(lineNumber, line, plain line, ResizeArray())
                flush ()
            | None ->
                match tryHeader options line, pending with
                | Some header, _ ->
                    flush ()
                    pending <- Some(lineNumber, line, header, ResizeArray())
                | None, Some(_, _, _, continuation) -> continuation.Add line
                | None, None -> pending <- Some(lineNumber, line, plain line, ResizeArray()))

        flush ()
        let entries = entries.ToArray()

        { Entries = entries
          LineCount = lines.Length
          Kind =
            match structuredAs with
            | Some label -> Structured label
            | None -> if jsonCount > 0 && jsonCount * 2 >= entries.Length then JsonLines else PlainText
          LevelCounts = countLevels entries
          Folds = if forceLines then foldRegions lines else Map.empty }

    let binary =
        { Entries = [||]
          LineCount = 0
          Kind = Binary
          LevelCounts = Map.empty
          Folds = Map.empty }

    /// The entry's JSON, indented for reading, or None for plain-text entries.
    let prettyJson (entry: LogEntry) =
        match entry.Format with
        | JsonEntry ->
            try
                Some(Json.reindent entry.Lines[0])
            with _ ->
                None
        | PlainEntry
        | TableEntry _ -> None
