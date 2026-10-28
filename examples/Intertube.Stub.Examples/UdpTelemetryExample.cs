using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Intertube.Stub.Examples;

// ---- Systems under test ----------------------------------------------------------------------

public enum Severity { Emergency, Alert, Critical, Error, Warning, Notice, Informational, Debug }

/// <summary>RFC 5424 syslog over UDP.</summary>
public sealed class SyslogSender(string host, int port, string appName) : IDisposable
{
    private readonly UdpClient _udp = new();

    public async Task LogAsync(Severity severity, string message)
    {
        const int userFacility = 1;
        var line = $"<{userFacility * 8 + (int)severity}>1 {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} " +
                   $"{Environment.MachineName} {appName} {Environment.ProcessId} - - {message}";
        await _udp.SendAsync(Encoding.UTF8.GetBytes(line), host, port);
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>Fire-and-forget StatsD metrics: losing packets must never hurt the application.</summary>
public sealed class Metrics(string host, int port) : IDisposable
{
    private readonly UdpClient _udp = new();

    public Task IncrementAsync(string name) => SendAsync($"{name}:1|c");

    public Task TimingAsync(string name, TimeSpan duration) =>
        SendAsync($"{name}:{((long)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)}|ms");

    private async Task SendAsync(string metric)
    {
        try
        {
            await _udp.SendAsync(Encoding.ASCII.GetBytes(metric), host, port);
        }
        catch (SocketException)
        {
            // Metrics are best effort.
        }
    }

    public void Dispose() => _udp.Dispose();
}

// ---- Tests -----------------------------------------------------------------------------------

public class UdpTelemetryExample
{
    [Fact]
    public async Task Syslog_UsesRfc5424Format()
    {
        await using var collector = UdpServer.Listen(0, _ => Task.CompletedTask);
        using var syslog = new SyslogSender("127.0.0.1", collector.Port, "checkout");

        await syslog.LogAsync(Severity.Error, "payment failed for order 1234");

        await collector.Transcript.WaitForReceivedAsync(1);
        Assert.Matches(new Regex(@"^<11>1 \d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z \S+ checkout \d+ - - payment failed for order 1234$"),
            collector.Transcript.ReceivedTexts[0]);
    }

    [Fact]
    public async Task Metrics_AreWellFormed()
    {
        await using var statsd = UdpServer.Listen(0, _ => Task.CompletedTask);
        using var metrics = new Metrics("127.0.0.1", statsd.Port);

        await metrics.IncrementAsync("orders.placed");
        await metrics.TimingAsync("orders.latency", TimeSpan.FromMilliseconds(42.9));

        await statsd.Transcript.WaitForReceivedAsync(2);
        Assert.Equal(["orders.placed:1|c", "orders.latency:42|ms"], statsd.Transcript.ReceivedTexts);
    }

    /// <summary>Reproducible 40% packet loss (seeded), something real networks only offer randomly.</summary>
    [Fact]
    public async Task Metrics_TolerateHeavyPacketLoss()
    {
        await using var statsd = UdpServer.Listen(0, _ => Task.CompletedTask, new UdpServerOptions { DropRate = 0.4, Seed = 7 });
        using var metrics = new Metrics("127.0.0.1", statsd.Port);

        for (var i = 0; i < 100; i++) await metrics.IncrementAsync("requests");

        await statsd.Transcript.WaitUntilAsync(() => statsd.Transcript.Records.Count == 100, "all 100 datagrams");
        Assert.InRange(statsd.Transcript.Received.Count, 40, 80);
    }

    [Fact]
    public async Task Metrics_SurviveAMissingCollector()
    {
        int port;
        await using (var gone = UdpServer.Listen(0, _ => Task.CompletedTask)) port = gone.Port;
        using var metrics = new Metrics("127.0.0.1", port);

        for (var i = 0; i < 10; i++) await metrics.IncrementAsync("still.fine");
    }
}
