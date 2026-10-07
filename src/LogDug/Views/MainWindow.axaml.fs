namespace LogDug.UI

open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Markup.Xaml
open Avalonia.Styling
open Avalonia.Threading

type MainWindow() as this =
    inherit ShadUI.Window()

    do AvaloniaXamlLoader.Load this

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

    override this.OnKeyDown(args: KeyEventArgs) =
        if args.Key = Key.F && args.KeyModifiers.HasFlag KeyModifiers.Control then
            let search = this.FindControl<TextBox> "SearchBox"
            search.Focus() |> ignore
            search.SelectAll()
            args.Handled <- true
        else
            base.OnKeyDown args
