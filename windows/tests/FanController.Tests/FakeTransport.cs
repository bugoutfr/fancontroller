using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FanController.Core;

namespace FanController.Tests;

/// <summary>Exclusivement lié au projet de tests ; aucune référence depuis l'application.</summary>
internal sealed class FakeTransport : IControllerTransport
{
    private readonly List<byte> _input = [];
    public event Action<byte[]>? BytesReceived;
    public event Action<Exception>? Disconnected;
    public Channel<JsonElement> Commands { get; } = Channel.CreateUnbounded<JsonElement>();
    public List<byte[]> Writes { get; } = [];
    public List<string> Names { get; } = [];
    public int DisconnectCount
    {
        get; private set;
    }
    public bool ThrowOnWrite
    {
        get; set;
    }
    public bool BlockWrite
    {
        get; set;
    }
    public bool Connected { get; private set; } = true;
    public string? DeviceName => "Test controller";
    public bool? PairingAllowed
    {
        get; private set;
    }
    public Func<CancellationToken, Task>? OnConnect
    {
        get; set;
    }
    public bool Disposed
    {
        get; private set;
    }
    public Task ConnectAsync(ulong address, CancellationToken cancellationToken) =>
        ConnectAsync(address, cancellationToken, allowPairing: true);
    public async Task ConnectAsync(ulong address, CancellationToken cancellationToken, bool allowPairing)
    {
        PairingAllowed = allowPairing;
        if (OnConnect is not null)
            await OnConnect(cancellationToken);
        Connected = true;
    }
    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (!Connected || ThrowOnWrite)
            throw new IOException("Erreur GATT simulée.");
        if (BlockWrite)
            await Task.Delay(Timeout.Infinite, cancellationToken);
        Writes.Add(bytes.ToArray());
        foreach (var b in bytes.Span)
        {
            if (b == 10)
            {
                using var doc = JsonDocument.Parse(_input.ToArray());
                var command = doc.RootElement.Clone();
                Names.Add(command.GetProperty("cmd").GetString()!);
                Commands.Writer.TryWrite(command);
                _input.Clear();
            }
            else
                _input.Add(b);
        }
    }
    public Task<JsonElement> NextAsync() => Commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    public void Reply(string json, int chunk = 20)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        for (var i = 0; i < bytes.Length; i += chunk)
            BytesReceived?.Invoke(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)).ToArray());
    }
    public void Emit(byte[] bytes) => BytesReceived?.Invoke(bytes);
    public void LoseConnection()
    {
        Disconnect();
        Disconnected?.Invoke(new IOException("Rupture radio simulée."));
    }
    public void Disconnect()
    {
        if (Connected)
        {
            Connected = false;
            DisconnectCount++;
        }
    }
    public void Dispose()
    {
        Disposed = true;
        Disconnect();
    }
}
