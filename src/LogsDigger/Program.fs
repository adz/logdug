module LogsDigger.UI.Program

open System
open System.Runtime.InteropServices
open Avalonia
open Axial
open LogsDigger

module private Native =
    [<DllImport("kernel32.dll")>]
    extern bool AttachConsole(int processId)

let buildAvaloniaApp () =
    AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()

/// `--self-test`: run the end-to-end checks without a window and report through the exit code.
let private selfTest () =
    // A Windows GUI executable has no console of its own; borrow the parent's so a CI shell sees the report.
    if OperatingSystem.IsWindows() then Native.AttachConsole -1 |> ignore

    use runtime = Runtime.live (fun callback -> callback ())

    match Flow.run runtime.Env (SelfTest.run runtime.Env) with
    | Exit.Success results ->
        printfn "%s" (SelfTest.render results)
        if results |> List.forall _.Passed then 0 else 1
    | Exit.Failure cause ->
        printfn "%s" (Cause.prettyPrint id cause)
        2

[<EntryPoint; STAThread>]
let main argv =
    if argv |> Array.contains "--self-test" then
        selfTest ()
    else
        buildAvaloniaApp().StartWithClassicDesktopLifetime argv
