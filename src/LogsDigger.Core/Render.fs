namespace LogsDigger

open System
open System.Text
open System.Text.RegularExpressions

/// The colour role of a piece of text. The UI maps each tone to a theme brush.
type Tone =
    | Plain
    | Muted
    | StringLiteral
    | NumberLiteral
    | KeywordLiteral
    | PropertyKey
    | Punctuation
    | ExceptionName
    | Link
    | Identifier
    | StackFrame
    | NewLine

type Segment =
    { Text: string
      Tone: Tone
      Hit: bool }

module Segment =
    let make tone text = { Text = text; Tone = tone; Hit = false }
    let newLine = { Text = "\n"; Tone = NewLine; Hit = false }

/// Converts entries into coloured segments. Pure, so the same output drives the UI and the tests.
module Render =
    let private options = RegexOptions.Compiled ||| RegexOptions.CultureInvariant

    let private tokens =
        Regex(
            String.Join(
                "|",
                [ "(?<str>\"(?:[^\"\\\\]|\\\\.)*\")"
                  @"(?<url>\bhttps?://[^\s""'<>]+)"
                  @"(?<guid>\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b)"
                  @"(?<exn>\b(?:[A-Za-z_]\w*\.)*[A-Z]\w*(?:Exception|Error)\b)"
                  @"(?<key>\b[A-Za-z_][\w.\-]*(?==[^=]))"
                  @"(?<num>(?<![\w.])-?\d+(?:\.\d+)?(?:ms|s|%|KB|MB|GB)?(?![\w]))"
                  @"(?<lit>\b(?:true|false|null|True|False|None)\b)" ]
            ),
            options,
            TimeSpan.FromMilliseconds 100.0
        )

    let private frameStart = Regex(@"^\s+(?:at\s|File\s""|\.\.\.\s\d+\smore)|^\t", options)
    let private frameLocation = Regex(@"(?:in\s+)?[\w\\/.:\-]+\.\w+:(?:line\s)?\d+|\([\w.\-]+\.\w+:\d+\)", options)

    let private toneOf (found: Match) =
        if found.Groups["str"].Success then StringLiteral
        elif found.Groups["url"].Success then Link
        elif found.Groups["guid"].Success then Identifier
        elif found.Groups["exn"].Success then ExceptionName
        elif found.Groups["key"].Success then PropertyKey
        elif found.Groups["num"].Success then NumberLiteral
        else KeywordLiteral

    let private split (pattern: Regex) (toneOf: Match -> Tone) (baseTone: Tone) (text: string) =
        let segments = ResizeArray<Segment>()
        let mutable position = 0

        try
            for found in pattern.Matches text do
                if found.Index > position then
                    segments.Add(Segment.make baseTone (text.Substring(position, found.Index - position)))

                segments.Add(Segment.make (toneOf found) found.Value)
                position <- found.Index + found.Length

            if position < text.Length then
                segments.Add(Segment.make baseTone (text.Substring position))

            List.ofSeq segments
        with :? RegexMatchTimeoutException ->
            [ Segment.make baseTone text ]

    let message (text: string) = split tokens toneOf Plain text

    let continuationLine (line: string) =
        if frameStart.IsMatch line then
            split frameLocation (fun _ -> Link) StackFrame line
        else
            message line

    let private valueTone kind =
        match kind with
        | TextValue -> StringLiteral
        | NumberValue -> NumberLiteral
        | BoolValue
        | NullValue -> KeywordLiteral
        | StructuredValue -> Muted

    let fields (fields: Field list) =
        fields
        |> List.collect (fun field ->
            [ Segment.make Plain "  "
              Segment.make PropertyKey field.Key
              Segment.make Punctuation "="
              Segment.make (valueTone field.Kind) field.Value ])

    /// The entry's message, its structured fields, then every continuation line.
    let body (entry: LogEntry) =
        let first =
            match entry.Format with
            | PlainEntry -> message entry.Message
            | JsonEntry when entry.Message = "" && entry.Fields.IsEmpty -> [ Segment.make Muted entry.Lines[0] ]
            | JsonEntry -> message entry.Message @ fields entry.Fields

        let rest =
            entry.Continuation
            |> Array.toList
            |> List.collect (fun line -> Segment.newLine :: continuationLine line)

        first @ rest

    /// Splits segments so that every character inside a match is in a segment marked `Hit`.
    let highlight (pattern: SearchPattern option) (segments: Segment list) =
        match pattern with
        | None -> segments
        | Some pattern ->
            let text = StringBuilder()
            segments |> List.iter (fun segment -> text.Append(segment.Text) |> ignore)
            let ranges = SearchPattern.find pattern (text.ToString())

            if ranges.IsEmpty then
                segments
            else
                // Cut points: every match start and end plus every segment boundary, walked once in order.
                let marks = ranges |> List.collect (fun struct (start, length) -> [ start; start + length ]) |> List.toArray
                let result = ResizeArray<Segment>()
                let mutable offset = 0
                let mutable mark = 0
                let mutable hit = false

                for segment in segments do
                    let finish = offset + segment.Text.Length
                    let mutable cursor = offset

                    while cursor < finish do
                        while mark < marks.Length && marks[mark] <= cursor do
                            hit <- mark % 2 = 0
                            mark <- mark + 1

                        let next = if mark < marks.Length then min marks[mark] finish else finish
                        result.Add { segment with Text = segment.Text.Substring(cursor - offset, next - cursor); Hit = hit }
                        cursor <- next

                    offset <- finish

                List.ofSeq result

    let countMatches (pattern: SearchPattern option) (entry: LogEntry) =
        match pattern with
        | None -> 0
        | Some pattern -> entry.Lines |> Array.sumBy (fun line -> (SearchPattern.find pattern line).Length)
