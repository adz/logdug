namespace LogDug.UI

open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open Avalonia.Platform.Storage
open Avalonia.Styling
open Avalonia.Threading

type MainWindow() as this =
    inherit ShadUI.Window()

    let focusSearch () =
        let search = this.FindControl<TextBox> "SearchBox"
        search.Focus() |> ignore
        search.SelectAll()

    let typingInTextBox () =
        match this.FocusManager with
        | null -> false
        | focus -> focus.GetFocusedElement() :? TextBox

    /// Window shortcuts run before any control sees the key (tunnelling), so a focused list or button
    /// can't swallow them. `/` is left alone while typing, so it still types into a text box.
    let onKeyDown (args: KeyEventArgs) =
        let control = args.KeyModifiers.HasFlag KeyModifiers.Control
        let alt = args.KeyModifiers.HasFlag KeyModifiers.Alt

        if args.Key = Key.F && control then
            focusSearch ()
            args.Handled <- true
        elif args.KeySymbol = "/" && not control && not alt && not (typingInTextBox ()) then
            focusSearch ()
            args.Handled <- true
        elif args.Key = Key.O && control then
            this.RootPicker |> Option.iter (fun pick -> pick ())
            args.Handled <- true
        elif args.Key = Key.F4 && alt then
            this.Close()
            args.Handled <- true

    do
        AvaloniaXamlLoader.Load this
        this.AddHandler(InputElement.KeyDownEvent, System.EventHandler<KeyEventArgs>(fun _ args -> onKeyDown args), Avalonia.Interactivity.RoutingStrategies.Tunnel)

    member this.Attach(vm: MainVm) =
        this.DataContext <- vm

        let entries = this.FindControl<ListBox> "EntriesList"

        // Scrolling is a view concern, so the viewmodel only announces which row to reveal.
        vm.RevealRequested.Add(fun (rowIndex, select) ->
            Dispatcher.UIThread.Post(
                (fun () ->
                    entries.ScrollIntoView rowIndex
                    if select then entries.SelectedIndex <- rowIndex),
                DispatcherPriority.Loaded
            ))

        let applyTheme () =
            match Application.Current with
            | null -> ()
            | app -> app.RequestedThemeVariant <- if vm.IsDark then ThemeVariant.Dark else ThemeVariant.Light

        // The results column takes no space until there is a search to show.
        let body = this.FindControl<Grid> "Body"

        let applyResultsColumn () =
            body.ColumnDefinitions[3].Width <- GridLength(if vm.HasSearch then 1.0 else 0.0)
            body.ColumnDefinitions[4].Width <- GridLength(if vm.HasSearch then 420.0 else 0.0)

        applyTheme ()
        applyResultsColumn ()

        let quickOpenBox = this.FindControl<TextBox> "QuickOpenBox"
        let quickOpenList = this.FindControl<ItemsControl> "QuickOpenList"

        // The Ctrl+P box takes focus when it opens, and keeps the highlighted file in view as it moves.
        let revealQuickOpenSelection () =
            Dispatcher.UIThread.Post(
                (fun () ->
                    match quickOpenList.ContainerFromIndex vm.QuickOpenSelectedIndex with
                    | null -> ()
                    | container -> container.BringIntoView()),
                DispatcherPriority.Loaded
            )

        vm.QuickOpenItems.CollectionChanged.Add(fun _ -> revealQuickOpenSelection ())

        vm.PropertyChanged.Add(fun args ->
            match args.PropertyName with
            | "IsDark" -> applyTheme ()
            | "HasSearch" -> applyResultsColumn ()
            | "QuickOpenVisible" when vm.QuickOpenVisible ->
                Dispatcher.UIThread.Post(
                    (fun () ->
                        quickOpenBox.Focus() |> ignore
                        quickOpenBox.SelectAll()),
                    DispatcherPriority.Input
                )
            | _ -> ())

        this.FindControl<Border>("QuickOpenBackdrop").PointerPressed.Add(fun _ -> (vm.HideQuickOpenCommand :> System.Windows.Input.ICommand).Execute null)

        quickOpenList.Tapped.Add(fun args ->
            match args.Source with
            | :? Control as source ->
                match source.DataContext with
                | :? QuickOpenItemVm as item -> vm.AcceptQuickOpenAt(vm.QuickOpenItems.IndexOf item)
                | _ -> ()
            | _ -> ())

        // Arrow keys, Enter and Escape drive the list while the box is open, before the text box can claim them.
        this.AddHandler(
            InputElement.KeyDownEvent,
            (fun _ (args: KeyEventArgs) ->
                if vm.QuickOpenVisible then
                    let run (command: LogDug.UI.Command) =
                        (command :> System.Windows.Input.ICommand).Execute null
                        args.Handled <- true

                    match args.Key with
                    | Key.Down -> run vm.QuickOpenDownCommand
                    | Key.Up -> run vm.QuickOpenUpCommand
                    | Key.Enter -> run vm.AcceptQuickOpenCommand
                    | Key.Escape -> run vm.HideQuickOpenCommand
                    | _ -> ()),
            RoutingStrategies.Tunnel
        )

        this.FindControl<Button>("RootButton").Click.Add(fun _ -> this.PickRoot vm)
        this.RootPicker <- Some(fun () -> this.PickRoot vm)


    member val private RootPicker: (unit -> unit) option = None with get, set

    /// Asks for a folder and browses it instead.
    member private this.PickRoot(vm: MainVm) =
        task {
            let! start = this.StorageProvider.TryGetFolderFromPathAsync vm.RootPath

            let! folders =
                this.StorageProvider.OpenFolderPickerAsync(
                    FolderPickerOpenOptions(Title = "Browse folder", AllowMultiple = false, SuggestedStartLocation = start)
                )

            if folders.Count > 0 then
                match folders[0].TryGetLocalPath() with
                | null -> ()
                | path -> vm.ChangeRoot path
        }
        |> ignore
