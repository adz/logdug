namespace LogDug.UI

open System
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Markup.Xaml
open Avalonia.Media.Imaging
open Avalonia.Threading
open LogDug

/// Command-line options. `--snapshot` renders the window to a PNG and exits, which lets a published
/// (NativeAOT) build prove it renders and searches without a person at the screen.
type private Options =
    { Folder: string array
      Snapshot: string option
      Search: string option }

module private Options =
    let parse (args: string array) =
        let rec go (rest: string list) options =
            match rest with
            | "--snapshot" :: path :: tail -> go tail { options with Snapshot = Some path }
            | "--search" :: text :: tail -> go tail { options with Search = Some text }
            | folder :: tail -> go tail { options with Folder = Array.append options.Folder [| folder |] }
            | [] -> options

        go (List.ofArray args) { Folder = [||]; Snapshot = None; Search = None }

type App() =
    inherit Application()

    let after (delay: float) (action: unit -> unit) =
        DispatcherTimer.RunOnce((fun () -> action ()), TimeSpan.FromSeconds delay) |> ignore

    override this.Initialize() = AvaloniaXamlLoader.Load this

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let options = Options.parse (if isNull desktop.Args then [||] else desktop.Args)
            let runtime = Runtime.live Shell.postToDispatcher
            let env = runtime.Env
            let window, connection = Shell.create env (Shell.rootPath env options.Folder)
            desktop.MainWindow <- window
            Shell.fileArgument env options.Folder |> Option.iter (fun path -> connection.Dispatch.Invoke(OpenPath path))

            desktop.Exit.Add(fun _ ->
                (connection :> IDisposable).Dispose()
                (runtime :> IDisposable).Dispose())

            match options.Snapshot with
            | Some path ->
                options.Search |> Option.iter (fun text -> after 2.0 (fun () -> connection.Dispatch.Invoke(SearchTextChanged text)))

                after 5.0 (fun () ->
                    options.Search |> Option.iter (fun _ -> connection.Dispatch.Invoke NextHit)

                    after 2.0 (fun () ->
                        let size = PixelSize(int window.Bounds.Width, int window.Bounds.Height)
                        use bitmap = new RenderTargetBitmap(size, Vector(96.0, 96.0))
                        bitmap.Render window
                        bitmap.Save path
                        desktop.Shutdown()))
            | None -> ()
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
