using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;

namespace Intertube.Stub.Sftp.Ssh;

/// <summary>
/// The SFTP version 3 subsystem (draft-ietf-secsh-filexfer-02) on one channel, plus the OpenSSH
/// extensions clients rely on: posix-rename, fsync and limits.
/// </summary>
internal sealed class SftpSession(SftpServer server, Connection connection, SshChannel channel, string user, CancellationToken lifetime)
{
    private const int MaxPacket = 256 * 1024 + 1024;
    private const int MaxRead = 255 * 1024;
    private const int MaxOpenHandles = 1024;

    private readonly Dictionary<string, Handle> _handles = [];
    private uint _nextHandle;

    private ISftpFileSystem Files => server.FileSystem;

    public async Task RunAsync()
    {
        var token = lifetime;
        var input = channel.Inbound.Reader;
        try
        {
            while (true)
            {
                var result = await input.ReadAsync(token).ConfigureAwait(false);
                var buffer = result.Buffer;
                if (TryTakePacket(ref buffer, out var packet))
                {
                    input.AdvanceTo(buffer.Start);
                    await HandleAsync(packet, token).ConfigureAwait(false);
                    await channel.ConsumedAsync(4 + packet.Length, token).ConfigureAwait(false);
                    continue;
                }
                if (result.IsCompleted) break;
                input.AdvanceTo(buffer.Start, buffer.End);
            }
            await channel.CloseAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException || (ex is OperationCanceledException && token.IsCancellationRequested))
        {
            // The connection or channel went away mid-request.
        }
        catch (Exception ex)
        {
            // Bad framing, or a bug: close the channel so the client is not left waiting. Bugs are rethrown to reach the server's errors.
            connection.Transcript.RecordMessage(TranscriptDirection.Event, $"SFTP subsystem stopped: {ex.Message}");
            try
            {
                await channel.CloseAsync(token).ConfigureAwait(false);
            }
            catch (Exception closeError) when (closeError is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
            if (ex is not SshProtocolException) throw;
        }
        finally
        {
            foreach (var handle in _handles.Values) handle.Dispose();
            _handles.Clear();
            await input.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static bool TryTakePacket(ref ReadOnlySequence<byte> buffer, out byte[] packet)
    {
        packet = [];
        if (buffer.Length < 4) return false;
        Span<byte> header = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaxPacket) throw new SshProtocolException($"Bad SFTP packet length {length}.");
        if (buffer.Length < 4 + length) return false;
        packet = buffer.Slice(4, length).ToArray();
        buffer = buffer.Slice(4 + length);
        return true;
    }

    // ---- Requests ------------------------------------------------------------------------------

    private const byte Init = 1, Version = 2, Open = 3, Close = 4, Read = 5, Write = 6, LStat = 7, FStat = 8, SetStat = 9,
        FSetStat = 10, OpenDir = 11, ReadDir = 12, Remove = 13, MkDir = 14, RmDir = 15, RealPath = 16, Stat = 17, Rename = 18,
        ReadLink = 19, Symlink = 20, Status = 101, HandleReply = 102, Data = 103, Name = 104, Attrs = 105, Extended = 200, ExtendedReply = 201;

    private const uint FlagRead = 1, FlagWrite = 2, FlagAppend = 4, FlagCreate = 8, FlagTruncate = 0x10, FlagExclusive = 0x20;

    /// <summary>A request's fields, parsed up front because a reader cannot live across an await.</summary>
    private sealed record Request(byte Type, uint Id)
    {
        public string Path { get; init; } = "";
        public string? Target { get; init; }
        public string? HandleId { get; init; }
        public ulong Offset { get; init; }
        public uint Length { get; init; }
        public ReadOnlyMemory<byte> Data { get; init; }
        public uint Flags { get; init; }
        public SftpAttributes? Attributes { get; init; }
        public string? ExtensionName { get; init; }
    }

    private async Task HandleAsync(byte[] packet, CancellationToken token)
    {
        if (packet[0] == Init)
        {
            var reply = new SshWriter(Version).UInt32(3)
                .String("posix-rename@openssh.com").String("1")
                .String("fsync@openssh.com").String("1")
                .String("limits@openssh.com").String("1");
            await SendAsync(reply, token).ConfigureAwait(false);
            return;
        }

        Request request;
        try
        {
            request = Parse(packet);
        }
        catch (Exception ex)
        {
            var id = packet.Length >= 5 ? BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(1)) : 0;
            await StatusAsync(id, SftpStatus.BadMessage, ex.Message, token).ConfigureAwait(false);
            return;
        }

        try
        {
            await DispatchAsync(request, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ToStatus(ex) is { } status)
        {
            await StatusAsync(request.Id, status, ex.Message, token).ConfigureAwait(false);
        }
    }

    private static Request Parse(byte[] packet)
    {
        var r = new SshReader(packet);
        var type = r.Byte();
        var request = new Request(type, r.UInt32());
        switch (type)
        {
            case Open:
                return request with { Path = Normalize(r.Utf8()), Flags = r.UInt32(), Attributes = ReadAttributes(ref r) };
            case Close or FStat or ReadDir:
                return request with { HandleId = HandleKey(r.String()) };
            case Read:
                return request with { HandleId = HandleKey(r.String()), Offset = r.UInt64(), Length = r.UInt32() };
            case Write:
            {
                var handle = HandleKey(r.String());
                var offset = r.UInt64();
                var length = r.UInt32();
                var start = packet.Length - r.Remaining;
                if (length > (uint)r.Remaining) throw new SshProtocolException("WRITE data is shorter than its length.");
                return request with { HandleId = handle, Offset = offset, Data = packet.AsMemory(start, (int)length) };
            }
            case FSetStat:
                return request with { HandleId = HandleKey(r.String()), Attributes = ReadAttributes(ref r) };
            case SetStat or MkDir:
                return request with { Path = Normalize(r.Utf8()), Attributes = ReadAttributes(ref r) };
            case LStat or Stat or OpenDir or Remove or RmDir or RealPath or ReadLink:
                return request with { Path = Normalize(r.Utf8()) };
            case Rename or Symlink:
                return request with { Path = Normalize(r.Utf8()), Target = Normalize(r.Utf8()) };
            case Extended:
            {
                var name = r.Utf8();
                return name switch
                {
                    "posix-rename@openssh.com" => request with { ExtensionName = name, Path = Normalize(r.Utf8()), Target = Normalize(r.Utf8()) },
                    "fsync@openssh.com" => request with { ExtensionName = name, HandleId = HandleKey(r.String()) },
                    _ => request with { ExtensionName = name, Path = name },
                };
            }
            default:
                return request;
        }
    }

    private async Task DispatchAsync(Request request, CancellationToken token)
    {
        var id = request.Id;
        switch (request.Type)
        {
            case Open:
            {
                if (await AdmitAsync(SftpOperation.Open, request.Path, request, token).ConfigureAwait(false)) return;
                if (_handles.Count >= MaxOpenHandles) throw new SftpException(SftpStatus.Failure, "Too many open handles.");
                var flags = request.Flags;
                var access = (flags & (FlagRead | FlagWrite)) switch
                {
                    FlagRead | FlagWrite => FileAccess.ReadWrite,
                    FlagWrite => FileAccess.Write,
                    _ => FileAccess.Read,
                };
                if ((flags & FlagAppend) != 0) access |= FileAccess.Write;
                var mode = (flags & FlagCreate) == 0
                    ? (flags & FlagTruncate) != 0 ? FileMode.Truncate : FileMode.Open
                    : (flags & FlagExclusive) != 0 ? FileMode.CreateNew
                    : (flags & FlagTruncate) != 0 ? FileMode.Create
                    : FileMode.OpenOrCreate;
                // .NET will not create or truncate without write access; the handle still only grants what was asked.
                var open = mode is FileMode.CreateNew or FileMode.Create or FileMode.Truncate ? access | FileAccess.Write : access;
                var stream = Files.OpenFile(request.Path, mode, open);
                await SendHandleAsync(id, new FileHandle(request.Path, stream, access, (flags & FlagAppend) != 0), token).ConfigureAwait(false);
                return;
            }

            case Close:
            {
                var handle = Lookup<Handle>(request.HandleId);
                if (await AdmitAsync(SftpOperation.Close, handle.Path, request, token).ConfigureAwait(false)) return;
                _handles.Remove(request.HandleId!);
                handle.Dispose();
                await StatusAsync(id, SftpStatus.Ok, "", token).ConfigureAwait(false);
                return;
            }

            case Read:
            {
                var handle = Lookup<FileHandle>(request.HandleId);
                var length = (int)Math.Min(request.Length, MaxRead);
                if (await AdmitAsync(SftpOperation.Read, handle.Path, request with { Length = (uint)length }, token).ConfigureAwait(false)) return;
                if (!handle.Access.HasFlag(FileAccess.Read)) throw new SftpException(SftpStatus.PermissionDenied, "Not opened for reading.");
                if (request.Offset >= (ulong)handle.Stream.Length)
                {
                    await StatusAsync(id, SftpStatus.Eof, "End of file.", token).ConfigureAwait(false);
                    return;
                }
                var buffer = new byte[length];
                handle.Stream.Position = (long)request.Offset;
                var filled = 0;
                while (filled < length)
                {
                    var read = handle.Stream.Read(buffer, filled, length - filled);
                    if (read == 0) break;
                    filled += read;
                }
                if (filled == 0 && length > 0)
                    await StatusAsync(id, SftpStatus.Eof, "End of file.", token).ConfigureAwait(false);
                else
                    await SendAsync(new SshWriter(Data, filled + 16).UInt32(id).String(buffer.AsSpan(0, filled)), token).ConfigureAwait(false);
                return;
            }

            case Write:
            {
                var handle = Lookup<FileHandle>(request.HandleId);
                if (await AdmitAsync(SftpOperation.Write, handle.Path, request, token).ConfigureAwait(false)) return;
                if (!handle.Access.HasFlag(FileAccess.Write)) throw new SftpException(SftpStatus.PermissionDenied, "Not opened for writing.");
                if (request.Offset > long.MaxValue) throw new SftpException(SftpStatus.Failure, "Offset out of range.");
                handle.Stream.Position = handle.Append ? handle.Stream.Length : (long)request.Offset;
                handle.Stream.Write(request.Data.Span);
                await StatusAsync(id, SftpStatus.Ok, "", token).ConfigureAwait(false);
                return;
            }

            case Stat or LStat:
            {
                var operation = request.Type == Stat ? SftpOperation.Stat : SftpOperation.LStat;
                if (await AdmitAsync(operation, request.Path, request, token).ConfigureAwait(false)) return;
                await SendAttributesAsync(id, Files.GetAttributes(request.Path), token).ConfigureAwait(false);
                return;
            }

            case FStat:
            {
                var handle = Lookup<Handle>(request.HandleId);
                if (await AdmitAsync(SftpOperation.FStat, handle.Path, request, token).ConfigureAwait(false)) return;
                var attributes = Files.GetAttributes(handle.Path);
                if (handle is FileHandle file) attributes = attributes with { Size = file.Stream.Length };
                await SendAttributesAsync(id, attributes, token).ConfigureAwait(false);
                return;
            }

            case SetStat or FSetStat:
            {
                var path = request.Type == SetStat ? request.Path : Lookup<Handle>(request.HandleId).Path;
                var operation = request.Type == SetStat ? SftpOperation.SetStat : SftpOperation.FSetStat;
                if (await AdmitAsync(operation, path, request, token).ConfigureAwait(false)) return;
                if (request.Type == FSetStat && request.Attributes!.Size is { } size && Lookup<Handle>(request.HandleId) is FileHandle open)
                {
                    open.Stream.SetLength(size);
                    Files.SetAttributes(path, request.Attributes with { Size = null });
                }
                else
                {
                    Files.SetAttributes(path, request.Attributes!);
                }
                await StatusAsync(id, SftpStatus.Ok, "", token).ConfigureAwait(false);
                return;
            }

            case OpenDir:
            {
                if (await AdmitAsync(SftpOperation.OpenDir, request.Path, request, token).ConfigureAwait(false)) return;
                if (!Files.GetAttributes(request.Path).IsDirectory) throw new SftpException(SftpStatus.Failure, "Not a directory.");
                var entries = new Queue<SftpDirectoryEntry>(Files.ListDirectory(request.Path));
                await SendHandleAsync(id, new DirectoryHandle(request.Path, entries), token).ConfigureAwait(false);
                return;
            }

            case ReadDir:
            {
                var handle = Lookup<DirectoryHandle>(request.HandleId);
                if (await AdmitAsync(SftpOperation.ReadDir, handle.Path, request, token).ConfigureAwait(false)) return;
                if (handle.Entries.Count == 0)
                {
                    await StatusAsync(id, SftpStatus.Eof, "End of directory.", token).ConfigureAwait(false);
                    return;
                }
                var batch = new List<SftpDirectoryEntry>();
                while (batch.Count < 100 && handle.Entries.TryDequeue(out var entry)) batch.Add(entry);
                var reply = new SshWriter(Name).UInt32(id).UInt32((uint)batch.Count);
                foreach (var entry in batch)
                {
                    reply.String(entry.Name).String(LongName(entry));
                    WriteAttributes(reply, entry.Attributes);
                }
                await SendAsync(reply, token).ConfigureAwait(false);
                return;
            }

            case Remove:
                if (await AdmitAsync(SftpOperation.Remove, request.Path, request, token).ConfigureAwait(false)) return;
                Files.DeleteFile(request.Path);
                await StatusAsync(id, SftpStatus.Ok, "", token).ConfigureAwait(false);
                return;

            case MkDir:
                if (await AdmitAsync(SftpOperation.MkDir, request.Path, request, token).ConfigureAwait(false)) return;
                Files.CreateDirectory(request.Path);
                await StatusAsync(id, SftpStatus.Ok, "", token).ConfigureAwait(false);
                return;

            case RmDir:
                if (await AdmitAsync(SftpOperation.RmDir, request.Path, request, token).ConfigureAwait(false)) return;
                Files.DeleteDirectory(request.Path);
                await StatusAsync(id, SftpStatus.Ok, "", token).ConfigureAwait(false);
                return;

            case RealPath:
            {
                if (await AdmitAsync(SftpOperation.RealPath, request.Path, request, token).ConfigureAwait(false)) return;
                var reply = new SshWriter(Name).UInt32(id).UInt32(1).String(request.Path).String(request.Path);
                WriteAttributes(reply, new SftpAttributes());
                await SendAsync(reply, token).ConfigureAwait(false);
                return;
            }

            case Rename:
                if (await AdmitAsync(SftpOperation.Rename, request.Path, request, token).ConfigureAwait(false)) return;
                Files.Rename(request.Path, request.Target!, overwrite: false);
                await StatusAsync(id, SftpStatus.Ok, "", token).ConfigureAwait(false);
                return;

            case ReadLink or Symlink:
            {
                var operation = request.Type == ReadLink ? SftpOperation.ReadLink : SftpOperation.Symlink;
                if (await AdmitAsync(operation, request.Path, request, token).ConfigureAwait(false)) return;
                await StatusAsync(id, SftpStatus.OperationUnsupported, "Symbolic links are not supported.", token).ConfigureAwait(false);
                return;
            }

            case Extended:
                await ExtendedAsync(request, token).ConfigureAwait(false);
                return;

            default:
                await StatusAsync(id, SftpStatus.OperationUnsupported, $"Unsupported request type {request.Type}.", token).ConfigureAwait(false);
                return;
        }
    }

    private async Task ExtendedAsync(Request request, CancellationToken token)
    {
        var id = request.Id;
        switch (request.ExtensionName)
        {
            case "posix-rename@openssh.com":
                if (await AdmitAsync(SftpOperation.PosixRename, request.Path, request, token).ConfigureAwait(false)) return;
                Files.Rename(request.Path, request.Target!, overwrite: true);
                await StatusAsync(id, SftpStatus.Ok, "", token).ConfigureAwait(false);
                return;

            case "fsync@openssh.com":
            {
                var handle = Lookup<FileHandle>(request.HandleId);
                if (await AdmitAsync(SftpOperation.Fsync, handle.Path, request, token).ConfigureAwait(false)) return;
                handle.Stream.Flush();
                await StatusAsync(id, SftpStatus.Ok, "", token).ConfigureAwait(false);
                return;
            }

            case "limits@openssh.com":
                if (await AdmitAsync(SftpOperation.Extended, request.Path, request, token).ConfigureAwait(false)) return;
                await SendAsync(new SshWriter(ExtendedReply).UInt32(id).UInt64(MaxPacket - 1024).UInt64(MaxRead).UInt64(MaxRead).UInt64(MaxOpenHandles), token).ConfigureAwait(false);
                return;

            default:
                if (await AdmitAsync(SftpOperation.Extended, request.Path, request, token).ConfigureAwait(false)) return;
                await StatusAsync(id, SftpStatus.OperationUnsupported, $"Extension {request.ExtensionName} is not supported.", token).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Record the request; if a rule or the interceptor answers it, send that status and return true.</summary>
    private async ValueTask<bool> AdmitAsync(SftpOperation operation, string path, Request request, CancellationToken token)
    {
        var record = new SftpRequest(operation, path, user, connection)
        {
            TargetPath = request.Target,
            Offset = request.Type is Read or Write ? (long)request.Offset : null,
            Length = request.Type switch { Read => (int)request.Length, Write => request.Data.Length, _ => null },
            OpenFlags = request.Type == Open ? request.Flags : null,
        };
        var verdict = await server.AdmitAsync(record).ConfigureAwait(false);
        // An interceptor that cut the connection ends the session: requests already buffered are not carried out.
        if (connection.IsTerminated) throw new IOException("The connection was terminated.");
        if (verdict is not { } status) return false;
        await StatusAsync(request.Id, status, status == SftpStatus.Ok ? "" : $"Injected failure: {status}.", token).ConfigureAwait(false);
        return true;
    }

    // ---- Replies -------------------------------------------------------------------------------

    private ValueTask SendAsync(SshWriter payload, CancellationToken token)
    {
        connection.Transcript.RecordMessage(TranscriptDirection.Sent, Describe(payload.Span));
        return channel.SendAsync(new SshWriter(payload.Length + 4).String(payload.Span).Memory, token);
    }

    /// <summary>A one-line summary of a reply for the transcript.</summary>
    private static string Describe(ReadOnlySpan<byte> reply)
    {
        var r = new SshReader(reply);
        var type = r.Byte();
        if (type == Version) return $"VERSION {r.UInt32()}";
        var id = r.UInt32();
        return type switch
        {
            Status => $"STATUS {(SftpStatus)r.UInt32()} {r.Utf8()}".TrimEnd(),
            HandleReply => $"HANDLE {r.Utf8()}",
            Data => $"DATA {r.UInt32()} bytes",
            Name => $"NAME {r.UInt32()} entries",
            Attrs => "ATTRS",
            _ => $"EXTENDED_REPLY",
        };
    }

    private ValueTask StatusAsync(uint id, SftpStatus status, string message, CancellationToken token) =>
        SendAsync(new SshWriter(Status).UInt32(id).UInt32((uint)status).String(message).String("en"), token);

    private ValueTask SendAttributesAsync(uint id, SftpAttributes attributes, CancellationToken token)
    {
        var reply = new SshWriter(Attrs).UInt32(id);
        WriteAttributes(reply, attributes);
        return SendAsync(reply, token);
    }

    private ValueTask SendHandleAsync(uint id, Handle handle, CancellationToken token)
    {
        var key = (_nextHandle++).ToString("x8", CultureInfo.InvariantCulture);
        _handles[key] = handle;
        return SendAsync(new SshWriter(HandleReply).UInt32(id).String(key), token);
    }

    private static SftpStatus? ToStatus(Exception ex) => ex switch
    {
        SftpException sftp => sftp.Status,
        FileNotFoundException or DirectoryNotFoundException => SftpStatus.NoSuchFile,
        UnauthorizedAccessException => SftpStatus.PermissionDenied,
        NotSupportedException => SftpStatus.OperationUnsupported,
        IOException => SftpStatus.Failure,
        ArgumentException => SftpStatus.Failure,
        _ => null,
    };

    // ---- Attributes ----------------------------------------------------------------------------

    private const uint AttrSize = 1, AttrUidGid = 2, AttrPermissions = 4, AttrTimes = 8, AttrExtended = 0x80000000;

    private static SftpAttributes ReadAttributes(ref SshReader r)
    {
        var flags = r.UInt32();
        var attributes = new SftpAttributes();
        if ((flags & AttrSize) != 0) attributes = attributes with { Size = (long)r.UInt64() };
        if ((flags & AttrUidGid) != 0) attributes = attributes with { UserId = r.UInt32(), GroupId = r.UInt32() };
        if ((flags & AttrPermissions) != 0) attributes = attributes with { Permissions = r.UInt32() };
        if ((flags & AttrTimes) != 0)
            attributes = attributes with { LastAccessTime = DateTimeOffset.FromUnixTimeSeconds(r.UInt32()), LastWriteTime = DateTimeOffset.FromUnixTimeSeconds(r.UInt32()) };
        if ((flags & AttrExtended) != 0)
            for (var count = r.UInt32(); count > 0; count--)
            {
                r.String();
                r.String();
            }
        return attributes;
    }

    private static void WriteAttributes(SshWriter writer, SftpAttributes attributes)
    {
        var flags = (attributes.Size is null ? 0 : AttrSize)
            | (attributes.UserId is null || attributes.GroupId is null ? 0 : AttrUidGid)
            | (attributes.Permissions is null ? 0 : AttrPermissions)
            | (attributes.LastWriteTime is null ? 0 : AttrTimes);
        writer.UInt32(flags);
        if (attributes.Size is { } size) writer.UInt64((ulong)size);
        if ((flags & AttrUidGid) != 0) writer.UInt32(attributes.UserId!.Value).UInt32(attributes.GroupId!.Value);
        if (attributes.Permissions is { } permissions) writer.UInt32(permissions);
        if (attributes.LastWriteTime is { } modified)
            writer.UInt32(UnixTime(attributes.LastAccessTime ?? modified)).UInt32(UnixTime(modified));
    }

    private static uint UnixTime(DateTimeOffset time) => (uint)Math.Clamp(time.ToUnixTimeSeconds(), 0, uint.MaxValue);

    /// <summary>An <c>ls -l</c> style line, which clients such as OpenSSH's <c>ls -l</c> print verbatim.</summary>
    private string LongName(SftpDirectoryEntry entry)
    {
        var a = entry.Attributes;
        var mode = a.Permissions ?? 0;
        Span<char> chars = stackalloc char[10];
        chars[0] = a.IsDirectory ? 'd' : '-';
        const string rwx = "rwxrwxrwx";
        for (var i = 0; i < 9; i++) chars[i + 1] = (mode & (1u << (8 - i))) != 0 ? rwx[i] : '-';
        var time = (a.LastWriteTime ?? DateTimeOffset.UnixEpoch).UtcDateTime.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        return $"{chars} 1 {user,-8} {user,-8} {a.Size ?? 0,8} {time} {entry.Name}";
    }

    // ---- Handles -------------------------------------------------------------------------------

    private T Lookup<T>(string? key) where T : Handle =>
        key is not null && _handles.TryGetValue(key, out var handle) && handle is T typed
            ? typed
            : throw new SftpException(SftpStatus.Failure, "Invalid handle.");

    private static string HandleKey(ReadOnlySpan<byte> handle) => System.Text.Encoding.ASCII.GetString(handle);

    private static string Normalize(string path) => SftpPath.Normalize(path);

    private abstract class Handle(string path) : IDisposable
    {
        public string Path { get; } = path;

        public virtual void Dispose()
        {
        }
    }

    private sealed class FileHandle(string path, Stream stream, FileAccess access, bool append) : Handle(path)
    {
        public Stream Stream { get; } = stream;

        /// <summary>What the client asked for, which may be less than the stream allows.</summary>
        public FileAccess Access { get; } = access;

        public bool Append { get; } = append;

        public override void Dispose() => Stream.Dispose();
    }

    private sealed class DirectoryHandle(string path, Queue<SftpDirectoryEntry> entries) : Handle(path)
    {
        public Queue<SftpDirectoryEntry> Entries { get; } = entries;
    }
}
