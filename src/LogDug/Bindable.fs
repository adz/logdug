namespace LogDug.UI

open System
open System.Collections.Generic
open System.Windows.Input
open Elmish.Glue.Core

/// A command that forwards to an Elmish dispatch. It is always executable; the model decides what a message means.
[<Sealed>]
type Command(execute: obj -> unit) =
    let canExecuteChanged = Event<EventHandler, EventArgs>()

    new(execute: unit -> unit) = Command(fun (_: obj) -> execute ())

    interface ICommand with
        [<CLIEvent>]
        member _.CanExecuteChanged = canExecuteChanged.Publish

        member _.CanExecute _ = true
        member _.Execute parameter = execute parameter

/// Base for the F# viewmodels: one notifying setter that only raises PropertyChanged for real changes,
/// so echoing a snapshot back to a control that produced it never disturbs the control (caret, selection).
[<AbstractClass>]
type Bindable() =
    inherit BindableNode()

    member this.Change<'T>(current: byref<'T>, next: 'T, name: string) =
        if not (EqualityComparer<'T>.Default.Equals(current, next)) then
            current <- next
            this.NotifyPropertyChanged name
