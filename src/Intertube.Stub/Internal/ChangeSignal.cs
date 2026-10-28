namespace Intertube.Stub.Internal;

/// <summary>Lets waiters re-check a condition whenever recorded state changes.</summary>
internal sealed class ChangeSignal
{
    private TaskCompletionSource _changed = NewSource();

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public void Pulse() => Interlocked.Exchange(ref _changed, NewSource()).TrySetResult();

    public async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout, Func<string> describe, CancellationToken cancellationToken)
    {
        var limit = timeout ?? DefaultTimeout;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(limit);
        while (true)
        {
            // Capture the signal before testing, so a change between the test and the wait is not missed.
            var changed = Volatile.Read(ref _changed).Task;
            if (condition()) return;
            try
            {
                await changed.WaitAsync(timer.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (condition()) return;
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0.###}s waiting for {describe()}");
            }
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
