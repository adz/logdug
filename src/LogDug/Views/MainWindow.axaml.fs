namespace LogDug.UI

open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Markup.Xaml
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

        vm.PropertyChanged.Add(fun args ->
            match args.PropertyName with
            | "IsDark" -> applyTheme ()
            | "HasSearch" -> applyResultsColumn ()
            | _ -> ())

