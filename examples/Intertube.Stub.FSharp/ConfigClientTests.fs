module Intertube.Stub.FSharp.ConfigClientTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Xunit // before FsCheck, which would otherwise make "Xunit" mean FsCheck.Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Intertube.Stub

// ---- Small F# helpers over the C# API ---------------------------------------------------------

/// A glob for the line-matching APIs. (F# would also apply LineMatcher's implicit conversion from
/// string, but naming it reads better and avoids the FS3391 warning.)
let glob = LineMatcher.Glob

let settingsFor (server: TcpServer) =
    { ConfigClient.Host = server.Host
      ConfigClient.Port = server.Port
      ConfigClient.Timeout = TimeSpan.FromSeconds 2.0
      ConfigClient.Attempts = 3 }

/// A working fake of the whole service: a dictionary behind a line handler.
let fakeService (store: ConcurrentDictionary<string, string>) =
    Handler.Lines(fun line ->
        match line.Split(' ', 3) with
        | [| "GET"; key |] ->
            match store.TryGetValue key with
            | true, value -> $"VALUE {value}"
            | _ -> "MISSING"
        | [| "SET"; key; value |] ->
            store[key] <- value
            "OK"
        | [| "SET"; key |] ->
            store[key] <- ""
            "OK"
        | _ -> "ERR unknown command")

// ---- Tests -------------------------------------------------------------------------------------

[<Fact>]
let ``rule table: GET of a known and an unknown key`` () =
    task {
        // LineResponder: an ordered table of glob -> reply, like WireMock stubs for a line protocol.
        use server =
            TcpServer.Listen(
                0,
                LineResponder()
                    .On(glob "GET timeout", "VALUE 30")
                    .On(glob "GET *", "MISSING")
                    .Otherwise("ERR unknown command"))

        let settings = settingsFor server
        let! timeout = ConfigClient.get settings "timeout"
        let! colour = ConfigClient.get settings "colour"

        Assert.Equal(Some "30", timeout)
        Assert.Equal(None, colour)
        server.Transcript.AssertReceivedLinesInOrder(glob "GET timeout", glob "GET colour")
        server.Transcript.AssertConnectionCount 2
    }

[<Fact>]
let ``scripted conversation: SET then GET, exactly in that order`` () =
    task {
        // A Conversation is an executable spec of one connection's dialogue; anything else fails it.
        let mutable connections = 0
        use server =
            TcpServer.Listen(
                0,
                fun connection ->
                    let script =
                        if Threading.Interlocked.Increment(&connections) = 1 then
                            Conversation.Create().Expect(glob "SET retries 5", "OK")
                        else
                            Conversation.Create().Expect(glob "GET retries", "VALUE 5")
                    script.HandleAsync connection)

        let settings = settingsFor server
        do! ConfigClient.set settings "retries" "5"
        let! value = ConfigClient.get settings "retries"

        Assert.Equal(Some "5", value)
        server.ThrowIfFaulted()
    }

[<Fact>]
let ``a reset on the first connection is retried`` () =
    task {
        let store = ConcurrentDictionary(dict [ "region", "eu-west" ])
        let service = fakeService store
        use server =
            TcpServer.Listen(
                0,
                fun connection ->
                    if connection.Id = 1 then
                        connection.Abort()
                        Task.CompletedTask
                    else
                        service.Invoke connection)

        let! region = ConfigClient.get (settingsFor server) "region"

        Assert.Equal(Some "eu-west", region)
        server.Transcript.AssertConnectionCount 2
        Assert.Contains("reset", server.Transcript[0].Events)
    }

[<Fact>]
let ``a hung service times out on every attempt, then fails`` () =
    task {
        use server = TcpServer.Listen(0, Handler.Hang)
        let settings = { settingsFor server with Timeout = TimeSpan.FromMilliseconds 200.0 }

        let! ex = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> ConfigClient.get settings "anything" :> Task)

        Assert.NotNull ex
        server.Transcript.AssertConnectionCount settings.Attempts
    }

[<Fact>]
let ``a reply cut mid-line is an error, not a truncated value`` () =
    task {
        // Fault injection: close the connection 8 bytes into every reply ("VALUE 30" never finishes).
        use server =
            TcpServer.Listen(
                0,
                Handler.Lines(fun _ -> "VALUE 3000000"),
                ServerOptions(Faults = FaultOptions.DisconnectAfter(8L, DisconnectMode.Close)))

        // StreamReader.ReadLineAsync would hand back "VALUE 30" here, a silently wrong setting. This
        // test is why ConfigClient reads its own lines and treats end of stream mid-line as an error.
        let! ex = Assert.ThrowsAsync<IOException>(fun () -> ConfigClient.get (settingsFor server) "timeout" :> Task)

        Assert.Contains("mid-reply", ex.Message)
        server.Transcript.AssertConnectionCount 3 // a transient failure, so it was retried
    }

[<Fact>]
let ``garbage from the service is a protocol error and is not retried`` () =
    task {
        use server = TcpServer.Listen(0, Handler.Lines(fun _ -> "HTTP/1.1 400 Bad Request"))

        let! ex = Record.ExceptionAsync(fun () -> ConfigClient.get (settingsFor server) "timeout" :> Task)

        match ex with
        | ConfigClient.ConfigProtocolException reply -> Assert.StartsWith("HTTP/1.1", reply)
        | other -> failwithf "expected a protocol error, got %A" other
        server.Transcript.AssertConnectionCount 1
    }

// ---- A property: whatever is stored can be read back unchanged ---------------------------------

/// Keys without spaces or line breaks, values without line breaks: what the protocol can carry.
type ProtocolStrings =
    static member Pairs() =
        let noBreaks (s: string) = not (isNull s) && s |> Seq.forall (fun c -> c <> '\r' && c <> '\n')
        let key = ArbMap.defaults |> ArbMap.generate<NonEmptyString> |> Gen.map _.Get |> Gen.filter (fun k -> noBreaks k && not (k.Contains ' '))
        let value = ArbMap.defaults |> ArbMap.generate<string> |> Gen.filter noBreaks
        Gen.zip key value |> Arb.fromGen

[<Property(Arbitrary = [| typeof<ProtocolStrings> |], MaxTest = 50)>]
let ``SET then GET round-trips any value the protocol can carry`` (key: string, value: string) =
    task {
        use server = TcpServer.Listen(0, fakeService (ConcurrentDictionary()))
        let settings = settingsFor server

        do! ConfigClient.set settings key value
        let! readBack = ConfigClient.get settings key

        return readBack = Some value
    }
    |> fun t -> t.GetAwaiter().GetResult()
