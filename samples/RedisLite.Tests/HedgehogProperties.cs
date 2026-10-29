using System.Diagnostics;
using System.Globalization;
using Hedgehog;
using Hedgehog.Linq;
using Intertube.Stub;
using RedisLite.Cli;
using Gen = Hedgehog.Linq.Gen;
using Property = Hedgehog.Linq.Property;
using Range = Hedgehog.Linq.Range;

namespace RedisLite.Tests;

/// <summary>
/// Hedgehog properties. Hedgehog shrinks integrated with generation, so a failing command sequence
/// shrinks to a minimal one while keeping every generated value valid, which matters for
/// model-based tests whose inputs depend on each other.
/// </summary>
public class HedgehogProperties
{
    private abstract record Op;
    private sealed record SetOp(string Key, string Value) : Op;
    private sealed record GetOp(string Key) : Op;
    private sealed record DelOp(string Key) : Op;
    private sealed record IncrOp(string Key, long By) : Op;

    private static readonly Gen<string> Key = Gen.Item("a", "b", "counter");

    private static readonly Gen<string> Value = Gen.Choice(
        Gen.AlphaNumeric.String(Range.Constant(0, 24)),
        Gen.Int64(Range.Constant(-1_000_000L, 1_000_000L)).Select(n => n.ToString(CultureInfo.InvariantCulture)),
        Gen.Unicode.String(Range.Constant(0, 8)));

    private static readonly Gen<Op> Operation = Gen.Choice(
        from k in Key from v in Value select (Op)new SetOp(k, v),
        Key.Select(k => (Op)new GetOp(k)),
        Key.Select(k => (Op)new DelOp(k)),
        from k in Key from amount in Gen.Int64(Range.Constant(-100L, 100L)) select (Op)new IncrOp(k, amount));

    /// <summary>
    /// Model-based test: random command sequences produce the same results through
    /// client, socket and FakeRedis as against a plain dictionary, while Intertube.Stub dribbles every
    /// reply out in fragments of a random size so the client's reassembly is exercised as well.
    /// </summary>
    [Fact]
    public void Client_AgreesWithModel_UnderRandomFragmentation()
    {
        var property =
            from ops in Operation.List(Range.LinearInt32(1, 40)).ForAll()
            from fragment in Gen.Int32(Range.Constant(1, 24)).ForAll()
            select AgreesWithModel(ops, fragment).GetAwaiter().GetResult();

        property.Check(PropertyConfig.Default.WithTests(40));
    }

    private static async Task<bool> AgreesWithModel(List<Op> ops, int fragment)
    {
        await using var fake = FakeRedis.Start(new ServerOptions { Faults = FaultOptions.Fragment(fragment) });
        // A generous timeout: this property is about agreement, not timing. Hedgehog runs it synchronously,
        // so on a loaded CI runner thread-pool starvation can delay a reply that was already sent past the
        // usual 3 s, and the timeout would then (correctly) reconnect and resend.
        await using var client = new RedisClient(fake.ClientOptions with { CommandTimeout = TimeSpan.FromSeconds(30) });
        var model = new Dictionary<string, string>();

        foreach (var op in ops)
        {
            switch (op)
            {
                case SetOp(var k, var v):
                    model[k] = v;
                    if (!await client.SetAsync(k, v)) return false;
                    break;
                case GetOp(var k):
                    if (await client.GetAsync(k) != model.GetValueOrDefault(k)) return false;
                    break;
                case DelOp(var k):
                    if (await client.DeleteAsync(k) != (model.Remove(k) ? 1 : 0)) return false;
                    break;
                case IncrOp(var k, var by):
                {
                    long? expected = null;
                    if (!model.TryGetValue(k, out var current)) expected = by;
                    else if (long.TryParse(current, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) expected = n + by;

                    try
                    {
                        var actual = await client.IncrementAsync(k, by);
                        if (actual != expected) return false;
                        model[k] = actual.ToString(CultureInfo.InvariantCulture);
                    }
                    catch (RedisServerException) when (expected is null)
                    {
                        // Not an integer: both sides refuse.
                    }
                    break;
                }
            }
        }
        // Throw rather than return false here, so a failure report says which invariant broke.
        if (fake.Server.Errors.Count > 0) throw new AggregateException("FakeRedis handler failed.", fake.Server.Errors);
        if (client.ConnectionsOpened != 1) throw new InvalidOperationException($"The client reconnected ({client.ConnectionsOpened} connections).\n{fake.Server.Transcript}");
        return true;
    }

    /// <summary>
    /// Whatever corruption Intertube.Stub injects into replies, the client never hangs and never throws
    /// anything but a RedisLite exception: wrong lengths become timeouts, bad bytes become
    /// protocol errors. (RESP has no checksums, so a flipped payload bit can still go unnoticed.)
    /// </summary>
    [Fact]
    public void Client_SurvivesCorruptReplies()
    {
        var property =
            from seed in Gen.Int32(Range.Constant(0, 1_000_000)).ForAll()
            from ratePerMille in Gen.Int32(Range.Constant(1, 80)).ForAll()
            select SurvivesCorruption(seed, ratePerMille / 1000.0).GetAwaiter().GetResult();

        property.Check(PropertyConfig.Default.WithTests(25));
    }

    private static async Task<bool> SurvivesCorruption(int seed, double rate)
    {
        await using var fake = FakeRedis.Start(new ServerOptions { Faults = FaultOptions.Corrupt(rate, seed) });
        await using var client = new RedisClient(fake.ClientOptions with { CommandTimeout = TimeSpan.FromMilliseconds(300), MaxRetries = 0 });

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            try
            {
                await client.SetAsync($"k{i}", new string('x', i * 10));
                await client.GetAsync($"k{i}");
                await client.IncrementAsync("n");
            }
            catch (RedisException)
            {
                // Expected under corruption.
            }
        }
        return watch.Elapsed < TimeSpan.FromSeconds(10);
    }

    [Fact]
    public void Tokenizer_SplitInvertsQuote()
    {
        var property =
            from tokens in Gen.Unicode.String(Range.Constant(0, 12)).List(Range.LinearInt32(0, 8)).ForAll()
            select Tokenizer.Split(string.Join(" ", tokens.Select(Tokenizer.Quote))).SequenceEqual(tokens);

        property.Check(PropertyConfig.Default.WithTests(500));
    }

    [Fact]
    public void Tokenizer_RejectsOnlyWithFormatException()
    {
        var alphabet = Gen.Item('a', ' ', '"', '\\', 'n', '\t');
        var property =
            from line in alphabet.String(Range.LinearInt32(0, 30)).ForAll()
            select Splits(line);

        property.Check(PropertyConfig.Default.WithTests(1000));

        static bool Splits(string line)
        {
            try
            {
                Tokenizer.Split(line);
            }
            catch (FormatException)
            {
            }
            return true;
        }
    }
}
