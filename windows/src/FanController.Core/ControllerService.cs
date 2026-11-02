namespace FanController.Core;

/// <summary>Sérialise aussi les transactions multi-commandes et le polling.</summary>
public sealed class ControllerService(ProtocolSession session)
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    public bool IsConnected => session.IsValid;

    private async Task<T> Run<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct);
        try { return await action(); }
        catch (Exception ex) { session.Invalidate(ex); throw; }
        finally { _operationGate.Release(); }
    }

    private async Task<RemoteConfiguration> ReadConfig(CancellationToken ct)
    {
        var reply = await session.RequestAsync(new { cmd = "get_config" }, ct);
        var config = WireJson.Read<Configuration>(reply.GetProperty("data"));
        Validation.Config(config);
        return new(config, reply.GetProperty("dirty").GetBoolean(), reply.GetProperty("storage").GetString() ?? "?");
    }
    private async Task<Telemetry> ReadStatus(CancellationToken ct)
    {
        var reply = await session.RequestAsync(new { cmd = "get_status" }, ct);
        var status = WireJson.Read<Telemetry>(reply.GetProperty("data"));
        Validation.Status(status);
        return status;
    }
    public Task<(RemoteConfiguration Config, Telemetry Status)> SynchronizeAsync(CancellationToken ct = default) =>
        Run(async () => (await ReadConfig(ct), await ReadStatus(ct)), ct);
    public Task<Telemetry> StatusAsync(CancellationToken ct = default) => Run(() => ReadStatus(ct), ct);

    public Task<(RemoteConfiguration Config, Telemetry Status)> ApplyAsync(Configuration draft, CancellationToken ct = default)
    {
        Validation.Config(draft);
        return Run(async () =>
        {
            for (var i = 0; i < 2; i++)
                await session.RequestAsync(new { cmd = "set_profile", sensor = i + 1, points = draft.Profiles[i] }, ct);
            for (var i = 0; i < 4; i++)
            {
                var fan = draft.Fans[i];
                if (fan.Mode == "fixed")
                    await session.RequestAsync(new { cmd = "set_fan", fan = i + 1, mode = fan.Mode, duty = fan.Duty }, ct);
                else
                    await session.RequestAsync(new { cmd = "set_fan", fan = i + 1, mode = "auto" }, ct);
            }
            // Le cycle firmware peut prendre 250 ms ; la télémétrie suit ensuite.
            await Task.Delay(300, ct);
            return (await ReadConfig(ct), await ReadStatus(ct));
        }, ct);
    }
    public Task<(RemoteConfiguration Config, Telemetry Status)> SaveAsync(CancellationToken ct = default) =>
        Run(async () =>
        {
            await session.RequestAsync(new { cmd = "save" }, ct);
            return (await ReadConfig(ct), await ReadStatus(ct));
        }, ct);
    public Task<(RemoteConfiguration Config, Telemetry Status)> DefaultsAsync(CancellationToken ct = default) =>
        Run(async () =>
        {
            await session.RequestAsync(new { cmd = "defaults" }, ct);
            await Task.Delay(300, ct);
            return (await ReadConfig(ct), await ReadStatus(ct));
        }, ct);
}
