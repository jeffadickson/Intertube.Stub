using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Intertube.Stub.Examples;

// ---- System under test -----------------------------------------------------------------------

public sealed class DnsNameNotFoundException(string name) : Exception($"{name} does not exist (NXDOMAIN).");

/// <summary>
/// A small stub resolver, the kind service-discovery or health-check code embeds: A-record lookups
/// over UDP with retries across several name servers, CNAME following, TCP fallback for truncated
/// answers, and rejection of replies whose ID or question does not match (spoofing defence).
/// </summary>
public sealed class DnsResolver(IReadOnlyList<IPEndPoint> servers, TimeSpan timeout, int attemptsPerServer = 2)
{
    private const ushort TypeA = 1, TypeCname = 5;

    public int QueriesSent { get; private set; }

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        foreach (var server in servers)
        {
            for (var attempt = 0; attempt < attemptsPerServer; attempt++)
            {
                var id = (ushort)Random.Shared.Next(ushort.MaxValue);
                var query = BuildQuery(id, name);
                try
                {
                    var response = await QueryUdpAsync(server, id, name, query, cancellationToken);
                    if ((response[2] & 0x02) != 0) response = await QueryTcpAsync(server, id, name, query, cancellationToken); // TC bit
                    switch (response[3] & 0x0F)
                    {
                        case 0:
                            return Answers(response, name);
                        case 3:
                            throw new DnsNameNotFoundException(name);
                        default:
                            last = new IOException($"{server} answered with RCODE {response[3] & 0x0F}.");
                            attempt = attemptsPerServer; // a failing server is not retried; move on
                            break;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    last = new TimeoutException($"{server} did not answer within {timeout.TotalMilliseconds} ms.");
                }
                catch (SocketException ex)
                {
                    last = ex;
                }
            }
        }
        throw new TimeoutException($"No name server could resolve {name}.", last);
    }

    private async Task<byte[]> QueryUdpAsync(IPEndPoint server, ushort id, string name, byte[] query, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(server.AddressFamily);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await udp.SendAsync(query, server, deadline.Token);
        QueriesSent++;
        while (true)
        {
            var result = await udp.ReceiveAsync(deadline.Token);
            if (result.RemoteEndPoint.Equals(server) && Matches(result.Buffer, id, name)) return result.Buffer;
            // Wrong ID, wrong question or wrong source: a spoofing attempt or a stale reply. Keep waiting.
        }
    }

    private async Task<byte[]> QueryTcpAsync(IPEndPoint server, ushort id, string name, byte[] query, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var tcp = new TcpClient(server.AddressFamily);
        await tcp.ConnectAsync(server, deadline.Token);
        var stream = tcp.GetStream();
        var framed = new byte[2 + query.Length];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, deadline.Token);
        var length = new byte[2];
        await stream.ReadExactlyAsync(length, deadline.Token);
        var response = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
        await stream.ReadExactlyAsync(response, deadline.Token);
        return Matches(response, id, name) ? response : throw new IOException("TCP answer does not match the query.");
    }

    private static byte[] BuildQuery(ushort id, string name)
    {
        var query = new List<byte>();
        query.AddRange([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]); // RD, one question
        query.AddRange(EncodeName(name));
        query.AddRange([0, (byte)TypeA, 0, 1]);
        return [.. query];
    }

    internal static byte[] EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        return [.. bytes];
    }

    private static bool Matches(byte[] response, ushort id, string name)
    {
        if (response.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(response) != id || (response[2] & 0x80) == 0) return false;
        var offset = 12;
        return BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4)) == 1
            && string.Equals(ReadName(response, ref offset), name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }

    private static List<IPAddress> Answers(byte[] response, string name)
    {
        var offset = 12;
        ReadName(response, ref offset);
        offset += 4;
        var records = new List<(string Name, ushort Type, byte[] Data, int Offset)>();
        for (var count = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)); count > 0; count--)
        {
            var owner = ReadName(response, ref offset);
            var type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 8));
            records.Add((owner, type, response.AsSpan(offset + 10, length).ToArray(), offset + 10));
            offset += 10 + length;
        }

        var target = name.TrimEnd('.');
        for (var hops = 0; hops < 8; hops++)
        {
            var addresses = records.Where(r => r.Type == TypeA && r.Name.Equals(target, StringComparison.OrdinalIgnoreCase)).Select(r => new IPAddress(r.Data)).ToList();
            if (addresses.Count > 0) return addresses;
            var cname = records.FirstOrDefault(r => r.Type == TypeCname && r.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (cname.Data is null) return [];
            var at = cname.Offset;
            target = ReadName(response, ref at);
        }
        throw new IOException($"CNAME chain for {name} is too long.");
    }

    /// <summary>Read a possibly compressed name (RFC 1035 section 4.1.4).</summary>
    private static string ReadName(byte[] message, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var guard = 0; guard < 128; guard++)
        {
            var length = message[position];
            if (length == 0)
            {
                if (!jumped) offset = position + 1;
                return string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (!jumped) offset = position + 2;
                jumped = true;
                position = ((length & 0x3F) << 8) | message[position + 1];
                continue;
            }
            labels.Add(Encoding.ASCII.GetString(message, position + 1, length));
            position += 1 + length;
        }
        throw new IOException("Name compression loop.");
    }
}

// ---- A fake name server --------------------------------------------------------------------

/// <summary>
/// An authoritative name server on one port, over UDP and TCP, serving A and CNAME records from a
/// dictionary. Real servers set the TC bit when an answer will not fit in a datagram;
/// <see cref="UdpAnswerLimit"/> makes that easy to provoke.
/// </summary>
public sealed class FakeNameServer : IAsyncDisposable
{
    private readonly List<string> _udpQueries = [];

    public FakeNameServer(UdpServerOptions? udpOptions = null)
    {
        // TCP and UDP on one port, as for a real name server. A port free for one protocol may be
        // taken or reserved for the other (Windows reserves UDP ports in blocks of 100, and hands out
        // ephemeral ports in sequence), so try random ports rather than the next ephemeral one.
        for (var attempt = 1; ; attempt++)
        {
            var port = Random.Shared.Next(49152, 65536);
            try
            {
                Udp = UdpServer.Listen(new IPEndPoint(IPAddress.Loopback, port), HandleUdpAsync, udpOptions);
            }
            catch (SocketException) when (attempt < 50)
            {
                continue;
            }
            try
            {
                Tcp = TcpServer.Listen(port, HandleTcpAsync);
                return;
            }
            catch (SocketException) when (attempt < 50)
            {
                Udp.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    public TcpServer Tcp { get; }

    public UdpServer Udp { get; }

    public IPEndPoint EndPoint => new(IPAddress.Loopback, Tcp.Port);

    public Dictionary<string, List<IPAddress>> A { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Cname { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Over UDP, answers with more records than this are truncated (TC bit, no answers).</summary>
    public int UdpAnswerLimit { get; set; } = int.MaxValue;

    /// <summary>Force a response code (2 = SERVFAIL, 5 = REFUSED) for the nth UDP query of a name, or null for a normal answer.</summary>
    public Func<string, int, int?> ResponseCode { get; set; } = (_, _) => null;

    /// <summary>Before each real UDP answer, also send one with a wrong ID, as an off-path attacker would.</summary>
    public bool SendSpoofedReplyFirst { get; set; }

    public IReadOnlyList<string> UdpQueries
    {
        get { lock (_udpQueries) return [.. _udpQueries]; }
    }

    private async Task HandleUdpAsync(Datagram datagram)
    {
        var query = datagram.Data.ToArray();
        var name = QuestionName(query);
        int count;
        lock (_udpQueries)
        {
            _udpQueries.Add(name);
            count = _udpQueries.Count(q => q.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        if (SendSpoofedReplyFirst)
        {
            var spoof = Answer(query, name, rcode: 0, truncate: false, fakeAddress: true);
            spoof[0] ^= 0xFF;
            await datagram.ReplyAsync(spoof);
        }
        var answer = Answer(query, name, ResponseCode(name, count) ?? 0, truncate: Records(name).Count > UdpAnswerLimit);
        await datagram.ReplyAsync(answer);
    }

    private async Task HandleTcpAsync(Connection connection)
    {
        while (true)
        {
            byte[] length;
            try
            {
                length = await connection.ReadExactlyAsync(2);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            var query = await connection.ReadExactlyAsync(BinaryPrimitives.ReadUInt16BigEndian(length));
            var name = QuestionName(query);
            connection.Transcript.RecordMessage(TranscriptDirection.Received, $"QUERY A {name}");
            var answer = Answer(query, name, rcode: 0, truncate: false);
            await connection.WriteAsync((byte[])[(byte)(answer.Length >> 8), (byte)answer.Length, .. answer]);
            connection.Transcript.RecordMessage(TranscriptDirection.Sent, $"ANSWER {Records(name).Count} records");
        }
    }

    private List<(string Owner, ushort Type, byte[] Data)> Records(string name)
    {
        var records = new List<(string, ushort, byte[])>();
        for (var hops = 0; hops < 8 && Cname.TryGetValue(name, out var target); hops++)
        {
            records.Add((name, 5, DnsResolver.EncodeName(target)));
            name = target;
        }
        if (A.TryGetValue(name, out var addresses)) records.AddRange(addresses.Select(a => (name, (ushort)1, a.GetAddressBytes())));
        return records;
    }

    private byte[] Answer(byte[] query, string name, int rcode, bool truncate, bool fakeAddress = false)
    {
        var questionEnd = 12 + DnsResolver.EncodeName(name).Length + 4;
        var records = rcode != 0 || truncate ? [] : Records(name);
        if (rcode == 0 && !truncate && records.Count == 0 && !A.ContainsKey(name)) rcode = 3; // NXDOMAIN
        if (fakeAddress) records = [(name, 1, [6, 6, 6, 6])];
        var response = new List<byte>(query[..questionEnd]);
        response[2] = (byte)(0x84 | (truncate ? 0x02 : 0) | (query[2] & 0x01)); // QR, AA, TC, RD
        response[3] = (byte)(0x80 | rcode);
        response[6] = (byte)(records.Count >> 8);
        response[7] = (byte)records.Count;
        foreach (var (owner, type, data) in records)
        {
            response.AddRange(DnsResolver.EncodeName(owner));
            response.AddRange([0, (byte)type, 0, 1, 0, 0, 0, 60, (byte)(data.Length >> 8), (byte)data.Length]);
            response.AddRange(data);
        }
        return [.. response];
    }

    private static string QuestionName(byte[] query)
    {
        var labels = new List<string>();
        for (var i = 12; query[i] != 0; i += 1 + query[i]) labels.Add(Encoding.ASCII.GetString(query, i + 1, query[i]));
        return string.Join('.', labels);
    }

    public async ValueTask DisposeAsync()
    {
        await Udp.DisposeAsync();
        await Tcp.DisposeAsync();
    }
}

// ---- Tests -----------------------------------------------------------------------------------

/// <summary>
/// Resolver behaviour that a real DNS server will not misbehave for on demand: packet loss,
/// truncation, SERVFAIL, a dead primary and spoofed replies. One fake serves UDP and TCP on the same port.
/// </summary>
public sealed class DnsExample : IAsyncDisposable
{
    private readonly FakeNameServer _dns = new();

    public DnsExample()
    {
        _dns.A["api.internal.test"] = [IPAddress.Parse("10.0.0.7")];
        _dns.Cname["www.internal.test"] = "lb.internal.test";
        _dns.Cname["lb.internal.test"] = "api.internal.test";
    }

    private DnsResolver Resolver(params FakeNameServer[] servers) =>
        new([.. (servers.Length == 0 ? [_dns] : servers).Select(s => s.EndPoint)], TimeSpan.FromMilliseconds(300));

    public ValueTask DisposeAsync() => _dns.DisposeAsync();

    [Fact]
    public async Task ResolvesARecords_OverUdp()
    {
        var addresses = await Resolver().ResolveAsync("API.internal.test");

        Assert.Equal([IPAddress.Parse("10.0.0.7")], addresses);
        Assert.Equal(["API.internal.test"], _dns.UdpQueries);
        _dns.Tcp.Transcript.AssertConnectionCount(0);
    }

    [Fact]
    public async Task FollowsCnameChains()
    {
        Assert.Equal([IPAddress.Parse("10.0.0.7")], await Resolver().ResolveAsync("www.internal.test"));
    }

    [Fact]
    public async Task NxDomain_IsReportedAsSuch_AndNotRetried()
    {
        var resolver = Resolver();

        await Assert.ThrowsAsync<DnsNameNotFoundException>(() => resolver.ResolveAsync("nope.internal.test"));

        Assert.Equal(1, resolver.QueriesSent);
    }

    [Fact]
    public async Task TruncatedAnswer_IsRetriedOverTcp()
    {
        _dns.A["pool.internal.test"] = [.. Enumerable.Range(1, 40).Select(i => IPAddress.Parse($"10.1.0.{i}"))];
        _dns.UdpAnswerLimit = 10;

        var addresses = await Resolver().ResolveAsync("pool.internal.test");

        Assert.Equal(40, addresses.Count);
        _dns.Tcp.Transcript.AssertReceivedMessage("QUERY A pool.internal.test");
        _dns.Tcp.Transcript.AssertSentMessage("ANSWER 40 records");
    }

    [Fact]
    public async Task PacketLoss_IsRiddenOutWithRetries()
    {
        await using var lossy = new FakeNameServer(new UdpServerOptions { DropRate = 0.5, Seed = 3 });
        lossy.A["api.internal.test"] = [IPAddress.Parse("10.0.0.7")];
        var resolver = new DnsResolver([lossy.EndPoint], TimeSpan.FromMilliseconds(200), attemptsPerServer: 8);

        Assert.Equal([IPAddress.Parse("10.0.0.7")], await resolver.ResolveAsync("api.internal.test"));

        Assert.True(lossy.Udp.Transcript.Records.Any(r => r.Dropped), "the seed should drop at least one query");
        Assert.True(resolver.QueriesSent > 1);
    }

    [Fact]
    public async Task ServFail_MovesOnToTheNextServer()
    {
        await using var broken = new FakeNameServer { ResponseCode = (_, _) => 2 };

        var addresses = await Resolver(broken, _dns).ResolveAsync("api.internal.test");

        Assert.Equal([IPAddress.Parse("10.0.0.7")], addresses);
        Assert.Single(broken.UdpQueries);
    }

    [Fact]
    public async Task SilentPrimary_TimesOut_ThenTheSecondaryAnswers()
    {
        await using var silent = new FakeNameServer(new UdpServerOptions { DropRate = 1.0 });

        var addresses = await Resolver(silent, _dns).ResolveAsync("api.internal.test");

        Assert.Equal([IPAddress.Parse("10.0.0.7")], addresses);
        Assert.Equal(2, silent.Udp.Transcript.Records.Count(r => r.Dropped));
    }

    [Fact]
    public async Task SpoofedReplyWithTheWrongId_IsIgnored()
    {
        _dns.SendSpoofedReplyFirst = true;

        var addresses = await Resolver().ResolveAsync("api.internal.test");

        Assert.Equal([IPAddress.Parse("10.0.0.7")], addresses);
        Assert.Equal(2, _dns.Udp.Transcript.Sent.Count);
    }

    [Fact]
    public async Task EveryServerSilent_GivesUp()
    {
        await using var silent = new FakeNameServer(new UdpServerOptions { DropRate = 1.0 });

        await Assert.ThrowsAsync<TimeoutException>(() => Resolver(silent).ResolveAsync("api.internal.test"));
    }
}
