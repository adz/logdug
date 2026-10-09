namespace LogDug.UI

open System
open System.Diagnostics
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Input.Platform
open LogDug.Files

/// Hand-offs to the rest of the desktop from a file's right-click menu: an editor, the file manager, the clipboard.
module Desktop =
    /// The file on disk behind a node: itself, or the outermost archive that contains it.
    let diskPathOf (node: Node) = Location.diskPath node.Location

    /// Full path to copy; entries inside an archive read `archive.zip!/inner/path`.
    let copyablePath (node: Node) = Location.key node.Location

    let revealLabel =
        if OperatingSystem.IsWindows() then "Show in Explorer"
        elif OperatingSystem.IsMacOS() then "Reveal in Finder"
        else "Show in file manager"

    let private launch (info: ProcessStartInfo) =
        try
            info.UseShellExecute <- false
            info.CreateNoWindow <- true
            Process.Start info |> ignore
        with _ ->
            ()

    let private command (file: string) (arguments: string list) =
        let info = ProcessStartInfo(file)
        for argument in arguments do info.ArgumentList.Add argument
        launch info

    /// `code` is a script on Windows (code.cmd), so it goes through cmd.exe there.
    let openInVsCode (path: string) =
        if OperatingSystem.IsWindows() then command "cmd.exe" [ "/c"; "code"; path ]
        elif OperatingSystem.IsMacOS() then command "open" [ "-a"; "Visual Studio Code"; path ]
        else command "code" [ path ]

    let reveal (node: Node) =
        let path = diskPathOf node
        let isFolder = node.Kind = NodeKind.Folder && (match node.Location with Disk _ -> true | _ -> false)

        if OperatingSystem.IsWindows() then
            let info = ProcessStartInfo("explorer.exe")
            info.Arguments <- if isFolder then $"\"{path}\"" else $"/select,\"{path}\""
            launch info
        elif OperatingSystem.IsMacOS() then
            command "open" (if isFolder then [ path ] else [ "-R"; path ])
        else
            command "xdg-open" [ (if isFolder then path else IO.Path.GetDirectoryName path) ]

    let copy (text: string) =
        match Application.Current with
        | null -> ()
        | app ->
            match app.ApplicationLifetime with
            | :? IClassicDesktopStyleApplicationLifetime as desktop when not (isNull desktop.MainWindow) ->
                match TopLevel.GetTopLevel desktop.MainWindow with
                | null -> ()
                | top ->
                    match top.Clipboard with
                    | null -> ()
                    | clipboard -> clipboard.SetTextAsync text |> ignore
            | _ -> ()
