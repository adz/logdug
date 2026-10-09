namespace LogDug.UI

open System
open System.Collections.ObjectModel
open Avalonia
open Elmish.Glue.Core
open LogDug
open LogDug.Files

module Glyph =
    let folder = ""
    let folderOpen = ""
    let archive = ""
    let text = ""
    let json = ""
    let log = ""
    let chevronRight = ""
    let chevronDown = ""
    let spinner = ""

    let ofNode (node: Node) expanded =
        match node.Kind with
        | NodeKind.Folder -> if expanded then folderOpen else folder
        | NodeKind.Archive _ -> archive
        | NodeKind.File ->
            let name = node.Name.ToLowerInvariant()

            if name.EndsWith ".json" || name.EndsWith ".jsonl" || name.EndsWith ".ndjson" then json
            elif name.Contains ".log" || name.EndsWith ".out" || name.EndsWith ".err" || name.EndsWith ".gz" then log
            else text

    let kindClass (node: Node) =
        match node.Kind with
        | NodeKind.Folder -> "kind-folder"
        | NodeKind.Archive _ -> "kind-archive"
        | NodeKind.File -> "kind-file"

module LevelClass =
    let ofLevel level =
        match level with
        | Level.Trace -> "level-trace"
        | Level.Debug -> "level-debug"
        | Level.Info -> "level-info"
        | Level.Warn -> "level-warn"
        | Level.Error -> "level-error"
        | Level.Fatal -> "level-fatal"
        | Level.NoLevel -> "level-none"

/// One visible row of the file tree. Rows are keyed by node location, so expanding a folder
/// inserts rows without recreating the ones around it.
type TreeRowVm(initial: TreeRow, activate: Node -> unit) =
    inherit Bindable()

    let mutable row = initial
    let activateCommand = Command(fun () -> activate row.Node)

    member _.Key = row.Key
    member _.Name = row.Name
    member _.Indent = Thickness(float row.Depth * 16.0 + 8.0, 0.0, 10.0, 0.0)
    member _.Chevron = if row.IsContainer then (if row.IsExpanded then Glyph.chevronDown else Glyph.chevronRight) else ""
    member _.Icon = Glyph.ofNode row.Node row.IsExpanded
    member _.KindClass = Glyph.kindClass row.Node
    member _.IsFolder = row.Node.Kind = NodeKind.Folder
    member _.IsArchive = row.IsContainer && row.Node.Kind <> NodeKind.Folder
    member _.IsFile = not row.IsContainer
    member _.Detail = row.Detail
    member _.IsSelected = row.IsSelected
    member _.IsLoading = row.IsLoading
    member _.Tip = row.Error |> Option.defaultValue row.Key
    member _.ActivateCommand = activateCommand
    member _.RevealLabel = Desktop.revealLabel
    member _.OpenInCodeCommand = Command(fun () -> Desktop.openInVsCode (Desktop.diskPathOf row.Node))
    member _.RevealCommand = Command(fun () -> Desktop.reveal row.Node)
    member _.CopyPathCommand = Command(fun () -> Desktop.copy (Desktop.copyablePath row.Node))

    member this.Update(next: TreeRow) =
        if next <> row then
            row <- next

            for name in [ "Name"; "Indent"; "Chevron"; "Icon"; "Detail"; "IsSelected"; "IsLoading"; "Tip" ] do
                this.NotifyPropertyChanged name

/// Whether an entry opens a foldable region, and if so whether it is folded.
type FoldState =
    | NotFoldable
    | FoldOpen
    | FoldCollapsed

/// One log entry as displayed. Immutable: a new set is built when the file, filter, zone, or search changes,
/// and segments are only computed for rows the list actually realises.
[<AllowNullLiteral>]
type EntryVm(entry: LogEntry, time: TimeContext, pattern: SearchPattern option, showTime: bool, showLevel: bool, fold: FoldState, hasFolds: bool, toggleFold: int -> unit) =
    let segments =
        lazy (
            let body = Render.body entry |> Render.highlight pattern

            // A folded region is marked on its opening line.
            Array.ofList (if fold = FoldCollapsed then body @ [ Segment.make Muted "  …" ] else body)
        )

    let toggleCommand = Command(fun () -> toggleFold entry.Index)

    member _.Entry = entry
    member _.Index = entry.Index
    member _.LineText = string entry.Line
    member _.TimeText = entry.Timestamp |> Option.map (Time.format time) |> Option.defaultValue ""
    member _.LevelText = Level.label entry.Level
    member _.LevelClass = LevelClass.ofLevel entry.Level
    member _.IsTrace = entry.Level = Level.Trace
    member _.IsDebug = entry.Level = Level.Debug
    member _.IsInfo = entry.Level = Level.Info
    member _.IsWarn = entry.Level = Level.Warn
    member _.IsError = entry.Level = Level.Error
    member _.IsFatal = entry.Level = Level.Fatal
    member _.IsUnlevelled = entry.Level = Level.NoLevel
    member _.HasLevel = entry.Level <> Level.NoLevel
    /// Document-wide: a file with no timestamps (or levels) gets no gap where the column would be.
    member _.HasFolds = hasFolds
    member _.IsFoldable = fold <> NotFoldable
    member _.FoldGlyph = if fold = FoldCollapsed then Glyph.chevronRight else Glyph.chevronDown
    member _.ToggleFoldCommand = toggleCommand
    member _.ShowTime = showTime
    member _.ShowLevel = showLevel
    member _.Segments = segments.Value
    member _.IsHighlighted = (Render.countMatches pattern entry) > 0

type ResultRowVm(initial: ResultRow, openHit: HitCursor -> unit) =
    inherit Bindable()

    let mutable row = initial

    let cursor () =
        match row with
        | FileHeader(order, _, _) -> { Order = order; Hit = 0 }
        | HitLine(order, hitIndex, _, _, _, _, _) -> { Order = order; Hit = hitIndex }

    let openCommand = Command(fun () -> openHit (cursor ()))

    static member KeyOf(row: ResultRow) =
        match row with
        | FileHeader(order, _, _) -> $"f{order}"
        | HitLine(order, hitIndex, _, _, _, _, _) -> $"h{order}:{hitIndex}"

    member _.Key = ResultRowVm.KeyOf row
    member _.IsHeader = match row with FileHeader _ -> true | HitLine _ -> false
    member _.IsHit = match row with FileHeader _ -> false | HitLine _ -> true
    member _.Path = match row with FileHeader(_, path, _) -> path | HitLine _ -> ""
    member _.FileName = match row with FileHeader(_, path, _) -> Location.fileName (path.Replace(" › ", "/")) | HitLine _ -> ""
    member _.Count = match row with FileHeader(_, _, count) -> count | HitLine _ -> ""
    member _.LineText = match row with HitLine(_, _, line, _, _, _, _) -> string line | FileHeader _ -> ""
    member _.Before = match row with HitLine(_, _, _, before, _, _, _) -> before | FileHeader _ -> ""
    member _.Matched = match row with HitLine(_, _, _, _, matched, _, _) -> matched | FileHeader _ -> ""
    member _.After = match row with HitLine(_, _, _, _, _, after, _) -> after | FileHeader _ -> ""
    member _.IsActive = match row with HitLine(_, _, _, _, _, _, active) -> active | FileHeader _ -> false
    member _.OpenCommand = openCommand

    member this.Update(next: ResultRow) =
        if next <> row then
            row <- next

            for name in [ "Path"; "FileName"; "Count"; "LineText"; "Before"; "Matched"; "After"; "IsActive" ] do
                this.NotifyPropertyChanged name

type LevelChipVm(initial: LevelChip, toggle: Level -> unit) =
    inherit Bindable()

    let mutable chip = initial
    let toggleCommand = Command(fun () -> toggle chip.Level)

    member _.Level = chip.Level
    member _.Label = Level.name chip.Level
    member _.CountText = chip.Count.ToString("N0")
    member _.IsOn = chip.IsOn
    member _.LevelClass = LevelClass.ofLevel chip.Level
    member _.IsTrace = chip.Level = Level.Trace
    member _.IsDebug = chip.Level = Level.Debug
    member _.IsInfo = chip.Level = Level.Info
    member _.IsWarn = chip.Level = Level.Warn
    member _.IsError = chip.Level = Level.Error
    member _.IsFatal = chip.Level = Level.Fatal
    member _.IsUnlevelled = chip.Level = Level.NoLevel
    member _.ToggleCommand = toggleCommand

    member this.Update(next: LevelChip) =
        if next <> chip then
            chip <- next
            this.NotifyPropertyChanged "CountText"
            this.NotifyPropertyChanged "IsOn"

/// One open file in the tab strip.
type TabVm(initial: Node, isActive: bool, activate: Node -> unit, close: string -> unit) =
    inherit Bindable()

    let mutable active = isActive
    let key = Node.key initial

    member _.Key = key
    member _.Name = initial.Name
    member _.Tip = key
    member _.IsActive = active
    member _.ActivateCommand = Command(fun () -> activate initial)
    member _.CloseCommand = Command(fun () -> close key)
    member _.RevealLabel = Desktop.revealLabel
    member _.OpenInCodeCommand = Command(fun () -> Desktop.openInVsCode (Desktop.diskPathOf initial))
    member _.RevealCommand = Command(fun () -> Desktop.reveal initial)
    member _.CopyPathCommand = Command(fun () -> Desktop.copy (Desktop.copyablePath initial))

    member this.SetActive(value: bool) =
        if value <> active then
            active <- value
            this.NotifyPropertyChanged "IsActive"

/// One row of the Ctrl+P list.
type QuickOpenItemVm(entry: QuickOpenEntry, isSelected: bool) =
    member _.Name = entry.Node.Name
    member _.Directory = if entry.Display.Length > entry.Node.Name.Length then entry.Display.Substring(0, entry.Display.Length - entry.Node.Name.Length).TrimEnd('/', ' ', '›') else ""
    member _.Icon = Glyph.ofNode entry.Node false
    member _.IsSelected = isSelected

/// The window's viewmodel. Elmish owns the state; `Update` copies each new model into bindable properties,
/// and setters for editable controls dispatch messages instead of changing state.
type MainVm(localZone: TimeZoneInfo) =
    inherit Bindable()

    let mutable dispatch: Msg -> unit = ignore
    let send msg = dispatch msg

    let treeRows = ObservableCollection<TreeRowVm>()
    let results = ObservableCollection<ResultRowVm>()
    let levelChips = ObservableCollection<LevelChipVm>()
    let tabs = ObservableCollection<TabVm>()
    let quickOpenItems = ObservableCollection<QuickOpenItemVm>()
    let revealRequested = Event<int * bool>()

    let mutable entries: EntryVm array = [||]
    let mutable shownDocument: obj = null
    let mutable shownLevels: Set<Level> = Set.empty
    let mutable shownTime: TimeContext option = None
    let mutable shownPattern: obj = null
    let mutable shownMode = HighlightMatches
    let mutable shownCollapsed: obj = null
    let mutable shownFind: obj = null
    let mutable findText = ""
    let mutable findCaption = ""
    let mutable isFindInvalid = false
    let mutable isFindCase = false
    let mutable isFindRegex = false
    let mutable fileMode = HighlightMatches
    let mutable activeTabKey = ""
    let mutable quickOpenVisible = false
    let mutable quickOpenQuery = ""
    let mutable shownQuickOpen: obj = null
    let mutable keepSelectionInView = false
    let mutable selectedEntry: EntryVm = null
    let mutable lastReveal: Reveal option = None
    let mutable zones: ZoneOption array = [||]

    let mutable searchText = ""
    let mutable isRegex = false
    let mutable isMatchCase = false
    let mutable searchStatus = ""
    let mutable cursorText = ""
    let mutable hasSearch = false
    let mutable isSearching = false
    let mutable isSearchInvalid = false
    let mutable timeCaption = ""
    let mutable isUtc = false
    let mutable isLocal = false
    let mutable isZone = false
    let mutable selectedZone: ZoneOption option = None
    let mutable viewerTitle = ""
    let mutable viewerSummary = ""
    let mutable hasFile = false
    let mutable isOpening = false
    let mutable showEmpty = true
    let mutable showEntries = false
    let mutable viewerMessage = ""
    let mutable detailHeader = ""
    let mutable detailTimes = ""
    let mutable detailText = ""
    let mutable hasDetail = false
    let mutable isDark = true
    let mutable rootPath = ""
    let mutable rootName = ""
    let mutable entryCountText = ""
    let mutable allLevelsShown = true
    let mutable isFollowing = false

    let nextHit = Command(fun () -> send NextHit)
    let previousHit = Command(fun () -> send PreviousHit)
    let clearSearch = Command(fun () -> send ClearSearch)
    let toggleRegex = Command(fun () -> send ToggleRegex)
    let toggleMatchCase = Command(fun () -> send ToggleMatchCase)
    let showUtc = Command(fun () -> send (SetTimeDisplay Utc))
    let showLocal = Command(fun () -> send (SetTimeDisplay Local))
    let mutable zoneId = "UTC"
    let showZone = Command(fun () -> send (SetTimeDisplay(Zone zoneId)))
    let toggleTheme = Command(fun () -> send ToggleTheme)
    let reload = Command(fun () -> send Reload)
    let showAllLevels = Command(fun () -> send ShowAllLevels)
    let closeDetail = Command(fun () -> send (SelectEntry None))
    let toggleFollow = Command(fun () -> send ToggleFollow)
    let collapseAll = Command(fun () -> send CollapseAll)
    let expandAll = Command(fun () -> send ExpandAll)
    let mutable hasFolds = false
    let mutable isCsv = false
    let mutable csvHasHeader = false
    let toggleCsvHeader = Command(fun () -> send ToggleCsvHeader)
    let findNext = Command(fun () -> send FindNext)
    let findPrevious = Command(fun () -> send FindPrevious)
    let closeFind = Command(fun () -> send CloseFind)
    let toggleFindCase = Command(fun () -> send ToggleFindCase)
    let toggleFindRegex = Command(fun () -> send ToggleFindRegex)
    let closeTab = Command(fun () -> if activeTabKey <> "" then send (CloseTab activeTabKey))
    let showQuickOpen = Command(fun () -> send ShowQuickOpen)
    let hideQuickOpen = Command(fun () -> send HideQuickOpen)
    let quickOpenUp = Command(fun () -> send (QuickOpenMove -1))
    let quickOpenDown = Command(fun () -> send (QuickOpenMove 1))
    let acceptQuickOpen = Command(fun () -> send (QuickOpenAccept None))
    let highlightMode = Command(fun () -> send (SetFileSearchMode HighlightMatches))
    let filterMode = Command(fun () -> send (SetFileSearchMode FilterToMatches))
    let ignoreMode = Command(fun () -> send (SetFileSearchMode IgnoreSearch))

    member _.TreeRows = treeRows
    member _.Results = results
    member _.LevelChips = levelChips
    member _.Zones = zones
    member _.Entries = entries
    member _.Tabs = tabs
    member _.QuickOpenItems = quickOpenItems

    [<CLIEvent>]
    member _.RevealRequested = revealRequested.Publish

    member _.SelectedEntry
        with get () = selectedEntry
        and set (value: EntryVm) =
            if not (obj.ReferenceEquals(value, selectedEntry)) then
                selectedEntry <- value
                send (SelectEntry(if isNull value then None else Some value.Index))

    member this.SearchText
        with get () = searchText
        and set (value: string) =
            let value = if isNull value then "" else value

            if value <> searchText then
                let hadText = searchText <> ""
                searchText <- value
                if hadText <> (value <> "") then this.NotifyPropertyChanged "HasSearchText"
                send (SearchTextChanged value)

    member _.SelectedZone
        with get () = match selectedZone with Some zone -> zone | None -> Unchecked.defaultof<ZoneOption>
        and set (value: ZoneOption) =
            match box value with
            | null -> ()
            | _ when Some value = selectedZone -> ()
            | _ ->
                selectedZone <- Some value
                send (SetZone value.Id)

    member this.QuickOpenQuery
        with get () = quickOpenQuery
        and set (value: string) =
            let value = if isNull value then "" else value

            if value <> quickOpenQuery then
                quickOpenQuery <- value
                send (QuickOpenQueryChanged value)

    member this.FindText
        with get () = findText
        and set (value: string) =
            let value = if isNull value then "" else value

            if value <> findText then
                findText <- value
                send (FindTextChanged value)

    member _.FindCaption = findCaption
    member _.IsFindInvalid = isFindInvalid
    member _.IsFindCase = isFindCase
    member _.IsFindRegex = isFindRegex
    member _.FindNextCommand = findNext
    member _.FindPreviousCommand = findPrevious
    member _.CloseFindCommand = closeFind
    member _.ToggleFindCaseCommand = toggleFindCase
    member _.ToggleFindRegexCommand = toggleFindRegex
    member _.IsCsv = isCsv
    member _.CsvHasHeader = csvHasHeader
    member _.ToggleCsvHeaderCommand = toggleCsvHeader
    member _.HasFolds = hasFolds
    member _.CollapseAllCommand = collapseAll
    member _.ExpandAllCommand = expandAll
    member _.HasSearchText = searchText <> ""
    member _.QuickOpenVisible = quickOpenVisible
    member _.QuickOpenSelectedIndex = quickOpenItems |> Seq.tryFindIndex _.IsSelected |> Option.defaultValue -1
    member _.IsHighlightMode = fileMode = HighlightMatches
    member _.IsFilterMode = fileMode = FilterToMatches
    member _.IsIgnoreMode = fileMode = IgnoreSearch
    member _.HighlightModeCommand = highlightMode
    member _.FilterModeCommand = filterMode
    member _.IgnoreModeCommand = ignoreMode
    member _.CloseTabCommand = closeTab
    member _.ShowQuickOpenCommand = showQuickOpen
    member _.HideQuickOpenCommand = hideQuickOpen
    member _.QuickOpenUpCommand = quickOpenUp
    member _.QuickOpenDownCommand = quickOpenDown
    member _.AcceptQuickOpenCommand = acceptQuickOpen
    member _.AcceptQuickOpenAt(index: int) = send (QuickOpenAccept(Some index))
    member _.ChangeRoot(path: string) = send (ChangeRoot path)
    member _.IncludeInSearch(text: string) = send (IncludeInSearch text)
    member _.ExcludeFromSearch(text: string) = send (ExcludeFromSearch text)
    member _.IsRegex = isRegex
    member _.IsMatchCase = isMatchCase
    member _.SearchStatus = searchStatus
    member _.CursorText = cursorText
    member _.HasSearch = hasSearch
    member _.IsSearching = isSearching
    member _.IsSearchInvalid = isSearchInvalid
    member _.TimeCaption = timeCaption
    member _.IsUtc = isUtc
    member _.IsLocal = isLocal
    member _.IsZone = isZone
    member _.ViewerTitle = viewerTitle
    member _.ViewerSummary = viewerSummary
    member _.HasFile = hasFile
    member _.IsOpening = isOpening
    member _.ShowEmpty = showEmpty
    member _.ShowEntries = showEntries
    member _.ViewerMessage = viewerMessage
    member _.HasViewerMessage = viewerMessage <> ""
    member _.DetailHeader = detailHeader
    member _.DetailTimes = detailTimes
    member _.DetailText = detailText
    member _.HasDetail = hasDetail
    member _.IsDark = isDark
    member _.RootPath = rootPath
    member _.RootName = rootName
    member _.EntryCountText = entryCountText
    member _.AllLevelsShown = allLevelsShown
    member _.IsFollowing = isFollowing
    member _.ToggleFollowCommand = toggleFollow

    member _.NextHitCommand = nextHit
    member _.PreviousHitCommand = previousHit
    member _.ClearSearchCommand = clearSearch
    member _.ToggleRegexCommand = toggleRegex
    member _.ToggleMatchCaseCommand = toggleMatchCase
    member _.ShowUtcCommand = showUtc
    member _.ShowLocalCommand = showLocal
    member _.ShowZoneCommand = showZone
    member _.ToggleThemeCommand = toggleTheme
    member _.ReloadCommand = reload
    member _.ShowAllLevelsCommand = showAllLevels
    member _.CloseDetailCommand = closeDetail

    member private this.UpdateEntries(model: Model) =
        match model.Viewer with
        | Showing file ->
            let mode = App.fileMode model
            let collapsed = App.collapsedIn model
            let collapsedSet = box collapsed

            let findPattern = App.findOf model |> Option.bind _.Pattern
            let findPatternObj = findPattern |> Option.map box |> Option.toObj
            let pattern = model.Search.Pattern |> Option.map box |> Option.toObj

            // Reference checks: the document holds every entry, so structural equality would walk them all.
            let changed =
                not (obj.ReferenceEquals(file.Document, shownDocument))
                || model.Levels <> shownLevels
                || Some model.Time <> shownTime
                || not (obj.ReferenceEquals(pattern, shownPattern))
                || mode <> shownMode
                || not (obj.ReferenceEquals(collapsedSet, shownCollapsed))
                || not (obj.ReferenceEquals(findPatternObj, shownFind))

            if changed then
                let displayChanged =
                    obj.ReferenceEquals(file.Document, shownDocument)
                    && (model.Levels <> shownLevels || Some model.Time <> shownTime)

                keepSelectionInView <- displayChanged
                shownDocument <- file.Document
                shownLevels <- model.Levels
                shownTime <- Some model.Time
                shownPattern <- pattern
                shownMode <- mode
                shownCollapsed <- collapsedSet
                shownFind <- findPatternObj

                // Ignoring the search leaves the file as if nothing were searched for; filtering narrows it to the matches.
                // The file's own find takes over highlighting while it has text; otherwise the main search shows.
                let highlight =
                    match findPattern with
                    | Some pattern -> Some pattern
                    | None -> if mode = IgnoreSearch then None else model.Search.Pattern
                let filter = if mode = FilterToMatches then model.Search.Pattern else None

                let hasDocumentFolds = not file.Document.Folds.IsEmpty
                let showTime = LogDocument.hasTimestamps file.Document
                let showLevel = LogDocument.hasLevels file.Document

                entries <-
                    Shape.visibleEntries model.Levels filter collapsed file.Document
                    |> Array.map (fun entry ->
                        let fold =
                            if file.Document.Folds.ContainsKey entry.Index then
                                (if collapsed.Contains entry.Index then FoldCollapsed else FoldOpen)
                            else
                                NotFoldable

                        EntryVm(entry, model.Time, highlight, showTime, showLevel, fold, hasDocumentFolds, ToggleFold >> send))

                selectedEntry <- null
                this.NotifyPropertyChanged "Entries"
        | _ ->
            if entries.Length > 0 || not (isNull shownDocument) then
                shownDocument <- null
                entries <- [||]
                selectedEntry <- null
                this.NotifyPropertyChanged "Entries"

        let wanted =
            model.SelectedEntry
            |> Option.bind (fun index -> entries |> Array.tryFind (fun row -> row.Index = index))
            |> Option.toObj

        if not (obj.ReferenceEquals(wanted, selectedEntry)) then
            selectedEntry <- wanted
            this.NotifyPropertyChanged "SelectedEntry"

            if keepSelectionInView && not (isNull wanted) then
                revealRequested.Trigger(Array.IndexOf(entries, wanted), true)

        keepSelectionInView <- false

    member private this.UpdateDetail(model: Model) =
        let entry =
            match model.Viewer, model.SelectedEntry with
            | Showing file, Some index when index < file.Document.Entries.Length -> Some file.Document.Entries[index]
            | _ -> None

        match entry with
        | Some entry ->
            let utc = Time.context localZone model.Now Utc
            let local = Time.context localZone model.Now Local

            let times =
                match entry.Timestamp with
                | Some timestamp ->
                    let zoneText =
                        match model.Time.Display with
                        | Zone _ -> $"   ·   {model.Time.Caption}  {Time.format model.Time timestamp}"
                        | _ -> ""

                    $"UTC  {Time.format utc timestamp}   ·   Local  {Time.format local timestamp}{zoneText}"
                | None -> "No timestamp on this entry"

            let text =
                LogParser.prettyJson entry
                |> Option.defaultValue (String.Join(Environment.NewLine, entry.Lines))

            this.Change(&detailHeader, $"Line {entry.Line} · {Level.name entry.Level}", "DetailHeader")
            this.Change(&detailTimes, times, "DetailTimes")
            this.Change(&detailText, text, "DetailText")
            this.Change(&hasDetail, true, "HasDetail")
        | None -> this.Change(&hasDetail, false, "HasDetail")

    member private this.UpdateTabsAndQuickOpen(model: Model) =
        activeTabKey <- App.viewerNode model |> Option.map Node.key |> Option.defaultValue ""
        let mode = App.fileMode model

        if mode <> fileMode then
            fileMode <- mode
            for name in [ "IsHighlightMode"; "IsFilterMode"; "IsIgnoreMode" ] do
                this.NotifyPropertyChanged name

        tabs.SyncWith(
            model.Tabs |> Array.ofList,
            Node.key,
            (fun (vm: TabVm) -> vm.Key),
            (fun node -> TabVm(node, (Node.key node = activeTabKey), ActivateNode >> send, CloseTab >> send)),
            (fun (vm: TabVm) node -> vm.SetActive(Node.key node = activeTabKey))
        )

        let isOpen = model.QuickOpen.IsSome
        this.Change(&quickOpenVisible, isOpen, "QuickOpenVisible")

        match model.QuickOpen with
        | Some state ->
            if state.Query <> quickOpenQuery then
                quickOpenQuery <- state.Query
                this.NotifyPropertyChanged "QuickOpenQuery"

            // Rebuilt only when the ranking or the highlighted row changes.
            let signature = box (state.Results, state.Selected)

            if not (obj.Equals(signature, shownQuickOpen)) then
                shownQuickOpen <- signature
                quickOpenItems.Clear()
                state.Results |> List.iteri (fun index entry -> quickOpenItems.Add(QuickOpenItemVm(entry, (index = state.Selected))))
        | None ->
            if not (isNull shownQuickOpen) then
                shownQuickOpen <- null
                quickOpenItems.Clear()

    member this.Update(model: Model) =
        this.Change(&rootPath, model.RootPath, "RootPath")
        this.Change(&rootName, model.Tree.Root.Name, "RootName")
        this.Change(&isDark, model.Settings.DarkTheme, "IsDark")
        this.Change(&isFollowing, model.Following, "IsFollowing")

        let find = App.findOf model
        let text = find |> Option.map _.Text |> Option.defaultValue ""

        if text <> findText then
            findText <- text
            this.NotifyPropertyChanged "FindText"

        let caption =
            match find with
            | Some state when state.Error.IsSome -> "Invalid"
            | Some state when state.Pattern.IsNone -> ""
            | Some state when state.Matches.Length = 0 -> "No matches"
            | Some state ->
                let position = match state.Cursor with Some position -> string (position + 1) | None -> "–"
                $"{position} / {state.Matches.Length:N0}"
            | None -> ""

        this.Change(&findCaption, caption, "FindCaption")
        this.Change(&isFindInvalid, (find |> Option.exists (fun state -> state.Error.IsSome || (state.Pattern.IsSome && state.Matches.Length = 0))), "IsFindInvalid")
        this.Change(&isFindCase, model.FindMatchCase, "IsFindCase")
        this.Change(&isFindRegex, model.FindRegex, "IsFindRegex")
        let csv = match model.Viewer with Showing { Document = { Kind = Delimited hasHeader } } -> Some hasHeader | _ -> None
        this.Change(&isCsv, csv.IsSome, "IsCsv")
        this.Change(&csvHasHeader, (csv = Some true), "CsvHasHeader")
        this.Change(&hasFolds, (match model.Viewer with Showing file -> not file.Document.Folds.IsEmpty | _ -> false), "HasFolds")
        this.UpdateTabsAndQuickOpen model

        treeRows.SyncWith(
            Shape.treeRows model |> Array.ofList,
            (fun row -> row.Key),
            (fun vm -> vm.Key),
            (fun row -> TreeRowVm(row, ActivateNode >> send)),
            (fun vm row -> vm.Update row)
        )

        // Search
        let search = model.Search
        this.Change(&isRegex, (search.Query.Mode = RegexSearch), "IsRegex")
        this.Change(&isMatchCase, search.Query.MatchCase, "IsMatchCase")

        if search.Query.Text <> searchText then
            searchText <- search.Query.Text
            this.NotifyPropertyChanged "SearchText"
            this.NotifyPropertyChanged "HasSearchText"

        this.Change(&searchStatus, Shape.searchStatus model, "SearchStatus")
        this.Change(&cursorText, Shape.cursorText model, "CursorText")
        this.Change(&hasSearch, (search.Status <> Idle), "HasSearch")
        this.Change(&isSearching, (match search.Status with Running _ -> true | _ -> false), "IsSearching")
        this.Change(&isSearchInvalid, (match search.Status with Invalid _ -> true | _ -> false), "IsSearchInvalid")

        results.SyncWith(
            Shape.resultRows model |> Array.ofList,
            (fun row -> ResultRowVm.KeyOf row),
            (fun vm -> vm.Key),
            (fun row -> ResultRowVm(row, OpenHit >> send)),
            (fun vm row -> vm.Update row)
        )

        // Time
        if zones.Length = 0 then
            zones <- Array.ofList model.Zones
            this.NotifyPropertyChanged "Zones"

        zoneId <- model.Settings.Zone
        this.Change(&timeCaption, model.Time.Caption, "TimeCaption")
        this.Change(&isUtc, (model.Time.Display = Utc), "IsUtc")
        this.Change(&isLocal, (model.Time.Display = Local), "IsLocal")
        this.Change(&isZone, (match model.Time.Display with Zone _ -> true | _ -> false), "IsZone")

        let zone = zones |> Array.tryFind (fun zone -> zone.Id = model.Settings.Zone)

        if zone <> selectedZone then
            selectedZone <- zone
            this.NotifyPropertyChanged "SelectedZone"

        // Viewer
        this.Change(&viewerTitle, Shape.viewerTitle model, "ViewerTitle")
        this.Change(&viewerSummary, Shape.viewerSummary model, "ViewerSummary")
        this.Change(&hasFile, (model.Viewer <> NothingOpen), "HasFile")
        this.Change(&showEmpty, (model.Viewer = NothingOpen), "ShowEmpty")
        this.Change(&isOpening, (match model.Viewer with Opening _ -> true | _ -> false), "IsOpening")

        let message =
            match model.Viewer with
            | Unreadable(_, message) -> message
            | Showing { Document = { Kind = Binary } } -> "This looks like a binary file, so there is nothing to show as text."
            | Showing { Document = document } when document.Entries.Length = 0 -> "This file is empty."
            | _ -> ""

        this.Change(&viewerMessage, message, "ViewerMessage")
        this.NotifyPropertyChanged "HasViewerMessage"
        this.Change(&showEntries, (match model.Viewer with Showing _ -> message = "" | _ -> false), "ShowEntries")

        levelChips.SyncWith(
            Shape.levelChips model |> Array.ofList,
            (fun chip -> LevelClass.ofLevel chip.Level),
            (fun vm -> vm.LevelClass),
            (fun chip -> LevelChipVm(chip, ToggleLevel >> send)),
            (fun vm chip -> vm.Update chip)
        )

        this.Change(&allLevelsShown, (model.Levels.Count = Level.all.Length), "AllLevelsShown")
        this.UpdateEntries model

        let total =
            match model.Viewer with
            | Showing file -> file.Document.Entries.Length
            | _ -> 0

        let countText = if entries.Length = total then $"{total:N0} entries" else $"{entries.Length:N0} of {total:N0} entries"
        this.Change(&entryCountText, countText, "EntryCountText")
        this.UpdateDetail model

        if model.Reveal <> lastReveal then
            lastReveal <- model.Reveal

            match model.Reveal with
            | Some reveal ->
                match entries |> Array.tryFindIndex (fun row -> row.Index = reveal.EntryIndex) with
                | Some rowIndex -> revealRequested.Trigger(rowIndex, reveal.Select)
                | None -> ()
            | None -> ()

    interface IProjection<Model> with
        member this.Update model = this.Update model

    interface IDispatchTarget<Msg> with
        member _.SetDispatch target = dispatch <- target.Invoke
