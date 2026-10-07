namespace LogDug.Files

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Text
open System.Threading.Tasks
open Axial
open Axial.FileSystem

/// Archive-transparent file access as an Axial service. The interface holds only the primitives that need
/// platform access or shared state; everything else (`readText`, `lines`, `walk`, ...) is composed from them
/// in the `Files` module, so a test fake implements three members.
type IFiles =
    /// The immediate children of a folder, an archive, or a folder inside an archive.
    abstract Children: Node -> Flow<unit, FilesError, Node list>
    /// Opens a file for reading, wherever it lives. The caller owns the stream.
    abstract OpenRead: Location -> Flow<unit, FilesError, Stream>
    /// Changes to the file or folder on disk that holds the location, for as long as the stream runs.
    abstract Watch: Location -> FlowStream<unit, FilesError, FileChange>

type IHasFiles =
    abstract Files: IFiles

/// A read-only stream that replays bytes already read from the front of another stream. It lets `reader`
/// sniff for binary content without reading the whole file or opening it twice.
[<Sealed>]
type private PrefixedStream(prefix: byte array, prefixLength: int, rest: Stream) =
    inherit Stream()
    let mutable position = 0

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())
    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())
    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

    override _.Read(buffer: byte array, offset: int, count: int) =
        if position < prefixLength then
            let copied = min count (prefixLength - position)
            Buffer.BlockCopy(prefix, position, buffer, offset, copied)
            position <- position + copied
            copied
        else
            rest.Read(buffer, offset, count)

    override _.Dispose(disposing: bool) =
        if disposing then rest.Dispose()
        base.Dispose disposing

[<RequireQualifiedAccess>]
module Files =
    let service<'env, 'error when 'env :> IHasFiles> : Flow<'env, 'error, IFiles> = Flow.envWith _.Files

    let private call (operation: IFiles -> Flow<unit, FilesError, 'value>) : Flow<'env, FilesError, 'value> when 'env :> IHasFiles =
        flow {
            let! files = service
            return! operation files |> Flow.localEnv ignore
        }

    let children (node: Node) : Flow<'env, FilesError, Node list> when 'env :> IHasFiles =
        call _.Children(node)

    let openRead (location: Location) : Flow<'env, FilesError, Stream> when 'env :> IHasFiles =
        call _.OpenRead(location)

    let private sniffLength = 8192

    /// A file is binary when its first 8 KB contain a NUL byte, which text encodings never produce
    /// apart from UTF-16, whose byte-order mark is checked first.
    let isBinary (bytes: byte array) (length: int) =
        let utf16 =
            length >= 2 && ((bytes[0] = 0xFFuy && bytes[1] = 0xFEuy) || (bytes[0] = 0xFEuy && bytes[1] = 0xFFuy))

        not utf16 && Array.IndexOf(bytes, 0uy, 0, length) >= 0

    let private openDecoded (node: Node) (raw: Stream) : TextReader option =
        let decoded: Stream =
            if Location.isGzipFile node.Name then new GZipStream(raw, CompressionMode.Decompress) else raw

        let prefix = Array.zeroCreate<byte> sniffLength
        let mutable length = 0
        let mutable reading = true

        while reading && length < sniffLength do
            let read = decoded.Read(prefix, length, sniffLength - length)
            if read = 0 then reading <- false else length <- length + read

        if isBinary prefix length then
            decoded.Dispose()
            None
        else
            let stream = new PrefixedStream(prefix, length, decoded)
            Some(new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks = true) :> TextReader)

    /// A text reader over the file, gunzipping `.gz` files, or None for binary content.
    /// The reader is released when the owning scope closes.
    let reader (node: Node) : Resource<'env, FilesError, TextReader option> when 'env :> IHasFiles =
        Resource.create
            (flow {
                let! raw = openRead node.Location

                return!
                    Flow.attemptBlocking (fun _ -> openDecoded node raw)
                    |> Flow.mapError (fun error -> ArchiveFailure(Location.key node.Location, error.Message))
            })
            (fun reader _ ->
                reader |> Option.iter _.Dispose()
                Task.CompletedTask)

    /// The whole file as text, or None for binary content.
    let readText (node: Node) : Flow<'env, FilesError, string option> when 'env :> IHasFiles =
        flow {
            let! reader = Flow.scopeResource (reader node)

            return!
                match reader with
                | Some reader -> Flow.fromBlocking (fun _ -> Some(reader.ReadToEnd()))
                | None -> Flow.succeed None
        }
        |> Flow.scoped

    let private batchSize = 2048

    /// The file's lines, read lazily in batches. Stopping the stream early (for example with `take`)
    /// closes the file without reading the rest. Binary files produce no lines.
    let lines (node: Node) : FlowStream<'env, FilesError, string> when 'env :> IHasFiles =
        FlowStream.using (reader node) (fun reader ->
            match reader with
            | None -> FlowStream.empty
            | Some reader ->
                FlowStream.repeatFlow (
                    Flow.fromBlocking (fun _ ->
                        let batch = ResizeArray<string>(batchSize)
                        let mutable line = reader.ReadLine()

                        while not (isNull line) && batch.Count < batchSize - 1 do
                            batch.Add line
                            line <- reader.ReadLine()

                        if not (isNull line) then batch.Add line
                        batch.ToArray())
                )
                |> FlowStream.takeWhile (fun batch -> batch.Length > 0)
                |> FlowStream.collect FlowStream.fromSeq)

    /// Every node under `root`, depth first in tree order, descending into folders and archives for which
    /// `descend` is true. Containers that cannot be read are emitted but not entered. The walk is lazy:
    /// a folder is listed only when the consumer pulls past it.
    let walk (descend: Node -> bool) (root: Node) : FlowStream<'env, FilesError, Node> when 'env :> IHasFiles =
        FlowStream.unfoldFlow
            (fun (pending: Node list) ->
                match pending with
                | [] -> Flow.succeed None
                | node :: rest when Node.isContainer node && descend node ->
                    children node
                    |> Flow.fold (fun found -> Flow.succeed (Some(node, found @ rest))) (fun _ -> Flow.succeed (Some(node, rest)))
                | node :: rest -> Flow.succeed (Some(node, rest)))
            [ root ]

    /// Changes to the file on disk that holds `node` (for archive contents, the outermost archive).
    let watch (node: Node) : FlowStream<'env, FilesError, FileChange> when 'env :> IHasFiles =
        FlowStream.fromFlow service
        |> FlowStream.collect (fun files -> files.Watch node.Location |> FlowStream.localEnv ignore)

    // ---- the live service -------------------------------------------------------------------------------

    type private ArchiveKey = { Version: string; Location: Location }

    let private archiveCapacity = 12

    let private fileSystemFailure (path: string) (error: exn) =
        FileSystemFailure(FileSystemError.describe (FileSystemError.fromException (Some path) error))

    let private watcher (fileSystem: IFileSystem) (location: Location) : FlowStream<unit, FilesError, FileChange> =
        let path = Location.diskPath location

        let acquire =
            flow {
                let! (changes: Queue<FileChange>) = Queue.sliding 256

                let! watcher =
                    Flow.attemptBlocking (fun _ ->
                        let isFolder = fileSystem.DirectoryExists path

                        let folder =
                            if isFolder then path
                            else fileSystem.GetDirectoryName path |> Option.defaultValue path

                        let watcher = new FileSystemWatcher(folder)
                        if not isFolder then watcher.Filter <- Location.fileName path
                        watcher.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.Size ||| NotifyFilters.FileName

                        let offer kind (args: FileSystemEventArgs) =
                            Queue.tryOffer { Path = args.FullPath; Kind = kind } changes |> ignore

                        watcher.Changed.Add(offer Changed)
                        watcher.Created.Add(offer Created)
                        watcher.Deleted.Add(offer Deleted)
                        watcher.Renamed.Add(fun args -> offer Renamed args)
                        watcher.EnableRaisingEvents <- true
                        watcher)
                    |> Flow.mapError (fun _ -> WatchUnsupported path)

                return changes, watcher
            }

        FlowStream.using
            (Resource.create acquire (fun (_, watcher) _ ->
                watcher.Dispose()
                Task.CompletedTask))
            (fun (changes, _) -> FlowStream.fromDequeue changes)

    /// Builds the live service over the platform file system. Decoded archives are shared through an Axial
    /// `Cache` owned by the scope this flow runs in, so concurrent requests for one archive decode it once.
    /// Run it in a long-lived scope, such as an application root.
    let make<'env when 'env :> IHasFileSystem and 'env :> IHasClock> : Flow<'env, Never, IFiles> =
        flow {
            let! fileSystem = FileSystem.service
            let archives: Cache<ArchiveKey, FilesError, ArchiveIndex> option ref = ref None
            let recent = LinkedList<ArchiveKey>()

            let disk path (operation: unit -> 'value) : Flow<'any, FilesError, 'value> =
                Flow.attemptBlocking (fun _ -> operation ()) |> Flow.mapError (fileSystemFailure path)

            let rec openRead (location: Location) : Flow<unit, FilesError, Stream> =
                match location with
                | Disk path -> disk path (fun () -> fileSystem.OpenRead path)
                | Entry(archive, entryPath) ->
                    flow {
                        let! index = archiveAt archive

                        match index.TryRead entryPath with
                        | Some bytes -> return new MemoryStream(bytes, false) :> Stream
                        | None -> return! Flow.fail (MissingEntry(Location.key location))
                    }

            and archiveAt (location: Location) : Flow<unit, FilesError, ArchiveIndex> =
                flow {
                    let path = Location.diskPath location
                    let! stamp = disk path (fun () -> fileSystem.GetFileLastWriteTimeUtc path)
                    let key = { Version = $"{path}|{stamp.Ticks}"; Location = location }
                    let cache = archives.Value.Value

                    // Keep the most recently used archives; forget the oldest beyond the capacity.
                    let evicted =
                        lock recent (fun () ->
                            recent.Remove key |> ignore
                            recent.AddFirst key |> ignore

                            [ while recent.Count > archiveCapacity do
                                  let oldest = recent.Last.Value
                                  recent.RemoveLast()
                                  oldest ])

                    for key in evicted do
                        do! Cache.invalidate key cache

                    return! Cache.get key cache
                }

            let decode (key: ArchiveKey) : Flow<'env, FilesError, ArchiveIndex> =
                let name =
                    match key.Location with
                    | Disk path -> Location.fileName path
                    | Entry(_, entryPath) -> entryPath

                flow {
                    let! stream = openRead key.Location

                    let! bytes =
                        Flow.attemptBlocking (fun _ ->
                            use stream = stream
                            use buffer = new MemoryStream()
                            stream.CopyTo buffer
                            buffer.ToArray())
                        |> Flow.mapError (fileSystemFailure (Location.key key.Location))

                    match Location.archiveKind name with
                    | Some kind ->
                        return!
                            Flow.attemptBlocking (fun _ -> Archive.decode kind bytes)
                            |> Flow.mapError (fun error -> ArchiveFailure(Location.key key.Location, error.Message))
                    | None -> return! Flow.fail (ArchiveFailure(Location.key key.Location, "not a supported archive"))
                }
                |> Flow.localEnv ignore

            let! cache = Cache.make decode
            archives.Value <- Some cache

            let diskChildren (path: string) =
                disk path (fun () ->
                    fileSystem.GetFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly)
                    |> Seq.choose (fun entry ->
                        try
                            let isDirectory = fileSystem.DirectoryExists entry
                            let size = if isDirectory then 0L else fileSystem.GetFileLength entry
                            Some(Node.ofName (Disk entry) (Location.fileName entry) size isDirectory)
                        with _ ->
                            None)
                    |> Node.sort)

            let archiveChildren (archive: Location) (directory: string) =
                archiveAt archive
                |> Flow.map (fun index ->
                    index.Children directory
                    |> Seq.map (fun child -> Node.ofName (Entry(archive, child.Path)) child.Name child.Size child.IsDirectory)
                    |> Node.sort)

            return
                { new IFiles with
                    member _.Children node =
                        match node.Kind, node.Location with
                        | NodeKind.Folder, Disk path -> diskChildren path
                        | NodeKind.Folder, Entry(archive, directory) -> archiveChildren archive directory
                        | NodeKind.Archive _, location -> archiveChildren location ""
                        | NodeKind.File, _ -> Flow.succeed []

                    member _.OpenRead location = openRead location
                    member _.Watch location = watcher fileSystem location }
        }
