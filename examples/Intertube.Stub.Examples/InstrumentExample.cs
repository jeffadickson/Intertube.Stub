using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Xunit.Abstractions;

namespace Intertube.Stub.Examples;

// ---- System under test -----------------------------------------------------------------------

/// <summary>A lab multimeter driven with SCPI over raw TCP (port 5025 on real hardware).</summary>
public sealed class Multimeter : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly TimeSpan _timeout;

    private Multimeter(TcpClient tcp, TimeSpan timeout)
    {
        _tcp = tcp;
        _timeout = timeout;
        _reader = new StreamReader(tcp.GetStream());
        _writer = new StreamWriter(tcp.GetStream()) { NewLine = "\n", AutoFlush = true };
    }

    public static async Task<Multimeter> ConnectAsync(string host, int port, TimeSpan timeout)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        return new Multimeter(tcp, timeout);
    }

    public Task<string> IdentifyAsync() => QueryAsync("*IDN?");

    public async Task<double> MeasureDcVoltsAsync() =>
        double.Parse(await QueryAsync("MEAS:VOLT:DC?"), NumberStyles.Float, CultureInfo.InvariantCulture);

    private async Task<string> QueryAsync(string command)
    {
        await _writer.WriteLineAsync(command);
        using var timeout = new CancellationTokenSource(_timeout);
        try
        {
            // Not StreamReader.ReadLineAsync: at end of stream it returns a truncated last line as
            // if it were complete. The fault matrix below caught exactly that.
            var line = new StringBuilder();
            var one = new char[1];
            while (await _reader.ReadAsync(one, timeout.Token) == 1)
            {
                if (one[0] == '\n') return line.ToString().TrimEnd('\r');
                line.Append(one[0]);
            }
            throw new IOException($"Instrument closed the connection mid-reply after \"{line}\".");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No reply to {command} within {_timeout}.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}

// ---- Tests -----------------------------------------------------------------------------------

/// <summary>
/// Hardware-in-the-loop without the hardware. A LineResponder plays the instrument, and the
/// server's live log goes to xUnit's test output.
/// </summary>
public class InstrumentExample(ITestOutputHelper output)
{
    private LineResponder Instrument(string volts = "+1.23456789E+00") => new LineResponder()
        .On("*IDN?", "KEYSIGHT TECHNOLOGIES,34465A,MY12345678,A.03.02")
        .On("MEAS:VOLT:DC?", volts)
        .Otherwise("-113,\"Undefined header\"");

    private ServerOptions Options(FaultOptions? faults = null) =>
        new() { NewLine = "\n", Log = output.WriteLine, Faults = faults ?? FaultOptions.None };

    [Fact]
    public async Task IdentifiesAndMeasures()
    {
        await using var server = TcpServer.Listen(0, Instrument(), Options());
        await using var dmm = await Multimeter.ConnectAsync(server.Host, server.Port, TimeSpan.FromSeconds(1));

        Assert.StartsWith("KEYSIGHT", await dmm.IdentifyAsync());
        Assert.Equal(1.23456789, await dmm.MeasureDcVoltsAsync(), 8);
        server.Transcript.AssertReceivedLinesInOrder("*IDN?", "MEAS:VOLT:DC?");
    }

    [Fact]
    public async Task OverloadReading_IsParsedAsRealInstrumentsSendIt()
    {
        await using var server = TcpServer.Listen(0, Instrument("+9.90000000E+37"), Options());
        await using var dmm = await Multimeter.ConnectAsync(server.Host, server.Port, TimeSpan.FromSeconds(1));

        Assert.Equal(9.9e37, await dmm.MeasureDcVoltsAsync());
    }

    [Fact]
    public async Task GarbledReading_IsRejected()
    {
        await using var server = TcpServer.Listen(0, Instrument("+1.2#4E+00"), Options());
        await using var dmm = await Multimeter.ConnectAsync(server.Host, server.Port, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<FormatException>(() => dmm.MeasureDcVoltsAsync());
    }

    public static TheoryData<string, bool> FaultMatrix => new()
    {
        { "none", true },
        { "fragment-1", true },
        { "latency-50ms", true },
        { "connect-delay-100ms", true },
        { "slower-than-timeout", false },
        { "disconnect-mid-reply", false },
        { "reset-mid-reply", false },
    };

    /// <summary>One table drives the client through every failure mode it must survive or report.</summary>
    [Theory]
    [MemberData(nameof(FaultMatrix))]
    public async Task FaultMatrix_EitherWorksOrFailsCleanly(string fault, bool shouldSucceed)
    {
        var faults = fault switch
        {
            "fragment-1" => FaultOptions.Fragment(1),
            "latency-50ms" => FaultOptions.Latency(TimeSpan.FromMilliseconds(50)),
            "connect-delay-100ms" => new FaultOptions { ConnectDelay = TimeSpan.FromMilliseconds(100) },
            "slower-than-timeout" => FaultOptions.Latency(TimeSpan.FromSeconds(5)),
            "disconnect-mid-reply" => FaultOptions.DisconnectAfter(10, DisconnectMode.Close),
            "reset-mid-reply" => FaultOptions.DisconnectAfter(10),
            _ => FaultOptions.None,
        };
        await using var server = TcpServer.Listen(0, Instrument(), Options(faults));
        // Generous next to the faults that should succeed (a busy CI machine adds hundreds of ms),
        // and well short of the one that should time out.
        await using var dmm = await Multimeter.ConnectAsync(server.Host, server.Port, TimeSpan.FromMilliseconds(1500));

        var attempt = dmm.IdentifyAsync();
        if (shouldSucceed)
        {
            Assert.StartsWith("KEYSIGHT", await attempt);
        }
        else
        {
            var ex = await Record.ExceptionAsync(() => attempt);
            Assert.True(ex is TimeoutException or IOException, $"unexpected {ex?.GetType().Name}: {ex?.Message}");
        }
    }
}
