namespace FanController.Core;

/// <summary>Sérialise aussi les transactions multi-commandes et le polling.</summary>
/// <param name="session">Session utilisée pour toutes les commandes du contrôleur.</param>
/// <remarks>Arrêter les opérations en cours avant de libérer le service ; la session reste à la charge de l'appelant.</remarks>
public sealed class ControllerService(ProtocolSession session) : IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    /// <summary>Indique si la session peut encore traiter des commandes.</summary>
    public bool IsConnected => session.IsValid;

    private async Task<T> Run<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct);
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            session.Invalidate(ex);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<RemoteConfiguration> ReadConfig(CancellationToken ct)
    {
        var reply = await session.RequestAsync(new
        {
            cmd = "get_config"
        }, ct);
        var config = WireJson.Read<Configuration>(reply.GetProperty("data"));
        Validation.Config(config);
        return new(config, reply.GetProperty("dirty").GetBoolean(), reply.GetProperty("storage").GetString() ?? "?");
    }

    private async Task<Telemetry> ReadStatus(CancellationToken ct)
    {
        var reply = await session.RequestAsync(new
        {
            cmd = "get_status"
        }, ct);
        var status = WireJson.Read<Telemetry>(reply.GetProperty("data"));
        Validation.Status(status);
        return status;
    }

    /// <summary>Relit la configuration puis la télémétrie sans intercaler d'autre opération.</summary>
    /// <param name="ct">Jeton d'annulation de l'opération.</param>
    /// <returns>La configuration distante et la télémétrie validées.</returns>
    /// <exception cref="OperationCanceledException">L'opération a été annulée.</exception>
    public Task<(RemoteConfiguration Config, Telemetry Status)> SynchronizeAsync(CancellationToken ct = default) =>
        Run(async () => (await ReadConfig(ct), await ReadStatus(ct)), ct);

    /// <summary>Relit et valide la télémétrie du contrôleur.</summary>
    /// <param name="ct">Jeton d'annulation de l'opération.</param>
    /// <returns>La télémétrie reçue.</returns>
    /// <exception cref="OperationCanceledException">L'opération a été annulée.</exception>
    public Task<Telemetry> StatusAsync(CancellationToken ct = default) => Run(() => ReadStatus(ct), ct);

    /// <summary>Collects consecutive firmware windows exclusively with respect to normal polling.</summary>
    /// <param name="seconds">Capture duration, either 30 or 60 seconds.</param>
    /// <param name="progress">Receives the number of captured seconds.</param>
    /// <param name="ct">Cancels acquisition and any outstanding request.</param>
    /// <returns>The complete capture, never a success-shaped partial result.</returns>
    public Task<IReadOnlyList<TachWindow>> CaptureTachAsync(int seconds, IProgress<double>? progress,
        CancellationToken ct = default)
    {
        if (seconds is not (30 or 60))
            throw new ArgumentOutOfRangeException(nameof(seconds));
        return Run<IReadOnlyList<TachWindow>>(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds + 60));
            async Task<TachReply> Read(ulong? after)
            {
                var response = after.HasValue
                    ? await session.RequestAsync(new
                    {
                        cmd = "get_tach_diagnostics",
                        after = after.Value
                    }, deadline.Token)
                    : await session.RequestAsync(new
                    {
                        cmd = "get_tach_diagnostics"
                    }, deadline.Token);
                var reply = WireJson.Read<TachReply>(response.GetProperty("data"));
                TachDiagnostics.Validate(reply, after);
                return reply;
            }

            var cursor = (await Read(null)).LatestSequence;
            var windows = new List<TachWindow>();
            ulong capturedUs = 0;
            // The deadline also bounds a controller that replies but stops advancing.
            while (capturedUs < (ulong)seconds * 1000000)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var reply = await Read(cursor);
                if (reply.Window is { } window)
                {
                    windows.Add(window);
                    cursor = window.Sequence;
                    capturedUs += window.Fans.Min(f => f.WindowUs);
                    progress?.Report(Math.Min(seconds, capturedUs / 1000000.0));
                }
                else
                    await Task.Delay(250, deadline.Token);
            }
            return windows;
        }, ct);
    }

    /// <summary>Applique les profils et ventilateurs, puis relit l'état sans sauvegarder en mémoire persistante.</summary>
    /// <param name="draft">Configuration à valider puis à envoyer.</param>
    /// <param name="ct">Jeton d'annulation de l'opération.</param>
    /// <returns>La configuration distante et la télémétrie après application.</returns>
    /// <exception cref="OperationCanceledException">L'opération a été annulée ; une application partielle est possible.</exception>
    public Task<(RemoteConfiguration Config, Telemetry Status)> ApplyAsync(Configuration draft, CancellationToken ct = default)
    {
        Validation.Config(draft);
        return Run(async () =>
        {
            for (var i = 0; i < 2; i++)
                await session.RequestAsync(new
                {
                    cmd = "set_profile",
                    sensor = i + 1,
                    points = draft.Profiles[i]
                }, ct);

            for (var i = 0; i < 4; i++)
            {
                var fan = draft.Fans[i];
                if (fan.Mode == "fixed")
                    await session.RequestAsync(new
                    {
                        cmd = "set_fan",
                        fan = i + 1,
                        mode = fan.Mode,
                        duty = fan.Duty
                    }, ct);
                else
                    await session.RequestAsync(new
                    {
                        cmd = "set_fan",
                        fan = i + 1,
                        mode = "auto"
                    }, ct);
            }

            // Le cycle firmware peut prendre 250 ms ; la télémétrie suit ensuite.
            await Task.Delay(300, ct);
            return (await ReadConfig(ct), await ReadStatus(ct));
        }, ct);
    }

    /// <summary>Sauvegarde la configuration distante en mémoire persistante puis relit l'état.</summary>
    /// <param name="ct">Jeton d'annulation de l'opération.</param>
    /// <returns>La configuration distante et la télémétrie après sauvegarde.</returns>
    /// <exception cref="OperationCanceledException">L'opération a été annulée.</exception>
    public Task<(RemoteConfiguration Config, Telemetry Status)> SaveAsync(CancellationToken ct = default) =>
        Run(async () =>
        {
            await session.RequestAsync(new
            {
                cmd = "save"
            }, ct);
            return (await ReadConfig(ct), await ReadStatus(ct));
        }, ct);

    /// <summary>Restaure les valeurs par défaut du contrôleur puis relit l'état.</summary>
    /// <param name="ct">Jeton d'annulation de l'opération.</param>
    /// <returns>La configuration distante et la télémétrie après restauration.</returns>
    /// <exception cref="OperationCanceledException">L'opération a été annulée.</exception>
    public Task<(RemoteConfiguration Config, Telemetry Status)> DefaultsAsync(CancellationToken ct = default) =>
        Run(async () =>
        {
            await session.RequestAsync(new
            {
                cmd = "defaults"
            }, ct);
            await Task.Delay(300, ct);
            return (await ReadConfig(ct), await ReadStatus(ct));
        }, ct);

    /// <summary>Transfers and validates an image exclusively with respect to polling and setting changes.</summary>
    /// <param name="transport">The connected binary OTA channel.</param>
    /// <param name="image">Locally validated image.</param>
    /// <param name="progress">Receives the percentage of bytes acknowledged in flash.</param>
    /// <param name="confirmActivation">Called after validation; false discards the image without restarting.</param>
    /// <param name="ct">Cancels the transfer or confirmation; ambiguous writes invalidate the connection.</param>
    /// <param name="transferProgress">Optional binary upload timing and acknowledged byte measurements.</param>
    /// <returns>True only when activation was acknowledged; restart verification remains the owner's responsibility.</returns>
    public Task<bool> UpdateFirmwareAsync(IFirmwareTransport transport, FirmwareImage image,
            IProgress<double>? progress, Func<CancellationToken, Task<bool>> confirmActivation, CancellationToken ct,
            IProgress<FirmwareTransferProgress>? transferProgress = null) =>
            Run(async () =>
            {
                var info = (await session.RequestAsync(new
                {
                    cmd = "ota_info"
                }, ct)).GetProperty("data");
                if (info.GetProperty("protocol").GetInt32() != 1 ||
                    image.Bytes.Length > info.GetProperty("max_size").GetInt32())
                    throw new InvalidDataException("The firmware is incompatible with the controller's OTA partition or protocol.");
                var begin = await session.RequestAsync(new
                {
                    cmd = "ota_begin",
                    size = image.Bytes.Length,
                    sha256 = image.FileSha256
                }, ct);
                if (begin.GetProperty("data").GetProperty("offset").GetUInt32() != 0)
                    throw new InvalidDataException("OTA transfer did not start at offset zero.");
                uint offset = 0;
                progress?.Report(0);
                var uploadClock = System.Diagnostics.Stopwatch.StartNew();
                transferProgress?.Report(new(0, image.Bytes.Length, TimeSpan.Zero));
                while (offset < image.Bytes.Length)
                {
                    var count = Math.Min(transport.FirmwareBlockSize, image.Bytes.Length - (int)offset);
                    var accepted = await transport.WriteFirmwareBlockAsync(offset,
                        image.Bytes.Slice((int)offset, count), ct);
                    if (accepted != offset + count)
                        throw new InvalidDataException("OTA acknowledgment offset is invalid.");
                    offset = accepted;
                    transferProgress?.Report(new(offset, image.Bytes.Length, uploadClock.Elapsed));
                    progress?.Report(100.0 * offset / image.Bytes.Length);
                }
                var finish = (await session.RequestAsync(new
                {
                    cmd = "ota_finish"
                }, ct)).GetProperty("data");
                if (finish.GetProperty("size").GetInt32() != image.Bytes.Length ||
                    !string.Equals(finish.GetProperty("sha256").GetString(), image.FileSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The staged firmware digest or size differs from the selected file.");
                var activate = await confirmActivation(ct);
                ct.ThrowIfCancellationRequested();
                await session.RequestAsync(new
                {
                    cmd = activate ? "ota_activate" : "ota_abort"
                }, ct);
                return activate;
            }, ct);

    /// <summary>Verifies the running partition's embedded image digest, not merely a reported version string.</summary>
    /// <param name="expectedImageSha256">Expected ESP image digest.</param>
    /// <param name="ct">Cancels the verification.</param>
    /// <returns>A task completed after a matching OTA info response.</returns>
    public Task VerifyFirmwareAsync(string expectedImageSha256, CancellationToken ct) =>
            Run(async () =>
            {
                var info = (await session.RequestAsync(new
                {
                    cmd = "ota_info"
                }, ct)).GetProperty("data");
                if (!string.Equals(info.GetProperty("running_image_sha256").GetString(),
                    expectedImageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The running firmware does not match the transferred image.");
                return true;
            }, ct);

    /// <inheritdoc />
    public void Dispose() => _operationGate.Dispose();
}
