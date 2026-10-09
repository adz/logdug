namespace LogDug

open System
open System.IO
open Axial
open Elmish
open LogDug.Files

type TreeState =
    { Root: Node
      /// Loaded children by container key. A key that is absent has not been loaded yet.
      Children: Map<string, Node list>
      Expanded: Set<string>
      Loading: Set<string>
      Failed: Map<string, string> }

type OpenFile = { Node: Node; Document: LogDocument }

type ViewerState =
    | NothingOpen
    | Opening of Node
    | Showing of OpenFile
    | Unreadable of Node * message: string

type SearchStatus =
    | Idle
    | Invalid of message: string
    | Running of SearchSummary
    | Finished of SearchSummary

/// A search result position: the file's walk order (stable while results stream in) and the hit within it.
type HitCursor = { Order: int; Hit: int }

type SearchState =
    { Query: SearchQuery
      /// The request whose events are current; events for older requests are ignored.
      RequestId: int
      /// The pattern of the newest request, shown once its first results arrive.
      Pending: SearchPattern option
      /// The pattern of the search whose results are shown; also drives in-file highlighting.
      Pattern: SearchPattern option
      Status: SearchStatus
      /// Files with hits, in tree order.
      Results: FileHits list
      Cursor: HitCursor option }

/// A request for the viewer to scroll to an entry, and optionally select it. The nonce makes repeats distinct.
type Reveal =
    { EntryIndex: int
      Select: bool
      Nonce: int }

/// Where to scroll once the file that is opening has loaded.
type RevealTarget =
    | AtLine of int
    | AtEnd

/// What the main search does to the file being viewed: colour the matches, show only the matching entries,
/// or leave the file alone.
type FileSearchMode =
    | HighlightMatches
    | FilterToMatches
    | IgnoreSearch

/// The Ctrl+P box: what was typed, the ranked files, and which one Enter would open.
type QuickOpenState =
    { Query: string
      Selected: int
      Results: QuickOpenEntry list }

type Model =
    { RootPath: string
      Tree: TreeState
      SelectedNode: string option
      /// Files opened so far, in the order they were opened. The viewer shows one of them.
      Tabs: Node list
      /// Per-file choice of how the main search applies to it; files not listed highlight.
      FileModes: Map<string, FileSearchMode>
      /// Per-file entries whose foldable region is currently collapsed.
      Collapsed: Map<string, Set<int>>
      /// Per-file choice of whether a CSV's first row is column names; files not listed are guessed.
      CsvHeaders: Map<string, bool>
      QuickOpen: QuickOpenState option
      /// Every file under the root, loaded the first time quick open is used.
      FileIndex: QuickOpenEntry array option
      IndexLoading: bool
      /// A file named on the command line, opened as soon as its folder has been read.
      OpenOnLoad: string option
      Viewer: ViewerState
      Search: SearchState
      Time: TimeContext
      Zones: ZoneOption list
      Settings: Settings
      Levels: Set<Level>
      SelectedEntry: int option
      Reveal: Reveal option
      PendingReveal: RevealTarget option
      /// Reload the open file when it changes on disk and keep the newest entries in view.
      Following: bool
      Now: DateTimeOffset }

type Msg =
    | SettingsLoaded of Settings
    | ToggleNode of key: string
    | ChildrenLoaded of key: string * Result<Node list, string>
    | ActivateNode of Node
    | FileOpened of key: string * Result<LogDocument, string>
    | Reload
    | ToggleFollow
    | FileChanged of key: string
    | SelectEntry of int option
    | ToggleLevel of Level
    | ShowAllLevels
    | SearchTextChanged of string
    | ToggleRegex
    | ToggleMatchCase
    | SearchEventReceived of SearchEvent
    | OpenHit of HitCursor
    | NextHit
    | PreviousHit
    | ClearSearch
    | SetTimeDisplay of TimeDisplay
    | SetZone of zoneId: string
    | ToggleTheme
    | CloseTab of key: string
    | SetFileSearchMode of FileSearchMode
    | ChangeRoot of path: string
    | IncludeInSearch of text: string
    | ExcludeFromSearch of text: string
    | ShowQuickOpen
    | HideQuickOpen
    | QuickOpenQueryChanged of string
    | QuickOpenMove of delta: int
    | QuickOpenAccept of index: int option
    | FileIndexLoaded of root: string * Result<Node list, string>
    | OpenPath of path: string
    | ToggleFold of entryIndex: int
    | CollapseAll
    | ExpandAll
    | ToggleCsvHeader

module App =
    let private followThrottle = TimeSpan.FromMilliseconds 300.0

    let private parseOptions model : LogParser.Options = { ReferenceYear = model.Now.Year }

    let private emptySearch =
        { Query = { Text = ""; Mode = PlainSearch; MatchCase = false }
          RequestId = 0
          Pending = None
          Pattern = None
          Status = Idle
          Results = []
          Cursor = None }

    /// The containers between the root (exclusive) and the node (exclusive), outermost first.
    let ancestors (rootPath: string) (location: Location) : Node list =
        let container location name = Node.ofName location name 0L (Location.archiveKind name).IsNone

        let rec go location =
            match location with
            | Disk path ->
                match Path.GetDirectoryName path with
                | null -> []
                | parent when String.Equals(parent, rootPath, StringComparison.OrdinalIgnoreCase) -> []
                | parent when not (parent.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) -> []
                | parent -> go (Disk parent) @ [ container (Disk parent) (Location.fileName parent) ]
            | Entry(archive, entryPath) ->
                let archiveName =
                    match archive with
                    | Disk path -> Location.fileName path
                    | Entry(_, inner) -> Location.fileName inner

                let segments = entryPath.Split('/', StringSplitOptions.RemoveEmptyEntries)

                let folders =
                    [ for depth in 1 .. segments.Length - 1 ->
                          let prefix = String.Join("/", segments, 0, depth)
                          Node.ofName (Entry(archive, prefix)) segments[depth - 1] 0L true ]

                go archive @ [ { (container archive archiveName) with Kind = NodeKind.Archive(defaultArg (Location.archiveKind archiveName) Zip) } ] @ folders

        go location

    let init (env: AppEnv) (rootPath: string) () : Model * Cmd<Msg> =
        let now = env.Clock.UtcNow()

        let root =
            { Location = Disk rootPath
              Name = Location.fileName rootPath
              Kind = NodeKind.Folder
              Size = 0L }

        let rootKey = Node.key root

        let model =
            { RootPath = rootPath
              Tree =
                { Root = root
                  Children = Map.empty
                  Expanded = Set.singleton rootKey
                  Loading = Set.singleton rootKey
                  Failed = Map.empty }
              SelectedNode = None
              Tabs = []
              FileModes = Map.empty
              Collapsed = Map.empty
              CsvHeaders = Map.empty
              QuickOpen = None
              FileIndex = None
              IndexLoading = false
              OpenOnLoad = None
              Viewer = NothingOpen
              Search = emptySearch
              Time = Time.context env.LocalZone now Local
              Zones = Time.zones now
              Settings = Settings.defaults
              Levels = Set.ofList Level.all
              SelectedEntry = None
              Reveal = None
              PendingReveal = None
              Following = false
              Now = now }

        let loadRoot =
            Files.children root
            |> FlowCmd.attempt env.Post env string (fun result -> ChildrenLoaded(rootKey, result))

        let loadSettings =
            Settings.load
            |> FlowCmd.attempt env.Post env (fun (never: Never) -> Never.absurd never) (fun result ->
                SettingsLoaded(Result.defaultValue Settings.defaults result))

        model, Cmd.batch [ loadRoot; loadSettings ]

    let private saveSettings env (settings: Settings) =
        Settings.save settings |> FlowCmd.fireAndForget env

    let private loadChildren env (node: Node) =
        let key = Node.key node

        Files.children node
        |> FlowCmd.attempt env.Post env string (fun result -> ChildrenLoaded(key, result))

    /// Expands every container on the way to `location`, loading any that have not been read yet.
    let private expandTo env model (location: Location) =
        let path = ancestors model.RootPath location

        let toLoad =
            path
            |> List.filter (fun node ->
                let key = Node.key node
                not (model.Tree.Children.ContainsKey key) && not (model.Tree.Loading.Contains key))

        let tree =
            { model.Tree with
                Expanded = path |> List.fold (fun expanded node -> Set.add (Node.key node) expanded) model.Tree.Expanded
                Loading = toLoad |> List.fold (fun loading node -> Set.add (Node.key node) loading) model.Tree.Loading }

        { model with Tree = tree }, toLoad |> List.map (loadChildren env) |> Cmd.batch

    let private openNode env model (node: Node) =
        let key = Node.key node
        let options = parseOptions model

        let work =
            flow {
                let! text = Files.readText node
                return! Flow.fromBlocking (fun _ -> text |> Option.map (LogParser.parseFileWith options (model.CsvHeaders.TryFind key) node.Name) |> Option.defaultValue LogParser.binary)
            }

        { model with
            Tabs = if model.Tabs |> List.exists (fun tab -> Node.key tab = key) then model.Tabs else model.Tabs @ [ node ]
            Viewer = Opening node
            SelectedNode = Some key
            SelectedEntry = None
            PendingReveal = None },
        work |> FlowCmd.attempt env.Post env string (fun result -> FileOpened(key, result))

    let private send env (command: SearchCommand) : Cmd<Msg> =
        Cmd.ofEffect (fun _ -> Queue.tryOffer command env.SearchCommands |> ignore)

    /// Hands the current query to the search pipeline, which debounces it and replaces any running search.
    let private requestSearch env model =
        let search = model.Search
        let requestId = search.RequestId + 1

        let cleared status =
            { search with
                RequestId = requestId
                Pending = None
                Pattern = None
                Status = status
                Results = []
                Cursor = None }

        if String.IsNullOrWhiteSpace search.Query.Text then
            { model with Search = cleared Idle }, send env StopSearch
        else
            match SearchPattern.create search.Query with
            | Error message -> { model with Search = cleared (Invalid message) }, send env StopSearch
            | Ok pattern ->
                { model with
                    Search =
                        { search with
                            RequestId = requestId
                            Pending = Some pattern
                            Status = Running Search.emptySummary
                            Results = []
                            Cursor = None } },
                send env (StartSearch { Id = requestId; Pattern = pattern; Root = model.Tree.Root })

    /// Unfolds any collapsed region that hides `entryIndex`, so a revealed entry can actually be seen.
    let private unfoldAround model (entryIndex: int) =
        match model.Viewer with
        | Showing file ->
            let key = Node.key file.Node

            match model.Collapsed.TryFind key with
            | Some collapsed ->
                let stillHidden header =
                    match file.Document.Folds.TryFind header with
                    | Some last -> entryIndex > header && entryIndex <= last
                    | None -> false

                { model with Collapsed = model.Collapsed.Add(key, collapsed |> Set.filter (stillHidden >> not)) }
            | None -> model
        | _ -> model

    let private reveal model (entryIndex: int) select =
        let entryIndex =
            match model.Viewer with
            | Showing file -> min entryIndex (file.Document.Entries.Length - 1)
            | _ -> entryIndex

        let level =
            match model.Viewer with
            | Showing file when entryIndex >= 0 -> Some file.Document.Entries[entryIndex].Level
            | _ -> None

        let nonce = model.Reveal |> Option.map (fun reveal -> reveal.Nonce + 1) |> Option.defaultValue 1

        if entryIndex < 0 then
            { model with PendingReveal = None }
        else
            { unfoldAround model entryIndex with
                SelectedEntry = if select then Some entryIndex else model.SelectedEntry
                Reveal = Some { EntryIndex = entryIndex; Select = select; Nonce = nonce }
                PendingReveal = None
                // A revealed entry must be visible even when its level is filtered out.
                Levels = match level with Some level -> Set.add level model.Levels | None -> model.Levels }

    let private revealLine model line =
        match model.Viewer with
        | Showing file ->
            match LogDocument.entryAtLine file.Document line with
            | Some entryIndex -> reveal model entryIndex true
            | None -> { model with PendingReveal = None }
        | _ -> model

    let private hitAt model (cursor: HitCursor) =
        model.Search.Results
        |> List.tryFind (fun file -> file.Order = cursor.Order)
        |> Option.bind (fun file -> file.Hits |> List.tryItem cursor.Hit |> Option.map (fun hit -> file, hit))

    let private openHit env model cursor =
        match hitAt model cursor with
        | None -> model, Cmd.none
        | Some(file, hit) ->
            let model = { model with Search = { model.Search with Cursor = Some cursor } }

            match model.Viewer with
            | Showing shown when Node.key shown.Node = Node.key file.Node -> revealLine model hit.Line, Cmd.none
            | _ ->
                let opened, openCmd = openNode env model file.Node
                let expanded, expandCmd = expandTo env { opened with PendingReveal = Some(AtLine hit.Line) } file.Node.Location
                expanded, Cmd.batch [ openCmd; expandCmd ]

    let private allCursors model =
        model.Search.Results
        |> List.collect (fun file -> file.Hits |> List.mapi (fun index _ -> { Order = file.Order; Hit = index }))

    let private stepHit env model direction =
        let cursors = allCursors model

        if cursors.IsEmpty then
            model, Cmd.none
        else
            let current =
                model.Search.Cursor
                |> Option.bind (fun cursor -> cursors |> List.tryFindIndex ((=) cursor))

            let next =
                match current with
                | None -> if direction > 0 then 0 else cursors.Length - 1
                | Some index -> (index + direction + cursors.Length) % cursors.Length

            openHit env model cursors[next]

    let private receive model (event: SearchEvent) =
        let search = model.Search

        let current id =
            id = search.RequestId

        match event with
        | SearchProgress(id, found, summary) when current id ->
            let results =
                if found.IsEmpty then search.Results
                else search.Results @ found |> List.sortBy _.Order

            { model with
                Search =
                    { search with
                        Pattern = search.Pending
                        Results = results
                        Status = Running summary } }
        | SearchEnded(id, summary) when current id ->
            { model with Search = { search with Pattern = search.Pending; Status = Finished summary } }
        | SearchFailed(id, message) when current id ->
            { model with Search = { search with Status = Invalid $"Search stopped: {message}" } }
        | _ -> model

    let private setTimeDisplay env model display =
        let settings = Settings.withTimeDisplay display model.Settings

        { model with
            Time = Time.context env.LocalZone model.Now display
            Settings = settings },
        saveSettings env settings

    let private followedNode model =
        match model.Viewer with
        | Opening node
        | Showing { Node = node } when model.Following -> Some node
        | _ -> None

    /// The file the viewer is on, whether it is still loading, shown, or unreadable.
    let viewerNode model =
        match model.Viewer with
        | Opening node
        | Showing { Node = node }
        | Unreadable(node, _) -> Some node
        | NothingOpen -> None

    let collapsedIn model =
        viewerNode model
        |> Option.bind (fun node -> model.Collapsed.TryFind(Node.key node))
        |> Option.defaultValue Set.empty

    let fileMode model =
        viewerNode model
        |> Option.bind (fun node -> model.FileModes.TryFind(Node.key node))
        |> Option.defaultValue HighlightMatches

    let private loadIndex env model =
        let root = model.RootPath

        Files.walk Search.descend model.Tree.Root
        |> FlowStream.filter Search.isSearchable
        |> FlowStream.take 50_000
        |> FlowStream.runCollect
        |> FlowCmd.attempt env.Post env string (fun result -> FileIndexLoaded(root, result))

    let private quickOpenState model query selected =
        let results =
            match model.FileIndex with
            | Some index -> QuickOpen.matches index (model.Tabs |> List.rev |> List.map Node.key) query
            | None -> []

        { Query = query; Selected = max 0 (min selected (results.Length - 1)); Results = results }

    /// The same model rooted at another folder: a fresh tree and no open files, keeping settings and the search text.
    let private rootedAt env model (path: string) =
        let fresh, loadRoot = init env (env.FileSystem.TrimEndingDirectorySeparator(env.FileSystem.GetFullPath path)) ()

        let rooted =
            { fresh with
                Settings = model.Settings
                Time = model.Time
                Levels = model.Levels
                Search = { emptySearch with Query = model.Search.Query; RequestId = model.Search.RequestId + 1 } }

        rooted, loadRoot

    let rec update (env: AppEnv) (msg: Msg) (model: Model) : Model * Cmd<Msg> =
        match msg with
        | SettingsLoaded settings ->
            let query =
                { model.Search.Query with
                    Mode = if settings.Regex then RegexSearch else PlainSearch
                    MatchCase = settings.MatchCase }

            { model with
                Settings = settings
                Time = Time.context env.LocalZone model.Now (Settings.timeDisplay settings)
                Search = { model.Search with Query = query } },
            Cmd.none

        | ToggleNode key ->
            let tree = model.Tree

            if tree.Expanded.Contains key then
                { model with Tree = { tree with Expanded = tree.Expanded.Remove key } }, Cmd.none
            else
                let node =
                    tree.Children
                    |> Map.toSeq
                    |> Seq.collect snd
                    |> Seq.tryFind (fun node -> Node.key node = key)

                match node with
                | Some node when not (tree.Children.ContainsKey key) ->
                    { model with Tree = { tree with Expanded = tree.Expanded.Add key; Loading = tree.Loading.Add key } },
                    loadChildren env node
                | _ -> { model with Tree = { tree with Expanded = tree.Expanded.Add key } }, Cmd.none

        | ChildrenLoaded(key, result) ->
            let tree = { model.Tree with Loading = model.Tree.Loading.Remove key }

            let tree =
                match result with
                | Ok children -> { tree with Children = tree.Children.Add(key, children); Failed = tree.Failed.Remove key }
                | Error message -> { tree with Expanded = tree.Expanded.Remove key; Failed = tree.Failed.Add(key, message) }

            match model.OpenOnLoad with
            | Some path when key = Node.key model.Tree.Root -> update env (OpenPath path) { model with Tree = tree }
            | _ -> { model with Tree = tree }, Cmd.none

        | OpenPath path ->
            match model.Tree.Children.TryFind(Node.key model.Tree.Root) with
            | None -> { model with OpenOnLoad = Some path }, Cmd.none
            | Some children ->
                let model = { model with OpenOnLoad = None }

                match children |> List.tryFind (fun node -> String.Equals(Node.key node, path, StringComparison.OrdinalIgnoreCase)) with
                | Some node -> update env (ActivateNode node) model
                | None -> model, Cmd.none

        | ActivateNode node when Node.isContainer node -> update env (ToggleNode(Node.key node)) { model with SelectedNode = Some(Node.key node) }

        | ActivateNode node -> openNode env model node

        | FileOpened(key, result) ->
            match model.Viewer with
            | Opening node when Node.key node = key ->
                let model =
                    match result with
                    | Ok document -> { model with Viewer = Showing { Node = node; Document = document } }
                    | Error message -> { model with Viewer = Unreadable(node, message) }

                match model.PendingReveal with
                | Some(AtLine line) -> revealLine model line, Cmd.none
                | Some AtEnd -> reveal model Int32.MaxValue false, Cmd.none
                | None -> model, Cmd.none
            | _ -> model, Cmd.none

        | Reload ->
            match model.Viewer with
            | Showing { Node = node }
            | Unreadable(node, _) -> openNode env model node
            | _ -> model, Cmd.none

        | ToggleFollow ->
            let following = not model.Following

            match model.Viewer with
            | Showing _ when following -> reveal { model with Following = true } Int32.MaxValue false, Cmd.none
            | _ -> { model with Following = following }, Cmd.none

        | FileChanged key ->
            match model.Viewer with
            | Showing { Node = node } when model.Following && Node.key node = key ->
                let opened, cmd = openNode env model node
                { opened with PendingReveal = Some AtEnd }, cmd
            | _ -> model, Cmd.none

        | SelectEntry entry -> { model with SelectedEntry = entry }, Cmd.none

        | ToggleLevel level ->
            let levels = if model.Levels.Contains level then model.Levels.Remove level else model.Levels.Add level
            { model with Levels = levels }, Cmd.none

        | ShowAllLevels -> { model with Levels = Set.ofList Level.all }, Cmd.none

        | SearchTextChanged text ->
            requestSearch env { model with Search = { model.Search with Query = { model.Search.Query with Text = text } } }

        | ToggleRegex ->
            let mode = if model.Search.Query.Mode = RegexSearch then PlainSearch else RegexSearch
            let settings = { model.Settings with Regex = (mode = RegexSearch) }
            let search = { model.Search with Query = { model.Search.Query with Mode = mode } }
            let model, cmd = requestSearch env { model with Settings = settings; Search = search }
            model, Cmd.batch [ cmd; saveSettings env settings ]

        | ToggleMatchCase ->
            let matchCase = not model.Search.Query.MatchCase
            let settings = { model.Settings with MatchCase = matchCase }
            let search = { model.Search with Query = { model.Search.Query with MatchCase = matchCase } }
            let model, cmd = requestSearch env { model with Settings = settings; Search = search }
            model, Cmd.batch [ cmd; saveSettings env settings ]

        | SearchEventReceived event -> receive model event, Cmd.none

        | OpenHit cursor -> openHit env model cursor
        | NextHit -> stepHit env model 1
        | PreviousHit -> stepHit env model -1

        | ClearSearch ->
            { model with
                Search =
                    { emptySearch with
                        Query = { model.Search.Query with Text = "" }
                        RequestId = model.Search.RequestId + 1 } },
            send env StopSearch

        | SetTimeDisplay display -> setTimeDisplay env model display
        | SetZone zoneId -> setTimeDisplay env model (Zone zoneId)

        | ToggleTheme ->
            let settings = { model.Settings with DarkTheme = not model.Settings.DarkTheme }
            { model with Settings = settings }, saveSettings env settings

        | ToggleFold entryIndex ->
            match viewerNode model with
            | Some node ->
                let collapsed = collapsedIn model
                let next = if collapsed.Contains entryIndex then collapsed.Remove entryIndex else collapsed.Add entryIndex
                { model with Collapsed = model.Collapsed.Add(Node.key node, next) }, Cmd.none
            | None -> model, Cmd.none

        | CollapseAll ->
            match model.Viewer with
            | Showing file ->
                // A file that is one big block (a JSON object, an XML root) keeps that block open.
                let headers = file.Document.Folds |> Map.toList
                let total = file.Document.Entries.Length
                let wrapper = headers |> List.tryFind (fun (header, last) -> (last - header + 1) * 10 >= total * 9)
                let all = headers |> List.map fst |> Set.ofList
                let collapsed = match wrapper with Some(header, _) -> all.Remove header | None -> all
                { model with Collapsed = model.Collapsed.Add(Node.key file.Node, collapsed) }, Cmd.none
            | _ -> model, Cmd.none

        | ToggleCsvHeader ->
            match model.Viewer with
            | Showing({ Document = { Kind = Delimited hasHeader } } as file) ->
                openNode env { model with CsvHeaders = model.CsvHeaders.Add(Node.key file.Node, not hasHeader) } file.Node
            | _ -> model, Cmd.none

        | ExpandAll ->
            match viewerNode model with
            | Some node -> { model with Collapsed = model.Collapsed.Add(Node.key node, Set.empty) }, Cmd.none
            | None -> model, Cmd.none

        | CloseTab key ->
            let tabs = model.Tabs |> List.filter (fun tab -> Node.key tab <> key)

            match viewerNode model with
            | Some shown when Node.key shown = key ->
                let index = model.Tabs |> List.findIndex (fun tab -> Node.key tab = key)

                match tabs with
                | [] ->
                    { model with
                        Tabs = []
                        Viewer = NothingOpen
                        SelectedNode = None
                        SelectedEntry = None
                        PendingReveal = None
                        Following = false },
                    Cmd.none
                | _ -> openNode env { model with Tabs = tabs } tabs[min index (tabs.Length - 1)]
            | _ -> { model with Tabs = tabs }, Cmd.none

        | SetFileSearchMode mode ->
            match viewerNode model with
            | Some node -> { model with FileModes = model.FileModes.Add(Node.key node, mode) }, Cmd.none
            | None -> model, Cmd.none

        | ChangeRoot path when env.FileSystem.DirectoryExists path ->
            let rooted, loadRoot = rootedAt env model path
            let searched, searchCmd = requestSearch env rooted
            searched, Cmd.batch [ loadRoot; searchCmd ]

        | ChangeRoot _ -> model, Cmd.none

        | IncludeInSearch text
        | ExcludeFromSearch text ->
            let selected = (text.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead |> Option.defaultValue "").Trim()
            let term = if model.Search.Query.Mode = RegexSearch then Text.RegularExpressions.Regex.Escape selected else selected

            if term = "" then
                model, Cmd.none
            else
                let isInclude = (match msg with IncludeInSearch _ -> true | _ -> false)
                update env (SearchTextChanged(SearchPattern.addTerm isInclude model.Search.Query.Text term)) model

        | ShowQuickOpen ->
            let model = { model with QuickOpen = Some(quickOpenState model "" 0) }

            if model.FileIndex.IsNone && not model.IndexLoading then
                { model with IndexLoading = true }, loadIndex env model
            else
                model, Cmd.none

        | HideQuickOpen -> { model with QuickOpen = None }, Cmd.none

        | QuickOpenQueryChanged query ->
            match model.QuickOpen with
            | Some _ -> { model with QuickOpen = Some(quickOpenState model query 0) }, Cmd.none
            | None -> model, Cmd.none

        | QuickOpenMove delta ->
            match model.QuickOpen with
            | Some state when not state.Results.IsEmpty ->
                let count = state.Results.Length
                { model with QuickOpen = Some { state with Selected = (state.Selected + delta + count) % count } }, Cmd.none
            | _ -> model, Cmd.none

        | QuickOpenAccept index ->
            match model.QuickOpen with
            | Some state ->
                match state.Results |> List.tryItem (defaultArg index state.Selected) with
                | Some entry ->
                    let opened, openCmd = openNode env { model with QuickOpen = None } entry.Node
                    let expanded, expandCmd = expandTo env opened entry.Node.Location
                    expanded, Cmd.batch [ openCmd; expandCmd ]
                | None -> model, Cmd.none
            | None -> model, Cmd.none

        | FileIndexLoaded(root, result) when root = model.RootPath ->
            let model = { model with IndexLoading = false }

            match result with
            | Ok nodes ->
                let index =
                    nodes
                    |> List.map (fun node -> { Node = node; Display = Location.display model.RootPath node.Location })
                    |> Array.ofList

                let model = { model with FileIndex = Some index }

                match model.QuickOpen with
                | Some state -> { model with QuickOpen = Some(quickOpenState model state.Query state.Selected) }, Cmd.none
                | None -> model, Cmd.none
            | Error _ -> model, Cmd.none

        | FileIndexLoaded _ -> model, Cmd.none

    /// Long-running inputs, as Elmish subscriptions over Axial streams: search events from the runtime's hub,
    /// and, while following a file, its changes on disk (throttled so a busy log reloads a few times a second).
    let subscriptions (env: AppEnv) (model: Model) : Sub<Msg> =
        [ [ "search-events" ],
          fun dispatch ->
              Runtime.follow env (FlowStream.fromHub QueueStrategy.Unbounded env.SearchEvents) (SearchEventReceived >> dispatch)

          match followedNode model with
          | Some node ->
              let key = Node.key node

              [ "follow"; key ],
              fun dispatch ->
                  Runtime.follow env (Files.watch node |> FlowStream.throttle followThrottle) (fun _ -> dispatch (FileChanged key))
          | None -> () ]

    let program (env: AppEnv) (rootPath: string) =
        Program.mkProgram (init env rootPath) (update env) (fun _ _ -> ())
        |> Program.withSubscription (subscriptions env)
