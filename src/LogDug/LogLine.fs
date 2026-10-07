namespace LogDug.UI

open System
open Avalonia.Controls
open Avalonia.Controls.Documents
open LogDug

/// A TextBlock that renders its entry's coloured segments as inlines.
/// It reads the segments from its DataContext rather than a binding, because an F# type cannot expose the
/// public static property field that XAML bindings need. Each run gets a `tone-*` class (and `hit` for search
/// matches), so colours come from theme styles and follow light/dark switches.
type LogLine() =
    inherit TextBlock()

    static member ToneClass(tone: Tone) =
        match tone with
        | Plain -> "tone-plain"
        | Muted -> "tone-muted"
        | StringLiteral -> "tone-string"
        | NumberLiteral -> "tone-number"
        | KeywordLiteral -> "tone-keyword"
        | PropertyKey -> "tone-key"
        | Punctuation -> "tone-punct"
        | ExceptionName -> "tone-exception"
        | Link -> "tone-link"
        | Identifier -> "tone-id"
        | StackFrame -> "tone-frame"
        | NewLine -> "tone-plain"

    static member BuildInlines(segments: Segment array) =
        let inlines = InlineCollection()

        for segment in segments do
            if segment.Tone = NewLine then
                inlines.Add(LineBreak())
            else
                let run = Run(segment.Text)
                run.Classes.Add(LogLine.ToneClass segment.Tone)
                if segment.Hit then run.Classes.Add "hit"
                inlines.Add run

        inlines

    override _.StyleKeyOverride = typeof<TextBlock>

    override this.OnDataContextChanged(args: EventArgs) =
        base.OnDataContextChanged args

        this.Inlines <-
            match this.DataContext with
            | :? EntryVm as entry -> LogLine.BuildInlines entry.Segments
            | _ -> InlineCollection()
