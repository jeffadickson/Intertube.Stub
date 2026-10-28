using RedisLite.Cli;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

return await CliApp.RunAsync(args, Console.In, Console.Out, cancel.Token);
