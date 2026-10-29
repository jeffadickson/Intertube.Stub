using Intertube.Stub.Sftp;
using Intertube.Stub.Sftp.Ssh;
using static Intertube.Stub.Tests.Sftp.RawSftpClient;

namespace Intertube.Stub.Tests.Sftp;

/// <summary>
/// The server's options and SFTP request handling, driven with <see cref="RawSftpClient"/> so that
/// requests no real client would send (bad handles, truncated packets, too many handles) can be tested.
/// </summary>
public sealed class SftpServerTests
{
    [Fact]
    public void Start_UnknownCipher_ThrowsArgumentExceptionListingTheSupportedOnes()
    {
        var ex = Assert.Throws<ArgumentException>(() => SftpServer.Start(new SftpServerOptions { Ciphers = ["aes128-gcm@openssh.com", "3des-cbc"] }));

        Assert.Equal("options", ex.ParamName);
        Assert.Contains("\"3des-cbc\"", ex.Message);
        Assert.Contains("Ciphers", ex.Message);
        Assert.All(PacketCipher.Ciphers, cipher => Assert.Contains(cipher, ex.Message));
    }

    [Fact]
    public void Start_UnknownMac_ThrowsArgumentExceptionListingTheSupportedOnes()
    {
        var ex = Assert.Throws<ArgumentException>(() => SftpServer.Start(new SftpServerOptions { Macs = ["hmac-sha2-256", "hmac-sha1"] }));

        Assert.Equal("options", ex.ParamName);
        Assert.Contains("\"hmac-sha1\"", ex.Message);
        Assert.Contains("Macs", ex.Message);
        Assert.All(PacketCipher.Macs, mac => Assert.Contains(mac, ex.Message));
    }

    [Fact]
    public async Task RestrictedCiphersAndMacs_AreOfferedExactly_InOrder()
    {
        await using var server = SftpServer.Start(new SftpServerOptions { Ciphers = ["aes256-ctr", "aes128-gcm@openssh.com"], Macs = ["hmac-sha2-512"] });
        await using var client = await ConnectAsync(server);

        Assert.Equal(new[] { "aes256-ctr", "aes128-gcm@openssh.com" }, client.OfferedCiphers);
        Assert.Equal(new[] { "hmac-sha2-512" }, client.OfferedMacs);
        Assert.Equal(104, (await client.RequestAsync(RealPath, w => w.String("."))).Type);
    }

    [Fact]
    public async Task UnknownHandle_IsRecorded_AndAnsweredWithFailure()
    {
        await using var server = SftpServer.Start();
        await using var client = await ConnectAsync(server);

        foreach (var (type, fields) in BadHandleRequests("bogus"))
        {
            var reply = await client.RequestAsync(type, fields);
            Assert.Equal((SftpStatus.Failure, "Invalid handle."), (reply.Status, reply.Message));
        }

        Assert.Equal(
            new[] { SftpOperation.Close, SftpOperation.Read, SftpOperation.Write, SftpOperation.FStat, SftpOperation.FSetStat, SftpOperation.ReadDir, SftpOperation.Fsync },
            server.Requests.Select(r => r.Operation));
        Assert.All(server.Requests, r => Assert.Equal("", r.Path));
        Assert.Equal((0L, 10), (server.Requests[1].Offset, server.Requests[1].Length));
        Assert.Equal(4, server.Requests[2].Length);
        Assert.Empty(server.Errors);
    }

    [Fact]
    public async Task UnknownHandle_CanBeFailedByRulesAndIntercept()
    {
        await using var server = SftpServer.Start();
        await using var client = await ConnectAsync(server);
        server.FailNext(SftpOperation.Close, status: SftpStatus.NoSuchFile);
        server.FailAlways(SftpOperation.Read);
        server.Intercept = request => ValueTask.FromResult<SftpStatus?>(request is { Operation: SftpOperation.FStat, Path: "" } ? SftpStatus.Ok : null);

        var close = await client.RequestAsync(Close, w => w.String("bogus"));
        var closeAgain = await client.RequestAsync(Close, w => w.String("bogus"));
        var read = await client.RequestAsync(Read, w => w.String("bogus").UInt64(0).UInt32(10));
        var fstat = await client.RequestAsync(FStat, w => w.String("bogus"));

        Assert.Equal((SftpStatus.NoSuchFile, "Injected failure: NoSuchFile."), (close.Status, close.Message));
        Assert.Equal((SftpStatus.Failure, "Invalid handle."), (closeAgain.Status, closeAgain.Message));
        Assert.Equal(SftpStatus.PermissionDenied, read.Status);
        Assert.Equal((101, SftpStatus.Ok), (fstat.Type, fstat.Status));
        Assert.Equal(2, server.RequestsOf(SftpOperation.Close).Count);
    }

    [Fact]
    public async Task HandleOfTheWrongKind_IsRecordedWithItsPath_AndAnsweredWithFailure()
    {
        await using var server = SftpServer.Start(new SftpServerOptions { FileSystem = new InMemorySftpFileSystem().AddFile("/f", "data").AddDirectory("/d") });
        await using var client = await ConnectAsync(server);
        var file = await client.OpenFileAsync("/f");
        var directory = await client.OpenDirectoryAsync("/d");

        var readDirectory = await client.RequestAsync(Read, w => w.String(directory).UInt64(0).UInt32(10));
        var writeDirectory = await client.RequestAsync(Write, w => w.String(directory).UInt64(0).String("x"));
        var listFile = await client.RequestAsync(ReadDir, w => w.String(file));

        Assert.All(new[] { readDirectory, writeDirectory, listFile }, reply => Assert.Equal((SftpStatus.Failure, "Invalid handle."), (reply.Status, reply.Message)));
        Assert.Equal("/d", Assert.Single(server.RequestsOf(SftpOperation.Read)).Path);
        Assert.Equal("/d", Assert.Single(server.RequestsOf(SftpOperation.Write)).Path);
        Assert.Equal("/f", Assert.Single(server.RequestsOf(SftpOperation.ReadDir)).Path);
    }

    [Fact]
    public async Task HandleLimit_CountsFilesAndDirectories_AndAppliesToOpenAndOpenDir()
    {
        await using var server = SftpServer.Start(new SftpServerOptions { FileSystem = new InMemorySftpFileSystem().AddFile("/f", "data").AddDirectory("/d") });
        await using var client = await ConnectAsync(server);
        var handles = new List<string>();
        for (var i = 0; i < 1024; i++)
            handles.Add(i % 2 == 0 ? await client.OpenFileAsync("/f") : await client.OpenDirectoryAsync("/d"));

        var open = await client.RequestAsync(Open, w => w.String("/f").UInt32(FlagRead).UInt32(0));
        var openDirectory = await client.RequestAsync(OpenDir, w => w.String("/d"));
        Assert.Equal((SftpStatus.Failure, "Too many open handles."), (open.Status, open.Message));
        Assert.Equal((SftpStatus.Failure, "Too many open handles."), (openDirectory.Status, openDirectory.Message));

        Assert.Equal(SftpStatus.Ok, (await client.RequestAsync(Close, w => w.String(handles[0]))).Status);
        await client.OpenDirectoryAsync("/d");
        Assert.Equal(SftpStatus.Failure, (await client.RequestAsync(OpenDir, w => w.String("/d"))).Status);
        Assert.Empty(server.Errors);
    }

    [Fact]
    public async Task MalformedRequests_AreAnsweredWithBadMessage_AndTheSessionContinues()
    {
        await using var server = SftpServer.Start(new SftpServerOptions { FileSystem = new InMemorySftpFileSystem().AddFile("/f", "data") });
        await using var client = await ConnectAsync(server);

        // A path whose length overruns the packet; a WRITE with less data than it declares; a packet too short for an id.
        await client.SendAsync(new SshWriter(Open).UInt32(7).UInt32(1000).Raw("abc"u8).ToArray());
        var overrun = await client.ReceiveAsync();
        await client.SendAsync(new SshWriter(Write).UInt32(8).String("h").UInt64(0).UInt32(100).Raw(new byte[10]).ToArray());
        var shortWrite = await client.ReceiveAsync();
        await client.SendAsync([Stat, 0, 0]);
        var noId = await client.ReceiveAsync();

        Assert.All(new[] { (overrun, 7u), (shortWrite, 8u), (noId, 0u) }, pair =>
        {
            var (packet, id) = pair;
            Assert.Equal(101, packet[0]);
            Assert.Equal(id, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(1)));
            Assert.Equal(SftpStatus.BadMessage, new Reply(packet).Status);
        });
        Assert.Empty(server.Requests);

        Assert.Equal(SftpStatus.Ok, (await client.RequestAsync(Stat, w => w.String("/f"))).Status);
        Assert.Equal(SftpOperation.Stat, Assert.Single(server.Requests).Operation);
        Assert.Empty(server.Errors);
    }

    /// <summary>Every request that names a handle, with <paramref name="handle"/> as that handle.</summary>
    private static IEnumerable<(byte Type, Action<SshWriter> Fields)> BadHandleRequests(string handle) =>
    [
        (Close, w => w.String(handle)),
        (Read, w => w.String(handle).UInt64(0).UInt32(10)),
        (Write, w => w.String(handle).UInt64(0).String("data")),
        (FStat, w => w.String(handle)),
        (FSetStat, w => w.String(handle).UInt32(0)),
        (ReadDir, w => w.String(handle)),
        (Extended, w => w.String("fsync@openssh.com").String(handle)),
    ];
}
