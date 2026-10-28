using System.Buffers;
using FsCheck;
using FsCheck.Fluent;

namespace RedisLite.Tests;

/// <summary>FsCheck generators and shrinkers for RESP values and connection options.</summary>
public static class Generators
{
    /// <summary>Valid Unicode text: FsCheck's raw strings include lone surrogates, which UTF-8 cannot carry.</summary>
    public static Gen<string> Text { get; } =
        ArbMap.Default.GeneratorFor<string>().Select(s => new string((s ?? "").Where(c => !char.IsSurrogate(c)).ToArray()));

    public static Gen<string> NonEmptyText { get; } = Text.Where(s => s.Length > 0);

    public static Gen<string> LineText { get; } = Text.Select(s => s.Replace("\r", "").Replace("\n", ""));

    private static Gen<RespValue> Leaf { get; } = Gen.OneOf(
        LineText.Select(s => (RespValue)new RespSimpleString(s)),
        LineText.Select(s => (RespValue)new RespError(s)),
        ArbMap.Default.GeneratorFor<long>().Select(n => (RespValue)new RespInteger(n)),
        ArbMap.Default.GeneratorFor<byte[]>().Select(b => (RespValue)new RespBulkString(b ?? [])),
        Gen.Elements<RespValue>(RedisLite.RespValue.Nil, new RespArray(null), new RespArray([])));

    public static Gen<RespValue> Value { get; } = Gen.Sized(Sized);

    private static Gen<RespValue> Sized(int size) => size <= 1
        ? Leaf
        : Gen.Frequency(
            (3, Leaf),
            (1, Gen.Choose(0, 5).SelectMany(n => Gen.ListOf(Sized(size / 3), n)).Select(items => (RespValue)new RespArray([.. items]))));

    /// <summary>Shrinks toward smaller, flatter values so failures are reported minimally.</summary>
    public static IEnumerable<RespValue> Shrink(RespValue value) => value switch
    {
        RespArray { Items: { Count: > 0 } items } => items.Concat(
            Enumerable.Range(0, items.Count).Select(skip => (RespValue)new RespArray([.. items.Where((_, i) => i != skip)]))),
        RespBulkString { Value: { Length: > 0 } bytes } => [new RespBulkString([]), new RespBulkString(bytes[..(bytes.Length / 2)])],
        RespSimpleString { Value.Length: > 0 } s => [new RespSimpleString(""), new RespSimpleString(s.Value[..(s.Value.Length / 2)])],
        RespInteger { Value: not 0 } i => [new RespInteger(0), new RespInteger(i.Value / 2)],
        _ => [],
    };

    public static Arbitrary<RespValue> RespValue() => Arb.From(Value, Shrink);

    private static Gen<string> HostLabel { get; } =
        Gen.Choose(1, 12).SelectMany(n => Gen.ArrayOf(Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789-".ToCharArray()), n)).Select(c => new string(c));

    private static Gen<string> Host { get; } = Gen.OneOf(
        Gen.Choose(1, 3).SelectMany(n => Gen.ListOf(HostLabel, n)).Select(labels => string.Join('.', labels)),
        Gen.Choose(0, 255).SelectMany(a => Gen.Choose(0, 255).Select(b => $"10.{a}.{b}.1")),
        Gen.Elements("::1", "fe80::1", "2001:db8::42", "localhost"));

    private static Gen<string?> Optional(Gen<string> gen) => Gen.OneOf(Gen.Constant<string?>(null), gen.Select(s => (string?)s));

    public static Arbitrary<RedisOptions> RedisOptions()
    {
        var tcp =
            from host in Host
            from port in Gen.Choose(1, 65535)
            from db in Gen.Choose(0, 15)
            from tls in ArbMap.Default.GeneratorFor<bool>()
            from user in Optional(NonEmptyText)
            from password in Optional(Text)
            from name in Optional(Text)
            select new RedisLite.RedisOptions { Host = host, Port = port, Database = db, UseTls = tls, User = user, Password = password, ClientName = name };

        var unix =
            from path in NonEmptyText
            from db in Gen.Choose(0, 15)
            from user in Optional(NonEmptyText)
            from password in Optional(Text)
            from name in Optional(Text)
            select new RedisLite.RedisOptions { UnixSocketPath = path, Database = db, User = user, Password = password, ClientName = name };

        return Arb.From(Gen.OneOf(tcp, unix));
    }

    /// <summary>Lay <paramref name="data"/> out across several buffer segments, split at the given points.</summary>
    public static ReadOnlySequence<byte> Chunked(byte[] data, IEnumerable<int> cuts)
    {
        var points = cuts.Select(c => Math.Abs(c % (data.Length + 1))).Append(0).Append(data.Length).Distinct().Order().ToArray();
        Segment? first = null, last = null;
        for (var i = 0; i + 1 < points.Length; i++)
        {
            var chunk = data.AsMemory(points[i], points[i + 1] - points[i]);
            last = last is null ? first = new Segment(chunk) : last.Append(chunk);
        }
        return first is null ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
