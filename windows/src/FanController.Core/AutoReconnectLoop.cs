namespace FanController.Core;

/// <summary>Retries eligible remembered devices until paused or cancelled; never replays writes.</summary>
/// <param name="policy">The pause and backoff policy.</param>
/// <param name="eligibleDevice">Returns the remembered address when a retry is currently allowed.</param>
/// <param name="connect">Attempts connection; false means no successful synchronization.</param>
/// <param name="onError">Reports failed attempts.</param>
/// <param name="wait">An optional delay implementation for deterministic testing.</param>
public sealed class AutoReconnectLoop(
    ReconnectPolicy policy,
    Func<ulong?> eligibleDevice,
    Func<ulong, CancellationToken, Task<bool>> connect,
    Action<Exception> onError,
    Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    /// <summary>Runs until cancellation, treating shutdown cancellation as normal termination.</summary>
    /// <param name="ct">The application lifetime token.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!policy.Paused && eligibleDevice() is { } address)
                {
                    try
                    {
                        if (await connect(address, ct))
                            policy.Succeeded();
                        else
                            policy.Failed();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        policy.Failed();
                        onError(ex);
                    }
                }
                await (wait ?? Task.Delay)(policy.Delay, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
