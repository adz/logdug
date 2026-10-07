namespace LogsDigger.Files

open System
open System.Collections.Generic
open System.Formats.Tar
open System.IO
open System.IO.Compression

/// One child of a directory inside an archive.
type ArchiveChild =
    { Name: string
      Path: string
      IsDirectory: bool
      Size: int64 }

/// A decoded archive held in memory: its directory structure plus each file's bytes.
/// Directories are implied by file paths because many archives omit explicit directory entries.
[<Sealed>]
type ArchiveIndex(files: (string * int64) seq, read: string -> byte array) =
    let children = Dictionary<string, Dictionary<string, ArchiveChild>>(StringComparer.Ordinal)

    let childrenOf directory =
        match children.TryGetValue directory with
        | true, existing -> existing
        | false, _ ->
            let created = Dictionary<string, ArchiveChild>(StringComparer.Ordinal)
            children[directory] <- created
            created

    do
        childrenOf "" |> ignore

        for path, size in files do
            let segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)

            for depth in 0 .. segments.Length - 1 do
                let parent = String.Join("/", segments, 0, depth)
                let current = String.Join("/", segments, 0, depth + 1)
                let isDirectory = depth < segments.Length - 1
                let siblings = childrenOf parent

                if not (siblings.ContainsKey segments[depth]) then
                    siblings[segments[depth]] <-
                        { Name = segments[depth]
                          Path = current
                          IsDirectory = isDirectory
                          Size = if isDirectory then 0L else size }

    member _.Children(directory: string) : ArchiveChild list =
        match children.TryGetValue(directory.Trim('/')) with
        | true, entries -> List.ofSeq entries.Values
        | false, _ -> []

    /// The file's bytes, or None when the archive has no such file.
    member _.TryRead(path: string) = read path |> Option.ofObj

module Archive =
    /// One canonical spelling per entry: forward slashes, no leading "/" or "./", no empty segments.
    let private normalise (name: string) =
        name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> Array.filter (fun segment -> segment <> ".")
        |> String.concat "/"

    let private copyToArray (stream: Stream) =
        use buffer = new MemoryStream()
        stream.CopyTo buffer
        buffer.ToArray()

    let ofZip (bytes: byte array) =
        let files = Dictionary<string, byte array>(StringComparer.Ordinal)

        do
            use zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)

            for entry in zip.Entries do
                let path = normalise entry.FullName

                if not (entry.FullName.EndsWith "/") && path <> "" then
                    use content = entry.Open()
                    files[path] <- copyToArray content

        new ArchiveIndex(
            files |> Seq.map (fun pair -> pair.Key, int64 pair.Value.Length),
            fun path ->
                match files.TryGetValue path with
                | true, content -> content
                | false, _ -> null
        )

    let ofTarGz (bytes: byte array) =
        let files = Dictionary<string, byte array>(StringComparer.Ordinal)

        do
            use gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)
            use reader = new TarReader(gzip)
            let mutable entry = reader.GetNextEntry(copyData = false)

            while not (isNull entry) do
                match entry.EntryType with
                | TarEntryType.RegularFile
                | TarEntryType.V7RegularFile
                | TarEntryType.ContiguousFile when not (isNull entry.DataStream) ->
                    let path = normalise entry.Name
                    if path <> "" then files[path] <- copyToArray entry.DataStream
                | _ -> ()

                entry <- reader.GetNextEntry(copyData = false)

        new ArchiveIndex(
            files |> Seq.map (fun pair -> pair.Key, int64 pair.Value.Length),
            fun path ->
                match files.TryGetValue path with
                | true, content -> content
                | false, _ -> null
        )

    let decode (kind: ArchiveKind) (bytes: byte array) =
        match kind with
        | Zip -> ofZip bytes
        | TarGz -> ofTarGz bytes
