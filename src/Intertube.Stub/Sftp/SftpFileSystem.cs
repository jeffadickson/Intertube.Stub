namespace Intertube.Stub.Sftp;

/// <summary>SFTP version 3 status codes.</summary>
public enum SftpStatus : uint
{
    Ok = 0,
    Eof = 1,
    NoSuchFile = 2,
    PermissionDenied = 3,
    Failure = 4,
    BadMessage = 5,
    NoConnection = 6,
    ConnectionLost = 7,
    OperationUnsupported = 8,
}

/// <summary>Thrown by an <see cref="ISftpFileSystem"/> to send the client a specific status.</summary>
public sealed class SftpException(SftpStatus status, string message) : IOException(message)
{
    public SftpStatus Status { get; } = status;
}

/// <summary>File attributes as SFTP carries them. Null means "not present".</summary>
public sealed record SftpAttributes
{
    /// <summary><c>S_IFDIR</c>, the directory bit of <see cref="Permissions"/>.</summary>
    public const uint DirectoryType = 0x4000;

    /// <summary><c>S_IFREG</c>, the regular-file bit of <see cref="Permissions"/>.</summary>
    public const uint FileType = 0x8000;

    public long? Size { get; init; }

    public uint? UserId { get; init; }

    public uint? GroupId { get; init; }

    /// <summary>Unix mode including the file type bits, e.g. <c>0x81A4</c> (regular file, 0644).</summary>
    public uint? Permissions { get; init; }

    public DateTimeOffset? LastAccessTime { get; init; }

    public DateTimeOffset? LastWriteTime { get; init; }

    public bool IsDirectory => (Permissions & 0xF000) == DirectoryType;

    /// <summary>Attributes of a regular file with mode 0644.</summary>
    public static SftpAttributes ForFile(long size, DateTimeOffset modified) =>
        new() { Size = size, Permissions = FileType | 0x1A4, UserId = 1000, GroupId = 1000, LastAccessTime = modified, LastWriteTime = modified };

    /// <summary>Attributes of a directory with mode 0755.</summary>
    public static SftpAttributes ForDirectory(DateTimeOffset modified) =>
        new() { Size = 0, Permissions = DirectoryType | 0x1ED, UserId = 1000, GroupId = 1000, LastAccessTime = modified, LastWriteTime = modified };
}

/// <summary>One entry of a directory listing.</summary>
public sealed record SftpDirectoryEntry(string Name, SftpAttributes Attributes);

/// <summary>
/// What an <see cref="SftpServer"/> serves. Paths are absolute and normalised (<c>/</c>,
/// <c>/incoming/a.txt</c>); the server resolves <c>.</c>, <c>..</c> and relative paths first.
/// Throw <see cref="SftpException"/> for a specific status; <see cref="FileNotFoundException"/>,
/// <see cref="DirectoryNotFoundException"/> and <see cref="UnauthorizedAccessException"/> are mapped too.
/// </summary>
public interface ISftpFileSystem
{
    SftpAttributes GetAttributes(string path);

    IEnumerable<SftpDirectoryEntry> ListDirectory(string path);

    /// <summary>Open a file. The stream must be seekable; the server reads and writes at explicit offsets.</summary>
    Stream OpenFile(string path, FileMode mode, FileAccess access);

    /// <summary>Apply whichever attributes are present (size truncates or extends).</summary>
    void SetAttributes(string path, SftpAttributes attributes);

    void CreateDirectory(string path);

    void DeleteFile(string path);

    void DeleteDirectory(string path);

    void Rename(string source, string destination, bool overwrite);
}

/// <summary>Helpers for the server's absolute, slash-separated paths.</summary>
public static class SftpPath
{
    /// <summary>Resolve <paramref name="path"/> against <c>/</c>, removing <c>.</c>, <c>..</c> and repeated slashes. Never escapes the root.</summary>
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>The parent directory of a normalised path; <c>/</c> is its own parent.</summary>
    public static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    /// <summary>The last segment of a normalised path.</summary>
    public static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static string Combine(string directory, string name) => directory == "/" ? "/" + name : directory + "/" + name;
}

/// <summary>
/// Serves a real directory: <c>/</c> is <paramref name="root"/> and nothing outside it is reachable.
/// The SFTP mirror of the classic "temp directory as remote" fixture.
/// </summary>
public sealed class DirectorySftpFileSystem(string root) : ISftpFileSystem
{
    /// <summary>The directory served as <c>/</c>.</summary>
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>The local path for a server path.</summary>
    public string LocalPath(string path)
    {
        var local = Path.GetFullPath(Path.Combine(Root, SftpPath.Normalize(path).TrimStart('/')));
        var inside = local.Equals(Root, StringComparison.OrdinalIgnoreCase)
            || local.StartsWith(Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return inside ? local : throw new SftpException(SftpStatus.PermissionDenied, "Path outside the served root.");
    }

    public SftpAttributes GetAttributes(string path)
    {
        var local = LocalPath(path);
        if (Directory.Exists(local)) return Attributes(new DirectoryInfo(local));
        if (File.Exists(local)) return Attributes(new FileInfo(local));
        throw new SftpException(SftpStatus.NoSuchFile, "No such file.");
    }

    public IEnumerable<SftpDirectoryEntry> ListDirectory(string path) =>
        new DirectoryInfo(LocalPath(path)).EnumerateFileSystemInfos()
            .Select(info => new SftpDirectoryEntry(info.Name, Attributes(info)))
            .ToList();

    public Stream OpenFile(string path, FileMode mode, FileAccess access)
    {
        var local = LocalPath(path);
        if (Directory.Exists(local)) throw new SftpException(SftpStatus.Failure, "Is a directory.");
        return new FileStream(local, mode, access, FileShare.ReadWrite | FileShare.Delete);
    }

    public void SetAttributes(string path, SftpAttributes attributes)
    {
        var local = LocalPath(path);
        if (!File.Exists(local) && !Directory.Exists(local)) throw new SftpException(SftpStatus.NoSuchFile, "No such file.");
        if (attributes.Size is { } size)
        {
            using var stream = new FileStream(local, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.SetLength(size);
        }
        if (attributes.LastWriteTime is { } modified) File.SetLastWriteTimeUtc(local, modified.UtcDateTime);
        if (attributes.LastAccessTime is { } accessed) File.SetLastAccessTimeUtc(local, accessed.UtcDateTime);
        if (attributes.Permissions is { } mode && !OperatingSystem.IsWindows()) File.SetUnixFileMode(local, (UnixFileMode)(mode & 0xFFF));
    }

    public void CreateDirectory(string path)
    {
        var local = LocalPath(path);
        if (Directory.Exists(local) || File.Exists(local)) throw new SftpException(SftpStatus.Failure, "Already exists.");
        if (!Directory.Exists(Path.GetDirectoryName(local))) throw new SftpException(SftpStatus.NoSuchFile, "No such directory.");
        Directory.CreateDirectory(local);
    }

    public void DeleteFile(string path)
    {
        var local = LocalPath(path);
        if (!File.Exists(local)) throw new SftpException(SftpStatus.NoSuchFile, "No such file.");
        File.Delete(local);
    }

    public void DeleteDirectory(string path)
    {
        var local = LocalPath(path);
        if (!Directory.Exists(local)) throw new SftpException(SftpStatus.NoSuchFile, "No such directory.");
        if (Directory.EnumerateFileSystemEntries(local).Any()) throw new SftpException(SftpStatus.Failure, "Directory not empty.");
        Directory.Delete(local);
    }

    public void Rename(string source, string destination, bool overwrite)
    {
        var from = LocalPath(source);
        var to = LocalPath(destination);
        if (Directory.Exists(from))
        {
            if (Directory.Exists(to) || File.Exists(to)) throw new SftpException(SftpStatus.Failure, "Destination exists.");
            Directory.Move(from, to);
            return;
        }
        if (!File.Exists(from)) throw new SftpException(SftpStatus.NoSuchFile, "No such file.");
        if (!overwrite && (File.Exists(to) || Directory.Exists(to))) throw new SftpException(SftpStatus.Failure, "Destination exists.");
        File.Move(from, to, overwrite);
    }

    private static SftpAttributes Attributes(FileSystemInfo info)
    {
        var attributes = info is DirectoryInfo
            ? SftpAttributes.ForDirectory(info.LastWriteTimeUtc)
            : SftpAttributes.ForFile(((FileInfo)info).Length, info.LastWriteTimeUtc);
        attributes = attributes with { LastAccessTime = info.LastAccessTimeUtc };
        if (!OperatingSystem.IsWindows())
            attributes = attributes with { Permissions = (attributes.Permissions & 0xF000) | (uint)info.UnixFileMode };
        return attributes;
    }
}

/// <summary>
/// A thread-safe in-memory file system, so a test needs no temporary directory and can seed and
/// inspect files directly. Starts with just <c>/</c>.
/// </summary>
public sealed class InMemorySftpFileSystem : ISftpFileSystem
{
    private readonly object _lock = new();
    private readonly Node _root = Node.Directory();

    /// <summary>Clock for timestamps; replace it for deterministic listings.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Create or replace a file, creating parent directories as needed.</summary>
    public InMemorySftpFileSystem AddFile(string path, ReadOnlySpan<byte> content)
    {
        lock (_lock)
        {
            path = SftpPath.Normalize(path);
            var parent = EnsureDirectory(SftpPath.Parent(path));
            var file = Node.File(Clock());
            file.Content.Write(content);
            parent.Children[SftpPath.Name(path)] = file;
        }
        return this;
    }

    /// <summary>Create or replace a UTF-8 text file, creating parent directories as needed.</summary>
    public InMemorySftpFileSystem AddFile(string path, string text) => AddFile(path, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Create a directory and any missing parents.</summary>
    public InMemorySftpFileSystem AddDirectory(string path)
    {
        lock (_lock) EnsureDirectory(SftpPath.Normalize(path));
        return this;
    }

    public bool FileExists(string path)
    {
        lock (_lock) return Find(SftpPath.Normalize(path)) is { IsDirectory: false };
    }

    public bool DirectoryExists(string path)
    {
        lock (_lock) return Find(SftpPath.Normalize(path)) is { IsDirectory: true };
    }

    public byte[] ReadAllBytes(string path)
    {
        lock (_lock) return FileNode(SftpPath.Normalize(path)).Content.ToArray();
    }

    public string ReadAllText(string path) => System.Text.Encoding.UTF8.GetString(ReadAllBytes(path));

    /// <summary>Every file path, sorted.</summary>
    public IReadOnlyList<string> AllFiles
    {
        get
        {
            lock (_lock)
            {
                var result = new List<string>();
                Walk("/", _root);
                result.Sort(StringComparer.Ordinal);
                return result;

                void Walk(string path, Node node)
                {
                    foreach (var (name, child) in node.Children)
                    {
                        var childPath = SftpPath.Combine(path, name);
                        if (child.IsDirectory) Walk(childPath, child);
                        else result.Add(childPath);
                    }
                }
            }
        }
    }

    public SftpAttributes GetAttributes(string path)
    {
        lock (_lock) return Attributes(Find(path) ?? throw NotFound());
    }

    public IEnumerable<SftpDirectoryEntry> ListDirectory(string path)
    {
        lock (_lock)
        {
            var node = Find(path) ?? throw NotFound();
            if (!node.IsDirectory) throw new SftpException(SftpStatus.Failure, "Not a directory.");
            return node.Children.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new SftpDirectoryEntry(c.Key, Attributes(c.Value))).ToList();
        }
    }

    public Stream OpenFile(string path, FileMode mode, FileAccess access)
    {
        lock (_lock)
        {
            var parent = Find(SftpPath.Parent(path));
            if (parent is not { IsDirectory: true }) throw NotFound();
            var name = SftpPath.Name(path);
            parent.Children.TryGetValue(name, out var node);
            if (node is { IsDirectory: true }) throw new SftpException(SftpStatus.Failure, "Is a directory.");
            switch (mode)
            {
                case FileMode.CreateNew when node is not null:
                    throw new SftpException(SftpStatus.Failure, "File exists.");
                case FileMode.Open or FileMode.Truncate when node is null:
                    throw NotFound();
                case FileMode.Create or FileMode.Truncate:
                    node ??= parent.Children[name] = Node.File(Clock());
                    node.Content.SetLength(0);
                    node.Modified = Clock();
                    break;
                default:
                    node ??= parent.Children[name] = Node.File(Clock());
                    break;
            }
            return new NodeStream(this, node, access);
        }
    }

    public void SetAttributes(string path, SftpAttributes attributes)
    {
        lock (_lock)
        {
            var node = Find(path) ?? throw NotFound();
            if (attributes.Size is { } size && !node.IsDirectory) node.Content.SetLength(size);
            if (attributes.Permissions is { } mode) node.Mode = mode & 0xFFF;
            if (attributes.LastWriteTime is { } modified) node.Modified = modified;
        }
    }

    public void CreateDirectory(string path)
    {
        lock (_lock)
        {
            var parent = Find(SftpPath.Parent(path));
            if (parent is not { IsDirectory: true }) throw NotFound();
            if (!parent.Children.TryAdd(SftpPath.Name(path), Node.Directory(Clock())))
                throw new SftpException(SftpStatus.Failure, "Already exists.");
        }
    }

    public void DeleteFile(string path)
    {
        lock (_lock)
        {
            var parent = Find(SftpPath.Parent(path));
            if (parent is null || !parent.Children.TryGetValue(SftpPath.Name(path), out var node) || node.IsDirectory) throw NotFound();
            parent.Children.Remove(SftpPath.Name(path));
        }
    }

    public void DeleteDirectory(string path)
    {
        lock (_lock)
        {
            if (path == "/") throw new SftpException(SftpStatus.PermissionDenied, "Cannot remove the root.");
            var parent = Find(SftpPath.Parent(path));
            if (parent is null || !parent.Children.TryGetValue(SftpPath.Name(path), out var node) || !node.IsDirectory) throw NotFound();
            if (node.Children.Count > 0) throw new SftpException(SftpStatus.Failure, "Directory not empty.");
            parent.Children.Remove(SftpPath.Name(path));
        }
    }

    public void Rename(string source, string destination, bool overwrite)
    {
        lock (_lock)
        {
            var fromParent = Find(SftpPath.Parent(source));
            if (fromParent is null || !fromParent.Children.TryGetValue(SftpPath.Name(source), out var node)) throw NotFound();
            var toParent = Find(SftpPath.Parent(destination));
            if (toParent is not { IsDirectory: true }) throw NotFound();
            if (destination == source || destination.StartsWith(source + "/", StringComparison.Ordinal))
                throw new SftpException(SftpStatus.Failure, "Cannot move a directory into itself.");
            if (toParent.Children.TryGetValue(SftpPath.Name(destination), out var existing) && (!overwrite || existing.IsDirectory || node.IsDirectory))
                throw new SftpException(SftpStatus.Failure, "Destination exists.");
            fromParent.Children.Remove(SftpPath.Name(source));
            toParent.Children[SftpPath.Name(destination)] = node;
        }
    }

    private Node? Find(string path)
    {
        var node = _root;
        foreach (var part in SftpPath.Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (!node.IsDirectory || !node.Children.TryGetValue(part, out node)) return null;
        return node;
    }

    private Node FileNode(string path) =>
        Find(path) is { IsDirectory: false } node ? node : throw new FileNotFoundException($"No file at {path}.", path);

    private Node EnsureDirectory(string path)
    {
        var node = _root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Children.TryGetValue(part, out var child)) node.Children[part] = child = Node.Directory(Clock());
            if (!child.IsDirectory) throw new IOException($"{part} in {path} is a file.");
            node = child;
        }
        return node;
    }

    private static SftpAttributes Attributes(Node node) => node.IsDirectory
        ? SftpAttributes.ForDirectory(node.Modified) with { Permissions = SftpAttributes.DirectoryType | node.Mode }
        : SftpAttributes.ForFile(node.Content.Length, node.Modified) with { Permissions = SftpAttributes.FileType | node.Mode };

    private static SftpException NotFound() => new(SftpStatus.NoSuchFile, "No such file.");

    private sealed class Node
    {
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);

        public MemoryStream Content { get; } = new();

        public bool IsDirectory { get; private init; }

        public uint Mode { get; set; }

        public DateTimeOffset Modified { get; set; }

        public static Node Directory(DateTimeOffset modified = default) => new() { IsDirectory = true, Mode = 0x1ED, Modified = modified };

        public static Node File(DateTimeOffset modified) => new() { Mode = 0x1A4, Modified = modified };
    }

    /// <summary>A private cursor over a node's shared content.</summary>
    private sealed class NodeStream(InMemorySftpFileSystem owner, Node node, FileAccess access) : Stream
    {
        public override bool CanRead => access.HasFlag(FileAccess.Read);

        public override bool CanWrite => access.HasFlag(FileAccess.Write);

        public override bool CanSeek => true;

        public override long Length
        {
            get
            {
                lock (owner._lock) return node.Content.Length;
            }
        }

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!CanRead) throw new SftpException(SftpStatus.PermissionDenied, "Not opened for reading.");
            lock (owner._lock)
            {
                node.Content.Position = Position;
                var read = node.Content.Read(buffer, offset, count);
                Position += read;
                return read;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!CanWrite) throw new SftpException(SftpStatus.PermissionDenied, "Not opened for writing.");
            lock (owner._lock)
            {
                node.Content.Position = Position;
                node.Content.Write(buffer, offset, count);
                Position += count;
                node.Modified = owner.Clock();
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => Length + offset,
        };

        public override void SetLength(long value)
        {
            lock (owner._lock) node.Content.SetLength(value);
        }

        public override void Flush()
        {
        }
    }
}
