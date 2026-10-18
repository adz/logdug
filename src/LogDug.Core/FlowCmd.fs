namespace LogDug

open Axial
open Elmish

/// Runs an Axial flow as an Elmish command for its side effects only; the outcome is discarded and no message
/// is dispatched. Kept because `Cmd.OfFlow.*` has no equivalent that drops both the success and the typed
/// failure of a workflow: `either`/`perform`/`attempt` all dispatch a message on completion.
module FlowCmd =
    let fireAndForget (environment: 'env) (work: Flow<'env, 'error, unit>) : Cmd<'msg> =
        Cmd.ofEffect (fun _ -> Flow.toAsync environment work |> Async.Ignore |> Async.Start)
