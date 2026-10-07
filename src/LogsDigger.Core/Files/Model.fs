namespace LogsDigger.Files

open System

type ArchiveKind =
    | Zip
    | TarGz

/// Where a file lives. Archive contents are addressed by the archive's own location plus an entry path,
/// so nested archives (a zip inside a tar.gz) compose without special cases.
type Location =
    | Disk of path: string
    | Entry of archive: Location * entryPath: string

module Location =
    let rec key location =
        match location with
        | Disk path -> path
        | Entry(archive, entryPath) -> key archive + "!/" + entryPath

    let rec display (root: string) location =
        match location with
        | Disk path when path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ->
            let relative = path.Substring(root.Length).TrimStart('\\', '/')
            if relative = "" then "." else relative.Replace('\\', '/')
        | Disk path -> path
        | Entry(archive, entryPath) -> display root archive + " › " + entryPath

    let fileName (path: string) =
        let trimmed = path.TrimEnd('/', '\\')
        let index = trimmed.LastIndexOfAny([| '/'; '\\' |])
        if index < 0 then trimmed else trimmed.Substring(index + 1)

    let archiveKind (name: string) =
        let lower = name.ToLowerInvariant()
        if lower.EndsWith ".zip" then Some Zip
        elif lower.EndsWith ".tar.gz" || lower.EndsWith ".tgz" then Some TarGz
        else None

    let isGzipFile (name: string) =
        let lower = name.ToLowerInvariant()
        lower.EndsWith ".gz" && not (lower.EndsWith ".tar.gz")

    /// The file on disk that contains this location: itself, or the outermost archive around it.
    let rec diskPath location =
        match location with
        | Disk path -> path
        | Entry(archive, _) -> diskPath archive

[<RequireQualifiedAccess>]
type NodeKind =
    | Folder
    | Archive of ArchiveKind
    | File

type Node =
    { Location: Location
      Name: string
      Kind: NodeKind
      Size: int64 }

module Node =
    let key node = Location.key node.Location

    let isContainer node =
        match node.Kind with
        | NodeKind.Folder
        | NodeKind.Archive _ -> true
        | NodeKind.File -> false

    let ofName location name size isDirectory =
        let kind =
            if isDirectory then
                NodeKind.Folder
            else
                match Location.archiveKind name with
                | Some archive -> NodeKind.Archive archive
                | None -> NodeKind.File

        { Location = location; Name = name; Kind = kind; Size = size }

    /// Folders and archives first, then files, each alphabetical ignoring case.
    let sort (nodes: Node seq) =
        nodes
        |> Seq.sortBy (fun node -> (if isContainer node then 0 else 1), node.Name.ToLowerInvariant())
        |> List.ofSeq

type FilesError =
    | FileSystemFailure of message: string
    | ArchiveFailure of location: string * message: string
    | MissingEntry of location: string
    | WatchUnsupported of location: string

    override this.ToString() =
        match this with
        | FileSystemFailure message -> message
        | ArchiveFailure(location, message) -> $"Could not read archive {location}: {message}"
        | MissingEntry location -> $"Archive entry not found: {location}"
        | WatchUnsupported location -> $"Cannot watch {location}"

type ChangeKind =
    | Changed
    | Created
    | Deleted
    | Renamed

type FileChange =
    { Path: string
      Kind: ChangeKind }

    override this.ToString() =
        let kind =
            match this.Kind with
            | Changed -> "changed"
            | Created -> "created"
            | Deleted -> "deleted"
            | Renamed -> "renamed"

        $"{this.Path} {kind}"
