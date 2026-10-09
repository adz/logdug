module LogDug.Tests.Screenshots

open System
open System.Diagnostics
open System.IO
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Input
open Avalonia.Threading
open Axial.PlatformService
open Xunit
open LogDug
open LogDug.Files
open LogDug.UI
open LogDug.Tests.Support

let private pumpUntil (description: string) (condition: unit -> bool) =
    let clock = Stopwatch.StartNew()

    while not (condition ()) && clock.Elapsed < TimeSpan.FromSeconds 60.0 do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep 10

    Dispatcher.UIThread.RunJobs()
    if not (condition ()) then failwith $"Timed out waiting for: {description}"

let private settle () =
    for _ in 1..20 do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep 10

let private capture (window: Window) (name: string) =
    settle ()
    let folder = Path.Combine(repoRoot (), "docs", "screenshots")
    Directory.CreateDirectory folder |> ignore
    use frame = window.CaptureRenderedFrame()
    let path = Path.Combine(folder, name)
    frame.Save path
    Assert.True(FileInfo(path).Length > 10_000L, $"{name} looks empty")

let private row (vm: MainVm) name =
    pumpUntil $"tree row {name}" (fun () -> vm.TreeRows |> Seq.exists (fun row -> row.Name = name))
    vm.TreeRows |> Seq.find (fun row -> row.Name = name)

let private activate (vm: MainVm) name =
    let target = row vm name
    (target.ActivateCommand :> Windows.Input.ICommand).Execute null

let private openFile (vm: MainVm) name =
    activate vm name
    pumpUntil $"{name} to open" (fun () -> vm.ShowEntries && vm.ViewerTitle.EndsWith name)

/// Selects the first entry at `level` and scrolls it near the top of the list.
let private focusFirst (window: Window) (vm: MainVm) (dispatch: Msg -> unit) (pick: EntryVm -> bool) =
    let list = window.FindControl<ListBox> "EntriesList"
    let rowIndex = vm.Entries |> Array.findIndex pick
    list.ScrollIntoView(min (rowIndex + 40) (vm.Entries.Length - 1))
    settle ()
    list.ScrollIntoView(max 0 (rowIndex - 3))
    dispatch (SelectEntry(Some vm.Entries[rowIndex].Index))
    pumpUntil "entry detail" (fun () -> vm.HasDetail)

let private scenario () =
    AppBuilder
        .Configure<App>()
        .UseSkia()
        .UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false))
        .WithInterFont()
        .SetupWithoutStarting()
    |> ignore

    let settingsFolder = Directory.CreateTempSubdirectory "logdug-shots"

    use runtime =
        Runtime.start
            { PlatformEnv.live () with EnvironmentVariables = EnvironmentVariables.fromPairs [ "APPDATA", settingsFolder.FullName ] }
            (TimeZoneInfo.FindSystemTimeZoneById "Europe/London")
            Shell.postToDispatcher

    let env = runtime.Env

    let window, connection = Shell.create env (samples ())
    let dispatch (msg: Msg) = connection.Dispatch.Invoke msg
    let vm = window.DataContext :?> MainVm
    window.Show()

    pumpUntil "root folders" (fun () -> vm.TreeRows.Count >= 5)
    capture window "00-start.png"

    activate vm "app"
    openFile vm "orders-api.log"
    focusFirst window vm dispatch (fun entry -> entry.Entry.Level = Level.Error)
    capture window "01-text-log.png"

    openFile vm "orders-api.jsonl"
    focusFirst window vm dispatch (fun entry -> entry.Entry.Level = Level.Error)
    capture window "02-jsonl.png"

    openFile vm "inventory.csv"
    window.FindControl<ListBox>("EntriesList").ScrollIntoView 0
    capture window "07-csv.png"

    openFile vm "startup-notes.log"
    capture window "08-no-timestamps.png"

    activate vm "archives"
    activate vm "incident-4711.tar.gz"
    activate vm "incident-4711"
    activate vm "attachments"
    activate vm "previous-nightly.zip"
    activate vm "logs"
    activate vm "workers"
    openFile vm "email-worker.ndjson"
    focusFirst window vm dispatch (fun entry -> entry.Entry.Level = Level.Fatal)
    capture window "03-archives.png"

    dispatch (SelectEntry None)
    dispatch ToggleRegex
    let search = window.FindControl<TextBox> "SearchBox"
    let entries = window.FindControl<ListBox> "EntriesList"
    entries.Focus() |> ignore
    window.KeyPressQwerty(PhysicalKey.Slash, RawInputModifiers.None)
    pumpUntil "/ to focus search" (fun () -> search.IsFocused)
    Assert.Equal("", search.Text)
    entries.Focus() |> ignore
    window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control)
    pumpUntil "Ctrl+F to focus search" (fun () -> search.IsFocused)
    window.KeyTextInput "timed out|ECONNRESET"
    pumpUntil "search to finish" (fun () -> vm.HasSearch && not vm.IsSearching && vm.SearchStatus.Contains "matches")
    Assert.Equal("timed out|ECONNRESET", vm.SearchText)
    window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None)
    window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None)
    window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None)
    pumpUntil "hit to open" (fun () -> vm.ShowEntries && vm.CursorText.StartsWith "3 /")
    capture window "04-search.png"

    dispatch (SetZone "Australia/Adelaide")
    pumpUntil "zone caption" (fun () -> vm.TimeCaption.StartsWith "Australia/Adelaide")
    capture window "05-target-zone.png"

    dispatch ToggleTheme
    pumpUntil "light theme" (fun () -> not vm.IsDark)
    capture window "06-light.png"

    let mutable closed = false
    window.Closed.Add(fun _ -> closed <- true)
    window.KeyPressQwerty(PhysicalKey.F4, RawInputModifiers.Alt)
    pumpUntil "Alt+F4 to close the window" (fun () -> closed)
    (connection :> IDisposable).Dispose()

    // Follow mode: a second window over a scratch folder, with a log that grows while it is open.
    let liveFolder = Directory.CreateTempSubdirectory "logdug-follow"
    let livePath = Path.Combine(liveFolder.FullName, "live.log")
    File.WriteAllText(livePath, "2026-10-07 09:00:00 [INF] started\n")

    let liveWindow, liveConnection = Shell.create env liveFolder.FullName
    let liveVm = liveWindow.DataContext :?> MainVm
    liveWindow.Show()
    openFile liveVm "live.log"
    liveConnection.Dispatch.Invoke ToggleFollow
    pumpUntil "following" (fun () -> liveVm.IsFollowing)
    settle ()

    for i in 1..5 do
        File.AppendAllText(livePath, $"2026-10-07 09:00:0{i} [WRN] appended {i}\n")

    pumpUntil "appended entries to load" (fun () -> liveVm.Entries.Length = 6)
    Assert.Contains("appended 5", liveVm.Entries[5].Entry.Message)
    (liveConnection :> IDisposable).Dispose()
    liveWindow.Close()

[<Fact>]
let ``capture screenshots of the main flows`` () =
    let mutable failure: exn option = None

    let thread =
        Thread(fun () ->
            try
                scenario ()
            with error ->
                failure <- Some error)

    thread.Start()
    thread.Join()

    match failure with
    | Some error -> raise (Exception("Screenshot scenario failed", error))
    | None -> ()
