module LogsDigger.Tests.AppTests

open System.IO
open Xunit
open LogsDigger
open LogsDigger.Files

let private root () = Path.Combine(Path.GetTempPath(), "logs")

[<Fact>]
let ``ancestors of a file inside a nested archive are every container on the way`` () =
    let root = root ()
    let tarball = Disk(Path.Combine(root, "archives", "incident.tar.gz"))
    let nestedZip = Entry(tarball, "incident/attachments/nightly.zip")
    let file = Entry(nestedZip, "logs/app.log")

    let names = App.ancestors root file |> List.map (fun node -> node.Name, node.Kind)

    Assert.Equal<(string * NodeKind) list>(
        [ "archives", NodeKind.Folder
          "incident.tar.gz", NodeKind.Archive TarGz
          "incident", NodeKind.Folder
          "attachments", NodeKind.Folder
          "nightly.zip", NodeKind.Archive Zip
          "logs", NodeKind.Folder ],
        names
    )

[<Fact>]
let ``ancestor keys match the keys the tree uses for loaded children`` () =
    let root = root ()
    let zip = Path.Combine(root, "a.zip")
    let keys = App.ancestors root (Entry(Disk zip, "x/y.log")) |> List.map Node.key
    Assert.Equal<string list>([ zip; zip + "!/x" ], keys)
