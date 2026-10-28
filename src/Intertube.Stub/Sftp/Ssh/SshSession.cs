using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Intertube.Stub.Sftp.Ssh;

/// <summary>
/// One SSH connection: transport handshake, user authentication (RFC 4252) and the connection
/// protocol (RFC 4254) with any number of session channels running the <c>sftp</c> subsystem.
/// </summary>
internal sealed class SshSession(SftpServer server, Connection connection)
{
    private const int MaxAuthAttempts = 20;

    private readonly SshTransport _transport = new(connection, server.Algorithms);
    private readonly Dictionary<uint, SshChannel> _channels = [];
    private readonly List<Task> _subsystems = [];
    private uint _nextChannel;

    public async Task RunAsync()
    {
        var token = connection.CancellationToken;
        try
        {
            await _transport.HandshakeAsync(token).ConfigureAwait(false);
            connection.Transcript.RecordMessage(TranscriptDirection.Event,
                $"SSH {_transport.ClientVersion}: {_transport.NegotiatedKeyExchange}, {_transport.NegotiatedCipher}{(_transport.NegotiatedMac is null ? "" : ", " + _transport.NegotiatedMac)}");
            if (!await AcceptServiceAsync("ssh-userauth", token).ConfigureAwait(false)) return;
            var user = await AuthenticateAsync(token).ConfigureAwait(false);
            if (user is null) return;
            await RunConnectionAsync(user, token).ConfigureAwait(false);
        }
        catch (SshProtocolException ex)
        {
            connection.Transcript.RecordMessage(TranscriptDirection.Sent, $"DISCONNECT {ex.Message}");
            await _transport.DisconnectAsync(ex.Reason, ex.Message).ConfigureAwait(false);
            // A client that cannot agree on algorithms, or fails a MAC, is a test outcome, not a server fault.
            if (ex.Reason == SshProtocolException.ProtocolError) throw;
        }
        catch (EndOfStreamException)
        {
            // The client went away mid-handshake or mid-packet.
        }
        finally
        {
            foreach (var channel in _channels.Values) channel.Inbound.Writer.Complete();
            await Task.WhenAll(_subsystems).ConfigureAwait(false);
            _transport.Dispose();
        }
    }

    private async Task<bool> AcceptServiceAsync(string service, CancellationToken token)
    {
        var message = await _transport.ReceiveAsync(token).ConfigureAwait(false);
        if (message is null) return false;
        if (message[0] != SshMessage.ServiceRequest) throw new SshProtocolException($"Expected SERVICE_REQUEST but got message {message[0]}.");
        var requested = Read(message, (ref r) => r.Utf8());
        if (requested != service) throw new SshProtocolException($"Service {requested} is not available.", SshProtocolException.ServiceNotAvailable);
        await _transport.SendAsync(new SshWriter(SshMessage.ServiceAccept).String(service), token).ConfigureAwait(false);
        return true;
    }

    /// <summary>Run the authentication exchange; returns the user name, or null if the client gave up.</summary>
    private async Task<string?> AuthenticateAsync(CancellationToken token)
    {
        for (var attempt = 0; attempt < MaxAuthAttempts; attempt++)
        {
            var message = await _transport.ReceiveAsync(token).ConfigureAwait(false);
            if (message is null) return null;
            if (message[0] != SshMessage.UserAuthRequest) throw new SshProtocolException($"Expected USERAUTH_REQUEST but got message {message[0]}.");

            var request = ParseAuthRequest(message);
            switch (request.Method)
            {
                case "publickey" when request.Signature is null:
                    // A query: "would you accept this key?" Only a refusal is recorded; acceptance is followed by the signed attempt.
                    if (KeyAllowed(request.User, request.Key))
                    {
                        await _transport.SendAsync(new SshWriter(SshMessage.UserAuthPkOk).String(request.KeyAlgorithm!).String(request.Key!.Blob.Span), token).ConfigureAwait(false);
                        continue;
                    }
                    Record(new SshAuthAttempt(request.User, "publickey", false, request.Key?.Fingerprint, connection.Id));
                    break;

                case "publickey":
                {
                    var data = new SshWriter()
                        .String(_transport.SessionId).Byte(SshMessage.UserAuthRequest)
                        .String(request.User).String(request.Service).String("publickey").Bool(true)
                        .String(request.KeyAlgorithm!).String(request.Key!.Blob.Span);
                    var accepted = request.Service == "ssh-connection" && KeyAllowed(request.User, request.Key) && request.Key.Verify(data.Span, request.Signature);
                    Record(new SshAuthAttempt(request.User, "publickey", accepted, request.Key.Fingerprint, connection.Id));
                    if (accepted) return await SucceedAsync(request.User, token).ConfigureAwait(false);
                    break;
                }

                case "password":
                {
                    var accepted = request.Service == "ssh-connection" && PasswordAllowed(request.User, request.Password!);
                    Record(new SshAuthAttempt(request.User, "password", accepted, null, connection.Id));
                    if (accepted) return await SucceedAsync(request.User, token).ConfigureAwait(false);
                    break;
                }

                case "none":
                    break;

                default:
                    Record(new SshAuthAttempt(request.User, request.Method, false, null, connection.Id));
                    break;
            }
            await _transport.SendAsync(new SshWriter(SshMessage.UserAuthFailure).NameList(["publickey", "password"]).Bool(false), token).ConfigureAwait(false);
        }
        throw new SshProtocolException("Too many authentication failures.", SshProtocolException.NoMoreAuthMethods);
    }

    private void Record(SshAuthAttempt attempt)
    {
        server.Record(attempt);
        connection.Transcript.RecordMessage(TranscriptDirection.Received, attempt.ToString());
    }

    private async Task<string> SucceedAsync(string user, CancellationToken token)
    {
        await _transport.SendAsync(new SshWriter(SshMessage.UserAuthSuccess), token).ConfigureAwait(false);
        return user;
    }

    private bool KeyAllowed(string user, SshPublicKey? key) =>
        key is not null && (server.Options.Users.Count == 0 || server.Options.Users.Any(u => u.Name == user && u.AuthorizedKeys.Contains(key)));

    private bool PasswordAllowed(string user, string password) =>
        server.Options.Users.Count == 0 || server.Options.Users.Any(u => u.Name == user && u.Password is not null && u.Password == password);

    private sealed record AuthRequest(string User, string Service, string Method)
    {
        public string? KeyAlgorithm { get; init; }

        public SshPublicKey? Key { get; init; }

        public byte[]? Signature { get; init; }

        public string? Password { get; init; }
    }

    private static AuthRequest ParseAuthRequest(byte[] message)
    {
        var reader = new SshReader(message.AsSpan(1));
        var request = new AuthRequest(reader.Utf8(), reader.Utf8(), reader.Utf8());
        switch (request.Method)
        {
            case "publickey":
            {
                var signed = reader.Bool();
                var algorithm = reader.Utf8();
                SshPublicKey? key;
                try
                {
                    key = SshPublicKey.FromBlob(reader.String());
                }
                catch (FormatException)
                {
                    key = null;
                }
                return request with { KeyAlgorithm = algorithm, Key = key, Signature = signed ? reader.String().ToArray() : null };
            }
            case "password":
                reader.Bool();
                return request with { Password = reader.Utf8() };
            default:
                return request;
        }
    }

    private async Task RunConnectionAsync(string user, CancellationToken token)
    {
        while (await _transport.ReceiveAsync(token).ConfigureAwait(false) is { } message)
        {
            switch (message[0])
            {
                case SshMessage.GlobalRequest:
                {
                    var wantReply = Read(message, (ref r) => { r.Utf8(); return r.Bool(); });
                    if (wantReply) await _transport.SendAsync(new SshWriter(SshMessage.RequestFailure), token).ConfigureAwait(false);
                    break;
                }

                case SshMessage.ChannelOpen:
                    await OpenChannelAsync(message, token).ConfigureAwait(false);
                    break;

                case SshMessage.ChannelRequest:
                    await ChannelRequestAsync(message, user, token).ConfigureAwait(false);
                    break;

                case SshMessage.ChannelWindowAdjust:
                    Channel(message).AddWindow(BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(5)));
                    break;

                case SshMessage.ChannelData:
                {
                    var channel = Channel(message);
                    var data = Read(message, (ref r) => { r.UInt32(); return r.String().ToArray(); });
                    if (!channel.ClientEof) await channel.Inbound.Writer.WriteAsync(data, token).ConfigureAwait(false);
                    break;
                }

                case SshMessage.ChannelExtendedData:
                    break;

                case SshMessage.ChannelEof:
                {
                    var channel = Channel(message);
                    channel.ClientEof = true;
                    await channel.Inbound.Writer.CompleteAsync().ConfigureAwait(false);
                    break;
                }

                case SshMessage.ChannelClose:
                {
                    var channel = Channel(message);
                    channel.ClientEof = true;
                    await channel.Inbound.Writer.CompleteAsync().ConfigureAwait(false);
                    await channel.ClientClosedAsync(token).ConfigureAwait(false);
                    _channels.Remove(channel.LocalId);
                    break;
                }

                case SshMessage.ChannelSuccess or SshMessage.ChannelFailure:
                    break;

                default:
                    connection.Transcript.RecordMessage(TranscriptDirection.Event, $"ignored SSH message {message[0]}");
                    break;
            }
        }
    }

    private async Task OpenChannelAsync(byte[] message, CancellationToken token)
    {
        var (type, remoteId, window, maxPacket) = Read(message, (ref r) => (r.Utf8(), r.UInt32(), r.UInt32(), r.UInt32()));
        if (type != "session")
        {
            await _transport.SendAsync(new SshWriter(SshMessage.ChannelOpenFailure).UInt32(remoteId).UInt32(3).String($"Channel type {type} is not supported.").String(""), token).ConfigureAwait(false);
            return;
        }
        var channel = new SshChannel(_transport, _nextChannel++, remoteId, window, maxPacket);
        _channels[channel.LocalId] = channel;
        await _transport.SendAsync(new SshWriter(SshMessage.ChannelOpenConfirmation)
            .UInt32(remoteId).UInt32(channel.LocalId).UInt32(SshChannel.LocalWindow).UInt32(SshChannel.LocalMaxPacket), token).ConfigureAwait(false);
    }

    private async Task ChannelRequestAsync(byte[] message, string user, CancellationToken token)
    {
        var channel = Channel(message);
        var (type, wantReply, subsystem) = Read(message, (ref r) =>
        {
            r.UInt32();
            var t = r.Utf8();
            var reply = r.Bool();
            return (t, reply, t == "subsystem" ? r.Utf8() : null);
        });

        var ok = type == "subsystem" && subsystem == "sftp" && !channel.Started;
        if (ok)
        {
            channel.Started = true;
            _subsystems.Add(Task.Run(() => new SftpSession(server, connection, channel, user).RunAsync()));
        }
        else
        {
            connection.Transcript.RecordMessage(TranscriptDirection.Event, $"refused channel request {type}{(subsystem is null ? "" : " " + subsystem)}");
        }
        if (wantReply)
            await _transport.SendAsync(new SshWriter(ok ? SshMessage.ChannelSuccess : SshMessage.ChannelFailure).UInt32(channel.RemoteId), token).ConfigureAwait(false);
    }

    private SshChannel Channel(byte[] message) =>
        _channels.TryGetValue(BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(1)), out var channel)
            ? channel
            : throw new SshProtocolException("Message for an unknown channel.");

    private delegate T ReaderFunc<T>(ref SshReader reader);

    private static T Read<T>(byte[] message, ReaderFunc<T> read)
    {
        var reader = new SshReader(message.AsSpan(1));
        return read(ref reader);
    }
}

/// <summary>A session channel's flow control: the client's window for what we send, and ours for what it sends.</summary>
internal sealed class SshChannel(SshTransport transport, uint localId, uint remoteId, uint remoteWindow, uint remoteMaxPacket)
{
    public const uint LocalWindow = 2 * 1024 * 1024;
    public const uint LocalMaxPacket = 32 * 1024;

    private readonly object _lock = new();
    private long _remoteWindow = remoteWindow;
    private TaskCompletionSource _windowChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _consumed;
    private int _closeSent;
    private bool _clientClosed;

    public uint LocalId { get; } = localId;

    public uint RemoteId { get; } = remoteId;

    /// <summary>Bytes from the client. Unbounded here: the SSH window is what limits it.</summary>
    public Pipe Inbound { get; } = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));

    public bool Started { get; set; }

    public bool ClientEof { get; set; }

    public void AddWindow(uint bytes)
    {
        lock (_lock)
        {
            _remoteWindow += bytes;
            _windowChanged.TrySetResult();
            _windowChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Send data, waiting for window space and splitting to the client's packet size.</summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        var maxChunk = (int)Math.Min(remoteMaxPacket, LocalMaxPacket);
        while (!data.IsEmpty)
        {
            int chunk;
            Task wait;
            lock (_lock)
            {
                if (_clientClosed) throw new IOException("The client closed the channel.");
                chunk = (int)Math.Min(Math.Min(_remoteWindow, data.Length), maxChunk);
                if (chunk > 0) _remoteWindow -= chunk;
                wait = _windowChanged.Task;
            }
            if (chunk == 0)
            {
                await wait.WaitAsync(token).ConfigureAwait(false);
                continue;
            }
            await transport.SendAsync(new SshWriter(SshMessage.ChannelData, chunk + 16).UInt32(RemoteId).String(data.Span[..chunk]), token).ConfigureAwait(false);
            data = data[chunk..];
        }
    }

    /// <summary>The subsystem used <paramref name="bytes"/> of input; reopen the client's window when half is used.</summary>
    public async ValueTask ConsumedAsync(long bytes, CancellationToken token)
    {
        _consumed += bytes;
        if (_consumed < LocalWindow / 2) return;
        var adjust = (uint)_consumed;
        _consumed = 0;
        await transport.SendAsync(new SshWriter(SshMessage.ChannelWindowAdjust).UInt32(RemoteId).UInt32(adjust), token).ConfigureAwait(false);
    }

    /// <summary>The subsystem finished: report exit status 0, then EOF and CLOSE.</summary>
    public async Task CloseAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _closeSent, 1) != 0) return;
        lock (_lock)
            if (_clientClosed) return;
        await transport.SendAsync(new SshWriter(SshMessage.ChannelRequest).UInt32(RemoteId).String("exit-status").Bool(false).UInt32(0), token).ConfigureAwait(false);
        await transport.SendAsync(new SshWriter(SshMessage.ChannelEof).UInt32(RemoteId), token).ConfigureAwait(false);
        await transport.SendAsync(new SshWriter(SshMessage.ChannelClose).UInt32(RemoteId), token).ConfigureAwait(false);
    }

    /// <summary>The client sent CLOSE; answer with ours unless already sent.</summary>
    public async Task ClientClosedAsync(CancellationToken token)
    {
        lock (_lock)
        {
            _clientClosed = true;
            _windowChanged.TrySetResult();
        }
        if (Interlocked.Exchange(ref _closeSent, 1) == 0)
            await transport.SendAsync(new SshWriter(SshMessage.ChannelClose).UInt32(RemoteId), token).ConfigureAwait(false);
    }
}
