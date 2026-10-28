using System.Net.Http.Json;
using System.Net.Sockets;
using Intertube.Stub.Examples.Support;

namespace Intertube.Stub.Examples;

// ---- System under test -----------------------------------------------------------------------

/// <summary>Talks to the Docker Engine API over its Unix socket, like the docker CLI does.</summary>
public sealed class DockerClient : IDisposable
{
    private readonly HttpClient _http;

    public DockerClient(string socketPath) =>
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            },
        })
        { BaseAddress = new Uri("http://docker/v1.43/") };

    public sealed record Container(string Id, string[] Names, string Image, string State);

    public async Task<IReadOnlyList<Container>> ListRunningAsync() =>
        (await _http.GetFromJsonAsync<Container[]>("containers/json"))!.Where(c => c.State == "running").ToArray();

    public async Task RestartAsync(string id)
    {
        using var response = await _http.PostAsync($"containers/{id}/restart?t=5", null);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}

// ---- Tests -----------------------------------------------------------------------------------

/// <summary>
/// A Unix-socket service mocked without Docker installed. Intertube.Stub's UnixServer also works on
/// Windows 10+, where Python's socketserver offers no Unix server at all.
/// </summary>
public class DockerUnixSocketExample
{
    [Fact]
    public async Task ListsOnlyRunningContainers()
    {
        await using var docker = UnixServer.Listen(null, HttpStub.Respond(_ => HttpStub.Response(200, """
            [{"Id":"a1","Names":["/web"],"Image":"nginx","State":"running"},
             {"Id":"b2","Names":["/batch"],"Image":"job","State":"exited"}]
            """)));
        using var client = new DockerClient(docker.Path);

        var running = await client.ListRunningAsync();

        Assert.Equal("web", Assert.Single(running).Names[0].TrimStart('/'));
        docker.Transcript.AssertReceivedLine("GET /v1.43/containers/json HTTP/1.1");
    }

    [Fact]
    public async Task Restart_PostsToTheRightEndpoint()
    {
        await using var docker = UnixServer.Listen(null, HttpStub.Respond(request =>
            request is { Method: "POST", Target: "/v1.43/containers/a1/restart?t=5" }
                ? HttpStub.Response(204)
                : HttpStub.Response(404, """{"message":"no such container"}""")));
        using var client = new DockerClient(docker.Path);

        await client.RestartAsync("a1");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.RestartAsync("zz"));
    }
}
