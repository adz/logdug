namespace LogDug.UI

open System
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Input
open LogDug

/// A selectable TextBlock that renders its entry's coloured segments as inlines.
/// It reads the segments from its DataContext rather than a binding, because an F# type cannot expose the
/// public static property field that XAML bindings need. Each run gets a `tone-*` class (and `hit` for search
/// matches), so colours come from theme styles and follow light/dark switches.
type LogLine() =
    inherit SelectableTextBlock()

    /// What was selected when the context menu opened, since opening it can disturb the selection.
    let mutable selected = ""

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

    member private this.Vm =
        match TopLevel.GetTopLevel this with
        | null -> None
        | top ->
            match top.DataContext with
            | :? MainVm as vm -> Some vm
            | _ -> None

    /// Right-click menu: copy the selection, or add it to the search as a term to include or exclude.
    member private this.BuildMenu() =
        let item (header: string) (action: MainVm -> string -> unit) =
            let menuItem = MenuItem(Header = header)
            menuItem.Click.Add(fun _ -> this.Vm |> Option.iter (fun vm -> if selected <> "" then action vm selected))
            menuItem

        let copy = MenuItem(Header = "Copy", InputGesture = KeyGesture(Key.C, KeyModifiers.Control))
        copy.Click.Add(fun _ -> this.Copy())
        let include' = item "Include in search" (fun vm text -> vm.IncludeInSearch text)
        let exclude = item "Exclude from search" (fun vm text -> vm.ExcludeFromSearch text)

        let flyout = MenuFlyout()
        flyout.Items.Add copy |> ignore
        flyout.Items.Add(Separator()) |> ignore
        flyout.Items.Add include' |> ignore
        flyout.Items.Add exclude |> ignore

        flyout.Opening.Add(fun _ ->
            selected <- (match this.SelectedText with null -> "" | text -> text)
            for entry in [ copy; include'; exclude ] do
                entry.IsEnabled <- selected <> "")

        this.ContextFlyout <- flyout

    override this.OnAttachedToVisualTree(args) =
        base.OnAttachedToVisualTree args

        if isNull this.ContextFlyout then
            this.BuildMenu()
            // Selection is drawn behind the text; the brush follows the theme.
            this.Bind(SelectableTextBlock.SelectionBrushProperty, this.GetResourceObservable "TextSelectionBrush") |> ignore

    /// A selectable text block takes the press for itself, so a plain click selects the row here instead.
    override this.OnTapped(args: TappedEventArgs) =
        base.OnTapped args

        match this.DataContext, this.Vm with
        | (:? EntryVm as entry), Some vm when this.SelectedText = "" || isNull this.SelectedText -> vm.SelectedEntry <- entry
        | _ -> ()

    override this.OnDataContextChanged(args: EventArgs) =
        base.OnDataContextChanged args

        this.Inlines <-
            match this.DataContext with
            | :? EntryVm as entry -> LogLine.BuildInlines entry.Segments
            | _ -> InlineCollection()
