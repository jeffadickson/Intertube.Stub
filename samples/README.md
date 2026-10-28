# RedisLite: a complete application tested with Intertube.Stub

RedisLite is a small, resilient Redis client with a `redis-cli`-style command line. It's a real
client, not a toy, and it serves as a showcase of how Intertube.Stub, property-based testing and
mutation testing work together.

| Project | What it is |
|---|---|
| `RedisLite` | The client library: RESP2 codec, pipelined connections, retries, TLS, Unix sockets, pub/sub. Every public member has XML docs, enforced as build errors. |
| `RedisLite.Cli` | `redis-lite`: run one command, open a REPL, or `SUBSCRIBE` and stream messages. |
| `RedisLite.Tests` | xUnit, FsCheck, Hedgehog and Stryker. The tests use **FakeRedis**, an in-memory Redis built on Intertube.Stub. |

```
dotnet run --project samples/RedisLite.Cli -- --url redis://localhost SET greeting "hello world"
dotnet run --project samples/RedisLite.Cli -- --url rediss://:secret@cache:6380/2
```

## Design points worth testing

- **Pipelining.** Concurrent callers share one connection. Requests are written as soon as
  they arrive, and a background loop hands replies back in FIFO order. A test proves this at the
  wire level: a scripted server reads all three commands as one exact byte sequence before it
  replies to any of them. A non-pipelining client would deadlock there.
- **Honest retries.** Idempotent commands (`GET`, `SET`, ...) are retried after a connection
  failure. `INCRBY` and `PUBLISH` are retried only when the failure came before they could have
  been sent, for example during the `AUTH` handshake. Otherwise the caller gets
  `RedisConnectionException`, because the outcome is unknown.
- **Fail closed.** A timeout, a protocol error or a cut connection poisons the connection and
  fails every outstanding command. Once replies may be out of step, the connection is never
  reused.
- **The parser works on `PipeReader` / `SequenceReader`.** It never consumes a partial value, and
  it never trusts a length prefix enough to preallocate from it.

## How FakeRedis uses Intertube.Stub

```csharp
await using var fake = FakeRedis.Start(new ServerOptions { Faults = FaultOptions.Fragment(1) });
await using var redis = new RedisClient(fake.ClientOptions);

fake.Intercept = (connection, command) =>          // misbehave on exactly one connection
{
    var drop = connection.Id == 1 && command[0] == "INCRBY";
    if (drop) connection.Abort();                   // TCP reset after the server got the command
    return Task.FromResult(drop);
};
```

| Intertube.Stub feature | Where RedisLite's tests use it |
|---|---|
| Per-connection behaviour (`connection.Id`, `Abort`, `HangAsync`) | `ResilienceTests`: reset the first connection after its Nth `GET`, hang one `GET`, drop the connection during `AUTH` |
| Fault injection (`Fragment`, `DisconnectAfter`, `Latency`, seeded `Corrupt`) | byte-at-a-time replies, a reply cut after exactly 11 bytes, timeouts, the Hedgehog corruption property |
| `Conversation.ExpectBytes` / `SendRaw` | exact RESP wire format plus proof of pipelining; scripted pub/sub pushes |
| TLS with on-the-fly certificates plus `TrustServerCertificate` | `Tls_WithAnOnTheFlyCertificate`, `Tls_UntrustedCertificate_IsRejected` |
| `UnixServer` (also on Windows) | `UnixSocket_Works` |
| `PipeReader` input (`connection.Input`) | FakeRedis parses requests with the same `RespReader` as the client |
| Transcripts and waits (`WaitForReceivedLineAsync`, plaintext even under TLS) | waiting until a subscription is live; asserting exactly what went on the wire |

Python's `socketserver` provides an accept loop and nothing else. Every row in that table would
have to be written by hand there.

## Property-based testing

**FsCheck** (`RespCodecProperties`, `RedisOptionsProperties`, `ClientTests`), with custom
generators and shrinkers in `Generators.cs`:

- encode, then parse, gives back the same value
- parsing does not depend on how the bytes are split across buffer segments (TCP fragmentation, tested in memory)
- a strict prefix of an encoded value always needs more data, and nothing is consumed
- arbitrary garbage, or a single corrupted byte in a valid reply, can only fail with `RespProtocolException`
- `RedisOptions.Parse(options.ToUrl()) == options` for TCP, TLS, IPv6 and Unix socket options with arbitrary credentials
- any valid Unicode string survives `SET`/`GET` over a real socket

**Hedgehog** (`HedgehogProperties`), whose integrated shrinking suits dependent inputs:

- **Model-based test:** random sequences of `SET`/`GET`/`DEL`/`INCRBY` give the same results
  through the client, a socket and FakeRedis as against a `Dictionary`. Meanwhile Intertube.Stub
  splits every reply into fragments of a random size.
- **Corruption never hangs the client:** with seeded random bit flips in replies, every call
  completes in bounded time and throws only RedisLite exceptions.
- **Tokenizer:** `Split(Quote(tokens)) == tokens`, and arbitrary input fails only with `FormatException`.

The generators also turned up an invariant that's easy to miss: FsCheck's string generator
produces lone UTF-16 surrogates, which UTF-8 cannot represent. That limits what "any string
round-trips" can honestly promise, so the generators restrict themselves to valid Unicode.

## Bugs the tests found while this was written

- **`RedisConnection`:** `ex is IOException or ObjectDisposedException or OperationCanceledException && closing`
  binds the `&&` to the whole pattern. So a write that hit a reset before the read loop noticed it
  leaked a raw `IOException`, and the retry never ran. Caught by `ResetUnderLoad_AllCallersComplete`.
- **The `Multimeter` example:** `StreamReader.ReadLineAsync` returns a truncated final line as if
  it were complete. Caught by the fault matrix's `disconnect-mid-reply` case.

## Mutation testing (Stryker)

```
cd samples/RedisLite.Tests
dotnet tool restore
dotnet stryker            # report in StrykerOutput/<date>/reports/mutation-report.html
```

`stryker-config.json` sets the thresholds: below 60% fails the run.

| Run | Score | Killed | Timeout | Survived |
|---|---|---|---|---|
| First suite | 67.6% | 278 | 25 | 109 |
| After `MutationTargetedTests` and documented ignores | 86.7% | 308 | 18 | 25 |
| Plus the preallocation and CR-at-index-0 tests | **87.2%** | 316 | 12 | 23 |

The surviving mutants of the first run fell into three groups:

- **Equivalent (ignored in config).** `ConfigureAwait(false)` flipped to `true` (there's no
  SynchronizationContext in a library), `FlushAsync` on streams where flushing does nothing, and
  exception message strings (`*Exception.ctor`), which are diagnostics, not behaviour.
- **Real gaps, now killed by `MutationTargetedTests.cs`:**
  - every entry of the idempotent-command set (only `GET` retry was tested before)
  - `SET ... XX`
  - backoff doubling
  - deterministic write-failure mapping (the precedence bug's mutant had survived, because the load test only triggered it by luck)
  - server close failing pending commands without waiting for the timeout
  - RESP boundaries: max nesting depth, max line length, a CR at index 0, refusing to preallocate from `*2147483647`
  - URL canonical form (`ToUrl` always bracketing hosts or always adding `?` still round-trips, so the property alone cannot catch it)
  - equality against nil and null
- **Still surviving (25), reviewed as equivalent:** `leaveOpen` flags, disposal statements,
  `IndexOf(...) < 0` compared with `<= 0` where index 0 fails a later check anyway, and boundaries at
  `int.MaxValue` and 512 MB.

The `Math.Min(count, 1024)` to `Math.Max` mutant was a real denial-of-service hole: a hostile server could make the
client try a 16 GB allocation. Mutation testing found that gap, which code coverage, at 100% on that line, could not.
