using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Intertube.Stub.Examples.Support;

namespace Intertube.Stub.Examples;

// ---- System under test -----------------------------------------------------------------------

/// <summary>
/// An API client that must work behind a corporate proxy (SOCKS5 or HTTP CONNECT, optionally with
/// credentials), the setting most apps never test until a customer installs them behind one.
/// </summary>
public sealed class ProxiedStatusClient(Uri api, Uri? proxy, NetworkCredential? proxyCredentials = null, RemoteCertificateValidationCallback? certificateValidation = null)
{
    public async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy(proxy) { Credentials = proxyCredentials },
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        if (certificateValidation is not null) handler.SslOptions.RemoteCertificateValidationCallback = certificateValidation;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        return await http.GetStringAsync(new Uri(api, "/status"), cancellationToken);
    }
}

// ---- A fake proxy ------------------------------------------------------------------------------

/// <summary>
/// A SOCKS5 (RFC 1928/1929) or HTTP CONNECT proxy that relays to real endpoints. Host names are
/// resolved through <see cref="Hosts"/>, so tests can use names that exist nowhere else and prove the
/// client let the proxy resolve them. Each tunnel request is a transcript message.
/// </summary>
public sealed class FakeProxy : IAsyncDisposable
{
    public enum Protocol { Socks5, HttpConnect }

    public FakeProxy(Protocol protocol, NetworkCredential? requiredCredentials = null, ServerOptions? options = null)
    {
        Kind = protocol;
        RequiredCredentials = requiredCredentials;
        Server = TcpServer.Listen(0, protocol == Protocol.Socks5 ? Socks5Async : HttpConnectAsync, options);
    }

    public Protocol Kind { get; }

    public NetworkCredential? RequiredCredentials { get; }

    public TcpServer Server { get; }

    public Uri Uri => new($"{(Kind == Protocol.Socks5 ? "socks5" : "http")}://{Server.Host}:{Server.Port}");

    /// <summary>Name to address map used for destinations.</summary>
    public Dictionary<string, IPAddress> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Return false to refuse a destination ("host:port"), as a filtering proxy would.</summary>
    public Func<string, bool> Allow { get; set; } = _ => true;

    // SOCKS5 -----------------------------------------------------------------------------------

    private async Task Socks5Async(Connection client)
    {
        var greeting = await client.ReadExactlyAsync(2);
        if (greeting[0] != 5) return;
        var methods = await client.ReadExactlyAsync(greeting[1]);
        var wanted = RequiredCredentials is null ? (byte)0 : (byte)2;
        if (!methods.Contains(wanted))
        {
            await client.WriteAsync(new byte[] { 5, 0xFF });
            client.Transcript.RecordMessage(TranscriptDirection.Sent, "NO ACCEPTABLE METHOD");
            return;
        }
        await client.WriteAsync(new byte[] { 5, wanted });

        if (wanted == 2)
        {
            var header = await client.ReadExactlyAsync(2);
            var user = Encoding.UTF8.GetString(await client.ReadExactlyAsync(header[1]));
            var password = Encoding.UTF8.GetString(await client.ReadExactlyAsync((await client.ReadExactlyAsync(1))[0]));
            var ok = user == RequiredCredentials!.UserName && password == RequiredCredentials.Password;
            client.Transcript.RecordMessage(TranscriptDirection.Received, $"AUTH {user} {(ok ? "accepted" : "rejected")}");
            await client.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 });
            if (!ok) return;
        }

        var request = await client.ReadExactlyAsync(4);
        var host = request[3] switch
        {
            1 => new IPAddress(await client.ReadExactlyAsync(4)).ToString(),
            3 => Encoding.ASCII.GetString(await client.ReadExactlyAsync((await client.ReadExactlyAsync(1))[0])),
            4 => new IPAddress(await client.ReadExactlyAsync(16)).ToString(),
            _ => throw new IOException("Bad address type."),
        };
        var portBytes = await client.ReadExactlyAsync(2);
        var destination = $"{host}:{(portBytes[0] << 8) | portBytes[1]}";
        client.Transcript.RecordMessage(TranscriptDirection.Received, $"CONNECT {destination}");

        var (upstream, reply) = request[1] != 1 ? (null, (byte)7) : await OpenAsync(destination);
        await client.WriteAsync(new byte[] { 5, reply, 0, 1, 0, 0, 0, 0, 0, 0 });
        client.Transcript.RecordMessage(TranscriptDirection.Sent, reply == 0 ? "SUCCEEDED" : $"FAILED {reply}");
        if (upstream is not null) await RelayAsync(client, upstream);
    }

    // HTTP CONNECT -----------------------------------------------------------------------------

    private async Task HttpConnectAsync(Connection client)
    {
        while (await HttpStub.ReadRequestAsync(client) is { } request)
        {
            if (request.Method != "CONNECT")
            {
                await client.WriteAsync(HttpStub.Response(405, "", "text/plain"));
                return;
            }
            client.Transcript.RecordMessage(TranscriptDirection.Received, $"CONNECT {request.Target}");

            if (RequiredCredentials is not null)
            {
                var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{RequiredCredentials.UserName}:{RequiredCredentials.Password}"));
                request.Headers.TryGetValue("Proxy-Authorization", out var given);
                if (given != expected)
                {
                    client.Transcript.RecordMessage(TranscriptDirection.Sent, given is null ? "407 (challenge)" : "407 (wrong credentials)");
                    await client.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\n\r\n");
                    continue;
                }
            }

            var (upstream, reply) = await OpenAsync(request.Target);
            if (upstream is null)
            {
                client.Transcript.RecordMessage(TranscriptDirection.Sent, reply == 2 ? "403" : "502");
                await client.WriteAsync(reply == 2 ? "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n" : "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n");
                return;
            }
            await client.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n");
            client.Transcript.RecordMessage(TranscriptDirection.Sent, "200 tunnel open");
            await RelayAsync(client, upstream);
            return;
        }
    }

    // Shared -----------------------------------------------------------------------------------

    /// <summary>Connect to "host:port"; the reply code follows SOCKS5 (0 ok, 2 not allowed, 4 host unreachable, 5 refused).</summary>
    private async Task<(TcpClient? Upstream, byte Reply)> OpenAsync(string destination)
    {
        if (!Allow(destination)) return (null, 2);
        var colon = destination.LastIndexOf(':');
        var host = destination[..colon].Trim('[', ']');
        var port = int.Parse(destination[(colon + 1)..]);
        if (!Hosts.TryGetValue(host, out var address) && !IPAddress.TryParse(host, out address)) return (null, 4);
        var upstream = new TcpClient(address.AddressFamily);
        try
        {
            await upstream.ConnectAsync(address, port);
            return (upstream, 0);
        }
        catch (SocketException)
        {
            upstream.Dispose();
            return (null, 5);
        }
    }

    private static async Task RelayAsync(Connection client, TcpClient upstream)
    {
        using (upstream)
        {
            var remote = upstream.GetStream();
            var up = Task.Run(async () =>
            {
                await client.Stream.CopyToAsync(remote);
                upstream.Client.Shutdown(SocketShutdown.Send);
            });
            var down = remote.CopyToAsync(client.Stream);
            await Task.WhenAny(up, down);
            await Task.WhenAny(Task.WhenAll(up, down), Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

// ---- Tests -----------------------------------------------------------------------------------

public sealed class ProxyExample : IAsyncDisposable
{
    private readonly TcpServer _api = TcpServer.Listen(0, HttpStub.Respond(_ => HttpStub.Response(200, "{\"status\":\"green\"}")));
    private readonly TlsOptions _tls = TlsOptions.SelfSigned("api.corp.test");
    private readonly TcpServer _secureApi;

    public ProxyExample() =>
        _secureApi = TcpServer.Listen(0, HttpStub.Respond(_ => HttpStub.Response(200, "{\"status\":\"secure\"}")), new ServerOptions { Tls = _tls });

    /// <summary>The API under a name only the proxy can resolve.</summary>
    private Uri Api => new($"http://api.corp.test:{_api.Port}");

    private Uri SecureApi => new($"https://api.corp.test:{_secureApi.Port}");

    private FakeProxy Proxy(FakeProxy.Protocol protocol, NetworkCredential? credentials = null, ServerOptions? options = null)
    {
        var proxy = new FakeProxy(protocol, credentials, options);
        proxy.Hosts["api.corp.test"] = IPAddress.Loopback;
        return proxy;
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _secureApi.DisposeAsync();
    }

    [Fact]
    public async Task Socks5_TunnelsTheRequest_AndTheProxyResolvesTheName()
    {
        await using var proxy = Proxy(FakeProxy.Protocol.Socks5);

        var status = await new ProxiedStatusClient(Api, proxy.Uri).GetStatusAsync();

        Assert.Equal("{\"status\":\"green\"}", status);
        proxy.Server.Transcript.AssertReceivedMessage($"CONNECT api.corp.test:{_api.Port}");
        _api.Transcript.AssertReceivedLine("GET /status HTTP/1.1");
    }

    [Fact]
    public async Task Socks5_WithCredentials()
    {
        await using var proxy = Proxy(FakeProxy.Protocol.Socks5, new NetworkCredential("alice", "pw"));

        await new ProxiedStatusClient(Api, proxy.Uri, new NetworkCredential("alice", "pw")).GetStatusAsync();

        proxy.Server.Transcript.AssertReceivedMessagesInOrder("AUTH alice accepted", "CONNECT *");
    }

    [Fact]
    public async Task Socks5_WrongCredentials_Fail()
    {
        await using var proxy = Proxy(FakeProxy.Protocol.Socks5, new NetworkCredential("alice", "pw"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ProxiedStatusClient(Api, proxy.Uri, new NetworkCredential("alice", "nope")).GetStatusAsync());

        proxy.Server.Transcript.AssertNotReceivedMessage("CONNECT *");
    }

    [Fact]
    public async Task Socks5_DestinationRefusedByPolicy_Fails()
    {
        await using var proxy = Proxy(FakeProxy.Protocol.Socks5);
        proxy.Allow = destination => !destination.StartsWith("api.corp.test", StringComparison.Ordinal);

        await Assert.ThrowsAsync<HttpRequestException>(() => new ProxiedStatusClient(Api, proxy.Uri).GetStatusAsync());

        proxy.Server.Transcript.AssertSentMessage("FAILED 2");
        _api.Transcript.AssertConnectionCount(0);
    }

    [Fact]
    public async Task HttpConnect_TunnelsTls_AndAnswersTheCredentialChallenge()
    {
        await using var proxy = Proxy(FakeProxy.Protocol.HttpConnect, new NetworkCredential("bob", "pw"));

        var status = await new ProxiedStatusClient(SecureApi, proxy.Uri, new NetworkCredential("bob", "pw"), _tls.TrustServerCertificate).GetStatusAsync();

        Assert.Equal("{\"status\":\"secure\"}", status);
        proxy.Server.Transcript.AssertReceivedMessagesInOrder($"CONNECT api.corp.test:{_secureApi.Port}", $"CONNECT api.corp.test:{_secureApi.Port}");
        proxy.Server.Transcript.AssertSentMessage("407 (challenge)");
        proxy.Server.Transcript.AssertSentMessage("200 tunnel open");
        // The proxy only ever saw ciphertext after the tunnel opened; the API saw the request in the clear.
        proxy.Server.Transcript.AssertNotReceivedLine("GET /status HTTP/1.1");
        _secureApi.Transcript.AssertReceivedLine("GET /status HTTP/1.1");
    }

    [Fact]
    public async Task HttpConnect_UnreachableDestination_Is502()
    {
        await using var proxy = Proxy(FakeProxy.Protocol.HttpConnect);
        proxy.Hosts.Remove("api.corp.test");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ProxiedStatusClient(SecureApi, proxy.Uri, certificateValidation: _tls.TrustServerCertificate).GetStatusAsync());

        Assert.Contains("502", ex.Message);
    }

    [Fact]
    public async Task ProxyCuttingTheTunnelMidResponse_Fails()
    {
        // The SOCKS handshake replies are 2 + 10 bytes; let 20 bytes of the HTTP response through, then cut.
        await using var proxy = Proxy(FakeProxy.Protocol.Socks5, options: new ServerOptions { Faults = FaultOptions.DisconnectAfter(32, DisconnectMode.Close) });

        await Assert.ThrowsAnyAsync<Exception>(() => new ProxiedStatusClient(Api, proxy.Uri).GetStatusAsync());

        proxy.Server.Transcript.AssertSentMessage("SUCCEEDED");
    }
}
