/// The system under test: a client for a line-based configuration service. It knows nothing about
/// Intertube.Stub; it is ordinary F# over TcpClient.
///
///   GET key        -> VALUE text | MISSING
///   SET key text   -> OK
module Intertube.Stub.FSharp.ConfigClient

open System
open System.IO
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks

type Settings =
    { Host: string
      Port: int
      Timeout: TimeSpan
      Attempts: int }

exception ConfigProtocolException of reply: string

let private isTransient (ex: exn) =
    match ex with
    | :? IOException | :? SocketException | :? OperationCanceledException -> true
    | _ -> false

/// Read one CRLF- or LF-terminated line. Unlike StreamReader.ReadLineAsync, a line cut off by the
/// end of the stream is an error, not a shorter value.
let private readLine (stream: Stream) (cancellationToken: CancellationToken) : Task<string> =
    task {
        let line = System.Collections.Generic.List<byte>()
        let buffer = Array.zeroCreate<byte> 1
        let mutable finished = false
        while not finished do
            let! read = stream.ReadAsync(buffer.AsMemory(), cancellationToken)
            if read = 0 then
                raise (IOException $"The connection closed mid-reply after {line.Count} bytes.")
            elif buffer[0] = byte '\n' then
                finished <- true
            else
                line.Add buffer[0]
        return Text.Encoding.UTF8.GetString(line.ToArray()).TrimEnd '\r'
    }

/// Open a connection, send one command, read one reply line. Every call uses a fresh connection.
let private exchange settings (command: string) : Task<string> =
    task {
        use tcp = new TcpClient()
        use timeout = new CancellationTokenSource(settings.Timeout)
        do! tcp.ConnectAsync(settings.Host, settings.Port, timeout.Token)
        use stream = tcp.GetStream()
        use writer = new StreamWriter(stream, NewLine = "\r\n", AutoFlush = true)
        do! writer.WriteLineAsync(command.AsMemory(), timeout.Token)
        return! readLine stream timeout.Token
    }

/// Retry transient failures (resets, timeouts) up to settings.Attempts times; protocol errors are final.
let private withRetries settings (command: string) : Task<string> =
    let rec attempt n =
        task {
            try
                return! exchange settings command
            with ex when n < settings.Attempts && isTransient ex ->
                do! Task.Delay(TimeSpan.FromMilliseconds(20.0 * float n))
                return! attempt (n + 1)
        }
    attempt 1

let get settings (key: string) : Task<string option> =
    task {
        match! withRetries settings $"GET {key}" with
        | "MISSING" -> return None
        | reply when reply.StartsWith "VALUE " -> return Some(reply.Substring 6)
        | reply -> return raise (ConfigProtocolException reply)
    }

let set settings (key: string) (value: string) : Task =
    task {
        if key.Contains ' ' || value.Contains '\n' || value.Contains '\r' then
            invalidArg (nameof key) "Keys cannot contain spaces, and values cannot contain line breaks."
        match! withRetries settings $"SET {key} {value}" with
        | "OK" -> ()
        | reply -> raise (ConfigProtocolException reply)
    }
