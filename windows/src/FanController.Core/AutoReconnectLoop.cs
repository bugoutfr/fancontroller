namespace FanController.Core;

public sealed class AutoReconnectLoop(
    ReconnectPolicy policy,
    Func<ulong?> eligibleDevice,
    Func<ulong, CancellationToken, Task<bool>> connect,
    Action<Exception> onError,
    Func<TimeSpan, CancellationToken, Task>? wait = null)
{
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
                        if (await connect(address, ct)) policy.Succeeded();
                        else policy.Failed();
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
