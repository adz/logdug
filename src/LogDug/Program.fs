module LogDug.UI.Program

open System
open System.Runtime.InteropServices
open Avalonia
open Axial
open LogDug

module private Native =
    [<DllImport("kernel32.dll")>]
    extern bool AttachConsole(int processId)

let buildAvaloniaApp () =
    AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()

/// `--self-test`: run the end-to-end checks without a window and report through the exit code.
let private selfTest (localZone: TimeZoneInfo) =
    // A Windows GUI executable has no console of its own; borrow the parent's so a CI shell sees the report.
    if OperatingSystem.IsWindows() then Native.AttachConsole -1 |> ignore

    use runtime = Runtime.live localZone (fun callback -> callback ()) Shell.reportDefect

    match Flow.run runtime.Env (SelfTest.run runtime.Env) with
    | Exit.Success results ->
        Console.WriteLine(SelfTest.render results)
        if results |> List.forall _.Passed then 0 else 1
    | Exit.Failure cause ->
        Console.WriteLine(Cause.prettyPrint id cause)
        2

[<EntryPoint; STAThread>]
let main argv =
    let localZone =
        match Flow.run () Axial.TimeZone.local with
        | Exit.Success zone -> TimeZoneInfo.FindSystemTimeZoneById(Axial.TimeZone.id zone)
        | Exit.Failure _ -> TimeZoneInfo.Utc
    if argv |> Array.contains "--self-test" then
        selfTest localZone
    else
        App.LocalZone <- localZone
        buildAvaloniaApp().StartWithClassicDesktopLifetime argv
