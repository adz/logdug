# Codebase guide

This guide explains how Log Dug is put together, where each concern lives, and how to change it. Read it top to bottom once; after that, use the file table as a map.

## The shape of the app

The solution has two application projects and a test project:

| Project | Language | Role |
| --- | --- | --- |
| `src/LogDug.Core` | F# | Everything that is not UI: the domain types, archive-aware file access, parsing, rendering to coloured segments, search, settings, and the Elmish program (model, messages, update). It has no Avalonia reference. |
| `src/LogDug` | F# + AXAML | The Avalonia app: F# viewmodels, F# code-behind, AXAML views and styles. It contains no C#. |
| `tests/LogDug.Tests` | F# | xUnit v3 tests for Core, plus a headless Avalonia scenario that drives the real window and writes the screenshots in `docs/screenshots`. |

Four libraries do the heavy lifting:

- **Elmish** owns state. There is one immutable `Model`, one `Msg` union and one `update` function (`App.fs`).
- **Elmish.Avalonia.Glue** connects that program to Avalonia. The app uses its *projection* style: an F# viewmodel (`MainVm`) receives each new model and copies it into bindable properties, and setters dispatch messages instead of changing state.
- **Axial** describes every effect as a `Flow<'env, 'error, 'value>` with a typed error channel, and every sequence of effects (walking a tree, reading a file's lines, search results, file changes) as a `FlowStream`. An Axial application root (`Runtime.fs`) owns the long-lived services: the `Files` service with its archive `Cache`, a search command `Queue`, and a search event `Hub`. **Axial.Guardrails** runs at `error` severity on every project, so code that touches `System.IO.File`, `Directory`, the ambient clock, environment variables or `Console` directly fails the build.
- **Reified** declares the settings file schema (validation on load, compiled JSON codec on save), refines a raw search query into a `SearchPattern` that is known to be valid, and parses JSON log lines into its `Data` tree.

The look comes from **ShadUI** (a shadcn-style theme for Avalonia) plus `Views/Styles.axaml`. Icons are the Lucide icon font; log text uses JetBrains Mono.

## How a change flows

```text
user action (click, keystroke)
  -> viewmodel setter or Command        (src/LogDug/ViewModels.fs)
  -> dispatch Msg
  -> App.update returns Model * Cmd     (src/LogDug.Core/App.fs)
  -> Cmd runs an Axial Flow off the UI thread (FlowCmd.fs)
  -> the result Msg is posted back to the UI thread (AppEnv.Post)
  -> App.update
  -> MainVm.Update(model) raises PropertyChanged only for values that changed
  -> AXAML bindings refresh
```

Searches and live file changes do not use one-shot commands. They are streams that run inside the Axial runtime and reach Elmish through subscriptions:

```text
SearchTextChanged -> App.update -> Queue.tryOffer (StartSearch request)       one-slot sliding queue
Runtime.searchPipeline: fromDequeue -> debounce 200 ms -> switchMapFlow runSearch
runSearch: Files.walk -> filter searchable -> mapFlowPar 4 (Files.lines per file) -> scan totals -> cap
         -> groupedWithin 64 / 120 ms -> Hub.publish SearchProgress ... SearchEnded
Elmish subscription "search-events": FlowStream.fromHub -> post -> SearchEventReceived

ToggleFollow -> subscription "follow/<file>": Files.watch -> throttle 300 ms -> post -> FileChanged -> reload, scroll to end
```

A newer search interrupts the running one through `switchMapFlow`, which closes its open files and stops its walk before the next search starts. Each event carries its request id, so an event that was already in flight for an older query is ignored.

Two details make this smooth:

1. `Shell.postToUi` runs model updates inline when already on the UI thread. A keystroke in the search box therefore dispatches, updates and refreshes the viewmodel in one pass, so the two-way `TextBox` binding never receives a stale echo of its own text. Background results arrive through `AppEnv.Post`, which queues onto the Avalonia dispatcher.
2. `Bindable.Change` and each row's `Update` only raise `PropertyChanged` when a value really changed. Echoing a snapshot back to the control that produced it does not move the caret or reset a selection.

## Core, file by file

Files compile in this order, and each only depends on the files above it.

| File | What it owns |
| --- | --- |
| `Files/Model.fs` | File vocabulary with no log knowledge. `Location` addresses a file on disk or an entry inside an archive (`Entry(archive, path)`); because `archive` is itself a `Location`, a zip inside a tar.gz needs no special case. Also `Node`, `FilesError` and `FileChange`. |
| `Files/Archive.fs` | Decodes a zip or tar.gz into an in-memory `ArchiveIndex`: its implied directory tree and each file's bytes. Entry paths are normalised (no leading `/`, no empty segments). |
| `Files/Files.fs` | The `Files` service, an archive-transparent file system as an Axial service (`IFiles`, `IHasFiles`). The interface holds three primitives (`Children`, `OpenRead`, `Watch`); the module composes the rest: `readText`, `reader` (a scoped resource that sniffs for binary and gunzips `.gz`), `lines` (a lazy `FlowStream` read in batches that closes the file when the consumer stops), `walk` (a lazy depth-first `FlowStream` through folders and archives), and `watch` (a `FlowStream` of changes from a `FileSystemWatcher`, fed through an Axial `Queue`). `Files.make` builds the live service in the current scope, with decoded archives shared through an Axial `Cache` keyed by the outermost file's last-write time and capped at the 12 most recently used. This folder could become a package on its own. |
| `Domain.fs` | The log vocabulary: `Level`, `LogEntry`, `LogDocument` (with `entryAtLine`), `TimeDisplay`, and the search result types. |
| `LogParser.fs` | Pure text to `LogDocument`. Lines that start with a timestamp or a level token start a new entry; other lines (stack traces, wrapped output) join the entry above. A file with no such markers shows each line as its own entry. JSON lines are parsed with `Reified.Json.parseData`, and well-known keys become timestamp, level, message and exception; everything else becomes `Fields`. Serilog `@mt` templates are rendered with their property values. |
| `Time.fs` | Time zones. `Time.context` resolves a `TimeDisplay` to a `TimeZoneInfo` and a caption once per change; `Time.format` converts and formats one timestamp. |
| `Pattern.fs` | `SearchPattern`, a Reified refinement over `SearchQuery`. `SearchPattern.create` is the only way to build one, so it is always non-blank and, in regex mode, compiles. Invalid input comes back as a readable message ("Invalid regex: ..."). |
| `Render.fs` | Turns an entry into `Segment`s with a `Tone` (string, number, key, exception, link, stack frame and so on) and marks search matches with `Hit`. Pure, so the same output drives the UI and the tests. |
| `Search.fs` | `Search.run` is a `FlowStream` of `SearchStep`s: it walks the tree, searches four files at a time by streaming their lines (stopping each file one hit past its cap of 500), keeps running totals with `scan`, and ends after the step that reaches 20,000 hits, which also stops the walk and any in-flight files. Each `FileHits` carries its walk order, so results display in tree order whatever order they finish in. |
| `Settings.fs` | The persisted settings record and its Reified schema. Loading parses the file through the schema (bad files fall back to defaults); saving uses the compiled codec. |
| `Env.fs` | The search command and event types, `PlatformEnv` (file system, clock, environment variables; `PlatformEnv.live` names the live implementations), and `AppEnv`, which adds the runtime's services, the local time zone and `Post`. |
| `Runtime.fs` | Starts the Axial application root (`App.start`) that owns `Files`, the search queue and hub, and the search pipeline, and hands back an `AppEnv`. Disposing it stops the root and closes its scope. `Runtime.follow` runs a stream against the env and delivers each value through `Post`, which is how Elmish subscriptions follow Axial streams. |
| `FlowCmd.fs` | Runs a Flow as an Elmish `Cmd` and posts the result message back. |
| `App.fs` | The Elmish program: `Model`, `Msg`, `init`, `update`, and `subscriptions`. Search requests go to the runtime's queue and results come back as `SearchEventReceived`. Opening a search hit expands every ancestor container (`App.ancestors`) and reveals the entry at the hit's line once the file loads. Follow mode subscribes to `Files.watch` for the open file. |
| `Shape.fs` | Pure projections of the model into what the screen shows: flattened tree rows, result rows, level chips, status text. |

## UI, file by file

| File | What it owns |
| --- | --- |
| `Bindable.fs` | `Command` (an `ICommand` that forwards to dispatch) and `Bindable`, the viewmodel base with the change-only setter. |
| `ViewModels.fs` | The F# viewmodels. `MainVm` implements Glue's `IProjection<Model>` and `IDispatchTarget<Msg>`. The tree, results and level chips are `ObservableCollection`s kept in step with `SyncWith`, keyed by node location or result position, so expanding a folder inserts rows without rebuilding the rest. Log entries are lightweight `EntryVm`s rebuilt only when the file, level filter, zone or search changes; each computes its coloured segments lazily, when the list realises the row. |
| `LogLine.fs` | A `TextBlock` that renders its `EntryVm`'s segments as runs with `tone-*` classes. It reads its `DataContext` instead of using an attached property, because F# cannot emit the public static field that XAML bindings require. |
| `Shell.fs` | Builds the window and starts the Elmish program with `ElmishHost.startAndBindWithPost`. The app and the screenshot test both use it; `App.axaml.fs` starts the `Runtime` first and disposes it on exit. |
| `Views/MainWindow.axaml(.fs)` | The layout: toolbar (search, match-case and regex toggles, next/previous, time mode and zone picker), tree, viewer with level chips and detail pane, results panel, status bar. The code-behind only handles scrolling to a revealed entry, the theme switch, the results column width and `Ctrl+F`. |
| `Views/Styles.axaml` | Every colour decision: tones, level badges, row tints, tree icons, segmented toggles. Colours are theme resources in `App.axaml`, so light and dark switch without re-rendering. |
| `App.axaml(.fs)`, `Program.fs` | Startup and command-line options (`[folder]`, `--search <text>`, `--snapshot <png>`). |

## Common changes

**Recognise a new log format.** Add its timestamp shape to `timestampPattern` or its level words to `leadingLevelPattern` and `Level.tryParse`. For JSON, add key names to `timestampKeys`, `levelKeys`, `messageKeys` or `exceptionKeys` in `LogParser.fs`. Add a test to `ParserTests.fs` with a real line.

**Support another archive type.** Add a case to `ArchiveKind` and `Location.archiveKind`, write an `Archive.ofX` that returns an `ArchiveIndex`, and add it to `Archive.decode`.

**Add a long-running background feature.** Model it as a `FlowStream`, run it inside the runtime (as `Runtime.searchPipeline` does) or as an Elmish subscription with `Runtime.follow`, and send results to the UI as messages. Use a `Queue` for one consumer, a `Hub` for many, and `switchMapFlow` when only the newest request matters.

**Add a setting.** Add the field to `Settings`, its line to `Settings.schema`, a default, and a message that updates it in `App.update` and calls `saveSettings`.

**Add something to the screen.** Derive the value in `Shape.fs` (pure, testable), expose it on `MainVm` with `this.Change(...)` inside `Update`, and bind it in AXAML with a compiled binding. If the user can edit it, give the property a setter that dispatches a message.

**Add an effect.** Write it as a `Flow` against a service on `AppEnv`. If the service is new, add an interface field to `AppEnv` and its live implementation in `AppEnv.live`. Do not call `File`, `Directory`, `DateTime.Now` or `Environment` directly; Guardrails will fail the build and tell you which service to use.

## Rules the build enforces

- **Axial.Guardrails** (`AxialGuardrailsSeverity=error` in `Directory.Build.props`): no ambient effects outside the service boundary (AXG001), no `raise`/`failwith` inside `flow { }` (AXG003), no discarded cancellation tokens (AXG005), and no reflection-based formatting such as `%A` or `string` on a union without a `ToString` override (AXG006). Error types like `FilesError` override `ToString` for this reason.
- **AOT**: Core sets `IsAotCompatible`, the app sets `PublishAot`, and every AXAML binding is compiled (`x:DataType` everywhere). A NativeAOT publish reports no trim or AOT warnings for this code; the only aggregate warning comes from FSharp.Core's own reflection-based printing, which the app does not call.
- **Central package versions** live in `Directory.Packages.props`.

## Tests

`dotnet run --project tests/LogDug.Tests` runs everything:

- `ParserTests`, `RenderTests`, `SettingsTests`, `AppTests` cover the pure Core logic.
- `FilesTests` reads the generated `samples` folder through the `Files` service, including a zip nested inside a tar.gz. It checks that `lines` stops early and releases the file, that `walk` is lazy and depth first, that `watch` reports a real change, and that the search pipeline runs only the newest of two quick requests.
- `Screenshots` starts Avalonia headless with Skia, opens the real `MainWindow`, clicks tree rows through their commands, types into the search box, presses Enter, switches zone and theme, and saves a PNG at each step. It then opens a second window on a scratch folder, turns Follow on, appends to the open log, and waits for the new entries to appear. If a step's state never appears, the test fails with what it was waiting for.

Regenerate the sample data with `dotnet fsi scripts/make-samples.fsx`. It is deterministic.
