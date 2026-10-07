namespace LogsDigger

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

    // ---- documents -----------------------------------------------------------------------------------------

    let private splitLines (text: string) =
        if text = "" then [||] else
        let lines = text.Replace("\r\n", "\n").Split('\n')
        // A trailing newline is a terminator, not an extra empty line.
        if lines.Length > 1 && lines[lines.Length - 1] = "" then Array.take (lines.Length - 1) lines else lines

    let private countLevels (entries: LogEntry array) =
        entries |> Array.countBy _.Level |> Map.ofArray

    let parse (options: Options) (text: string) : LogDocument =
        let lines = splitLines text
        let sample = lines |> Array.truncate 400

        // A file whose lines carry timestamps or levels groups unmarked lines (stack traces, wrapped output)
        // under the preceding entry. A file without any such markers shows each line as its own entry.
        let structured =
            sample |> Array.exists (fun line -> (tryHeader options line).IsSome || (tryJsonEntry line).IsSome)

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

            match tryJsonEntry line with
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
          Kind = if jsonCount > 0 && jsonCount * 2 >= entries.Length then JsonLines else PlainText
          LevelCounts = countLevels entries }

    let binary =
        { Entries = [||]
          LineCount = 0
          Kind = Binary
          LevelCounts = Map.empty }

    /// The entry's JSON, indented for reading, or None for plain-text entries.
    let prettyJson (entry: LogEntry) =
        match entry.Format with
        | JsonEntry ->
            try
                Some(Json.reindent entry.Lines[0])
            with _ ->
                None
        | PlainEntry -> None
