module LogDug.UI.Program

open System
open System.Runtime.InteropServices
open Avalonia
open Axial
open Axial.Hosting
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
        // A failed check is an ordinary failure; a defect in the checks themselves is the defect exit code.
        if results |> List.forall _.Passed then DotNetApp.exitCode (Exit.Success())
        else DotNetApp.exitCode (Exit.Failure(Cause.Fail results))
    | Exit.Failure cause ->
        Console.WriteLine(Cause.prettyPrint id cause)
        DotNetApp.exitCode (Exit.Failure cause)

[<EntryPoint; STAThread>]
let main argv =
    let localZone =
        match Flow.run () Axial.TimeZone.local with
        | Exit.Success zone -> Axial.TimeZone.toTimeZoneInfo zone
        | Exit.Failure _ -> TimeZoneInfo.Utc
    if argv |> Array.contains "--self-test" then
        selfTest localZone
    else
        App.LocalZone <- localZone
        buildAvaloniaApp().StartWithClassicDesktopLifetime argv
