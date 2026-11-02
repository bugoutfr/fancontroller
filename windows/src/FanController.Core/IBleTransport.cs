namespace FanController.Core;

public interface IBleTransport : IDisposable
{
    event Action<byte[]>? BytesReceived;
    event Action<Exception>? Disconnected;
    Task ConnectAsync(ulong address, CancellationToken cancellationToken);
    Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    void Disconnect();
}
