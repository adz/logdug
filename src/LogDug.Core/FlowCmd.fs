namespace LogDug

open Axial
open Elmish

/// Runs Axial flows as Elmish commands. Results come back through `post`, so every message reaches
/// Elmish on one thread no matter which worker thread finished the flow.
module FlowCmd =
    let describe (render: 'error -> string) (cause: Cause<'error>) = Cause.prettyPrint render cause

    let attempt
        (post: (unit -> unit) -> unit)
        (environment: 'env)
        (render: 'error -> string)
        (toMsg: Result<'value, string> -> 'msg)
        (work: Flow<'env, 'error, 'value>)
        : Cmd<'msg> =
        Cmd.ofEffect (fun dispatch ->
            async {
                let! exit = Flow.toAsync environment work

                let result =
                    match exit with
                    | Exit.Success value -> Ok value
                    | Exit.Failure cause -> Error(describe render cause)

                post (fun () -> dispatch (toMsg result))
            }
            |> Async.Start)

    /// Runs a flow for its effect only; failures are ignored.
    let fireAndForget (environment: 'env) (work: Flow<'env, 'error, unit>) : Cmd<'msg> =
        Cmd.ofEffect (fun _ -> Flow.toAsync environment work |> Async.Ignore |> Async.Start)
