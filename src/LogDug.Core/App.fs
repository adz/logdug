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

type Model =
    { RootPath: string
      Tree: TreeState
      SelectedNode: string option
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
                return! Flow.fromBlocking (fun _ -> text |> Option.map (LogParser.parse options) |> Option.defaultValue LogParser.binary)
            }

        { model with
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
            { model with
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

            { model with Tree = tree }, Cmd.none

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
