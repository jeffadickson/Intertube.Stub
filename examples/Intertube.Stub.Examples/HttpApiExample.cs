using System.Net;
using System.Net.Http.Json;
using Intertube.Stub.Examples.Support;

namespace Intertube.Stub.Examples;

// ---- System under test -----------------------------------------------------------------------

public sealed record Forecast(string City, double TemperatureC, string Summary);

/// <summary>A typical typed HttpClient wrapper: retries 503s, honours Retry-After.</summary>
public sealed class WeatherClient(HttpClient http)
{
    public async Task<Forecast> GetForecastAsync(string city, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await http.GetAsync($"forecast?city={Uri.EscapeDataString(city)}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 2)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(50), cancellationToken);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<Forecast>(cancellationToken))!;
        }
    }
}

// ---- Tests -----------------------------------------------------------------------------------

/// <summary>
/// Mocking an HTTP API at the socket level. Unlike an HttpMessageHandler fake, this exercises the
/// real SocketsHttpHandler: connection handling, header parsing, timeouts and broken responses.
/// </summary>
public class HttpApiExample
{
    private static WeatherClient ClientFor(TcpServer server, TimeSpan? timeout = null) =>
        new(new HttpClient { BaseAddress = new Uri($"http://{server.EndPoint}/"), Timeout = timeout ?? TimeSpan.FromSeconds(5) });

    [Fact]
    public async Task ReturnsForecast_AndSendsTheExpectedRequest()
    {
        await using var server = TcpServer.Listen(0, HttpStub.Respond(_ =>
            HttpStub.Response(200, """{"city":"Oslo","temperatureC":-3.5,"summary":"Snow"}""")));

        var forecast = await ClientFor(server).GetForecastAsync("Oslo");

        Assert.Equal(new Forecast("Oslo", -3.5, "Snow"), forecast);
        server.Transcript.AssertReceivedLine("GET /forecast?city=Oslo HTTP/1.1");
        server.Transcript.AssertReceivedLine("Host: 127.0.0.1:*");
    }

    [Fact]
    public async Task RetriesServiceUnavailable_ThenSucceeds()
    {
        var calls = 0;
        await using var server = TcpServer.Listen(0, HttpStub.Respond(_ => Interlocked.Increment(ref calls) <= 2
            ? HttpStub.Response(503, "", "text/plain", "Retry-After: 0")
            : HttpStub.Response(200, """{"city":"Rome","temperatureC":21,"summary":"Sunny"}""")));

        var forecast = await ClientFor(server).GetForecastAsync("Rome");

        Assert.Equal("Sunny", forecast.Summary);
        server.Transcript.AssertConnectionCount(3); // "Connection: close" forces a fresh connection each time
    }

    [Fact]
    public async Task GivesUpAfterThreeUnavailableResponses()
    {
        await using var server = TcpServer.Listen(0, HttpStub.Respond(_ => HttpStub.Response(503, "", "text/plain", "Retry-After: 0")));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => ClientFor(server).GetForecastAsync("Paris"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    /// <summary>A response promising 100 bytes that delivers 10: the kind of bug a mocked handler can never show.</summary>
    [Fact]
    public async Task TruncatedBody_IsAnError()
    {
        await using var server = TcpServer.Listen(0, Handler.Send(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{\"city\":\"X"));

        await Assert.ThrowsAnyAsync<Exception>(() => ClientFor(server).GetForecastAsync("X"));
    }

    [Fact]
    public async Task GarbageStatusLine_IsAnError()
    {
        await using var server = TcpServer.Listen(0, Handler.Send("SSH-2.0-OpenSSH_9.6\r\n"));

        await Assert.ThrowsAsync<HttpRequestException>(() => ClientFor(server).GetForecastAsync("X"));
    }

    [Fact]
    public async Task ServerThatNeverAnswers_HitsHttpClientTimeout()
    {
        await using var server = TcpServer.Listen(0, Handler.Hang);

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => ClientFor(server, TimeSpan.FromMilliseconds(300)).GetForecastAsync("X"));
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task ByteAtATimeResponse_IsStillParsed()
    {
        await using var server = TcpServer.Listen(0,
            HttpStub.Respond(_ => HttpStub.Response(200, """{"city":"Lima","temperatureC":18,"summary":"Fog"}""")),
            new ServerOptions { Faults = FaultOptions.Fragment(1) });

        Assert.Equal("Fog", (await ClientFor(server).GetForecastAsync("Lima")).Summary);
    }
}
