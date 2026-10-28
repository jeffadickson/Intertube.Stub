using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace Intertube.Stub.Examples;

// ---- Systems under test ----------------------------------------------------------------------

/// <summary>Requests a stock quote over a line protocol, retrying with exponential backoff.</summary>
public sealed class QuoteClient(string host, int port, int attempts = 3)
{
    public async Task<decimal> GetPriceAsync(string symbol)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port);
                var stream = tcp.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"QUOTE {symbol}\n"));
                using var reader = new StreamReader(stream);
                var reply = await reader.ReadLineAsync() ?? throw new EndOfStreamException();
                return decimal.Parse(reply.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is IOException or SocketException && attempt < attempts)
            {
                await Task.Delay(10 * (1 << attempt));
            }
        }
    }
}

/// <summary>Connects over TLS only to a server presenting a certificate with a pinned SHA-256 hash.</summary>
public sealed class PinnedTlsClient(string host, int port, string pinnedSha256)
{
    public async Task<string> FetchBannerAsync()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())) == pinnedSha256);
        await ssl.AuthenticateAsClientAsync("localhost");
        using var reader = new StreamReader(ssl);
        return await reader.ReadLineAsync() ?? "";
    }
}

/// <summary>Length-prefixed binary RPC: 4-byte big-endian length, then the payload.</summary>
public sealed class FrameClient(string host, int port)
{
    public const int MaxFrame = 1024 * 1024;

    public async Task<byte[]> CallAsync(byte[] request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        var stream = tcp.GetStream();

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, request.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(request);

        await stream.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > MaxFrame) throw new InvalidDataException($"Frame length {length} out of range.");
        var response = new byte[length];
        await stream.ReadExactlyAsync(response);
        return response;
    }
}

// ---- Tests -----------------------------------------------------------------------------------

public class RetryExample
{
    [Fact]
    public async Task RecoversFromTwoResets()
    {
        await using var server = TcpServer.Listen(0, connection => connection.Id <= 2
            ? Handler.Reset(connection)
            : Handler.Lines(line => $"{line.Split(' ')[1]} 187.42")(connection));

        Assert.Equal(187.42m, await new QuoteClient(server.Host, server.Port).GetPriceAsync("AAPL"));
        server.Transcript.AssertConnectionCount(3);
    }

    [Fact]
    public async Task GivesUpAfterThreeAttempts()
    {
        await using var server = TcpServer.Listen(0, Handler.Close);

        await Assert.ThrowsAnyAsync<IOException>(() => new QuoteClient(server.Host, server.Port).GetPriceAsync("AAPL"));
        server.Transcript.AssertConnectionCount(3);
    }
}

public class TlsPinningExample
{
    [Fact]
    public async Task AcceptsThePinnedCertificate()
    {
        var tls = TlsOptions.SelfSigned();
        await using var server = TcpServer.Listen(0, Handler.Send("220 secure banner\r\n"), new ServerOptions { Tls = tls });
        var pin = Convert.ToHexString(SHA256.HashData(tls.Certificate.RawData));

        Assert.Equal("220 secure banner", await new PinnedTlsClient(server.Host, server.Port, pin).FetchBannerAsync());
    }

    [Fact]
    public async Task RejectsAnyOtherCertificate()
    {
        await using var impostor = TcpServer.Listen(0, Handler.Send("220 evil\r\n"), new ServerOptions { Tls = TlsOptions.SelfSigned() });
        var pinOfTheRealServer = Convert.ToHexString(SHA256.HashData(TlsOptions.CreateSelfSignedCertificate().RawData));

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            new PinnedTlsClient(impostor.Host, impostor.Port, pinOfTheRealServer).FetchBannerAsync());
    }
}

/// <summary>A binary protocol: the stub parses frames straight off Intertube.Stub's PipeReader.</summary>
public class BinaryProtocolExample
{
    /// <summary>Uppercases each frame it receives.</summary>
    private static async Task UppercaseServer(Connection connection)
    {
        var header = await connection.ReadExactlyAsync(4);
        var payload = await connection.ReadExactlyAsync(BinaryPrimitives.ReadInt32BigEndian(header));
        var reply = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).ToUpperInvariant());
        BinaryPrimitives.WriteInt32BigEndian(header, reply.Length);
        await connection.WriteAsync((byte[])[.. header, .. reply]);
    }

    [Fact]
    public async Task RoundTrip()
    {
        await using var server = TcpServer.Listen(0, UppercaseServer);
        Assert.Equal("HELLO"u8.ToArray(), await new FrameClient(server.Host, server.Port).CallAsync("hello"u8.ToArray()));
    }

    [Fact]
    public async Task ReassemblesAFrameDeliveredOneByteAtATime()
    {
        await using var server = TcpServer.Listen(0, UppercaseServer, new ServerOptions { Faults = FaultOptions.Fragment(1) });
        var request = Encoding.UTF8.GetBytes(new string('x', 2000));

        Assert.Equal(2000, (await new FrameClient(server.Host, server.Port).CallAsync(request)).Length);
    }

    [Fact]
    public async Task TruncatedFrame_IsEndOfStream()
    {
        await using var server = TcpServer.Listen(0, UppercaseServer,
            new ServerOptions { Faults = FaultOptions.DisconnectAfter(6, DisconnectMode.Close) });

        await Assert.ThrowsAsync<EndOfStreamException>(() => new FrameClient(server.Host, server.Port).CallAsync("hello"u8.ToArray()));
    }

    [Fact]
    public async Task InsaneLengthPrefix_IsRejectedBeforeAllocating()
    {
        // Corrupt only the length prefix: a transform with full control over the bytes on the wire.
        var faults = new FaultOptions
        {
            TransformWrite = data =>
            {
                var copy = data.ToArray();
                copy[0] = 0x7F;
                return copy;
            },
        };
        await using var server = TcpServer.Listen(0, UppercaseServer, new ServerOptions { Faults = faults });

        await Assert.ThrowsAsync<InvalidDataException>(() => new FrameClient(server.Host, server.Port).CallAsync("hello"u8.ToArray()));
    }
}
