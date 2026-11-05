using System.Text;
using System.Text.Json;

namespace FanController.Core;

/// <summary>Une seule requête en vol ; aucune réutilisation après ambiguïté ou rupture.</summary>
public sealed class ProtocolSession : IDisposable
{
    private readonly IBleTransport _transport;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _sync = new();
    private readonly LineFramer _framer = new();
    private readonly TimeSpan _timeout;
    private TaskCompletionSource<JsonElement>? _pending;
    private bool _valid = true;

    /// <summary>Signale la première invalidation de la session et sa cause.</summary>
    public event Action<Exception>? Invalidated;

    /// <summary>Indique si une nouvelle requête peut être envoyée.</summary>
    public bool IsValid
    {
        get
        {
            lock (_sync)
                return _valid;
        }
    }

    /// <summary>Crée une session et s'abonne aux notifications du transport.</summary>
    /// <param name="transport">Transport connecté, libéré avec la session.</param>
    /// <param name="timeout">Délai d'une requête ; vingt secondes par défaut.</param>
    public ProtocolSession(IBleTransport transport, TimeSpan? timeout = null)
    {
        _transport = transport;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        _transport.BytesReceived += OnBytes;
        _transport.Disconnected += Invalidate;
    }

    /// <summary>Envoie une commande JSON et attend son unique réponse correspondante.</summary>
    /// <param name="command">Objet sérialisable contenant notamment le nom de commande dans cmd.</param>
    /// <param name="cancellationToken">Jeton d'annulation de l'attente ou de la requête.</param>
    /// <returns>La réponse JSON validée, indépendante du document temporaire.</returns>
    /// <exception cref="ArgumentException">La commande sérialisée dépasse 1 024 octets.</exception>
    /// <exception cref="IOException">La session est fermée, le transport échoue ou le contrôleur rejette la commande.</exception>
    /// <exception cref="InvalidDataException">La réponse est inattendue, invalide ou désynchronisée.</exception>
    /// <exception cref="TimeoutException">Le délai expire ; la session devient inutilisable.</exception>
    /// <exception cref="OperationCanceledException">L'attente ou la requête est annulée.</exception>
    public async Task<JsonElement> RequestAsync(object command, CancellationToken cancellationToken = default)
    {
        // Une annulation dans la file n'a encore rien écrit : elle n'invalide pas la session.
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(command, WireJson.Options);
            if (json.Length > 1024)
                throw new ArgumentException("Commande supérieure à 1 024 octets.");
            var expected = JsonDocument.Parse(json);
            using (expected)
            {
                var name = expected.RootElement.GetProperty("cmd").GetString();
                TaskCompletionSource<JsonElement> pending;
                lock (_sync)
                {
                    if (!_valid)
                        throw new IOException("Session fermée. Reconnectez-vous explicitement.");
                    pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending = pending;
                }

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(_timeout);
                // Observe aussi une erreur reçue pendant une écriture.
                _ = pending.Task.ContinueWith(t => { _ = t.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                try
                {
                    var framed = new byte[json.Length + 1];
                    json.CopyTo(framed, 0);
                    framed[^1] = 10;

                    for (var offset = 0; offset < framed.Length; offset += 20)
                    {
                        if (!IsValid)
                            throw new IOException("Session invalidée pendant l'écriture.");
                        await _transport.WriteAsync(framed.AsMemory(offset, Math.Min(20, framed.Length - offset)),
                            deadline.Token).WaitAsync(deadline.Token);
                    }

                    var response = await pending.Task.WaitAsync(deadline.Token);
                    if (!IsValid)
                        throw new IOException("Session désynchronisée pendant la réponse.");
                    if (!response.GetProperty("ok").GetBoolean())
                        throw new IOException($"ESP32 : {response.GetProperty("error").GetString()} — " +
                                              response.GetProperty("message").GetString());
                    if (response.GetProperty("cmd").GetString() != name)
                        throw new InvalidDataException("Réponse désynchronisée (commande différente).");

                    return response;
                }
                catch (Exception ex)
                {
                    var error = ex is OperationCanceledException && !cancellationToken.IsCancellationRequested
                        ? new TimeoutException("Délai BLE dépassé ; résultat incertain. Reconnectez-vous pour relire l'état.", ex)
                        : ex;
                    Invalidate(error);
                    throw error;
                }
                finally
                {
                    lock (_sync)
                        _pending = null;
                }
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private void OnBytes(byte[] bytes)
    {
        try
        {
            lock (_sync)
            {
                if (!_valid)
                    return;
                if (_pending is null || _pending.Task.IsCompleted)
                    throw new InvalidDataException("Notification hors requête.");
                var lines = _framer.Push(bytes);
                if (lines.Count > 1 || (lines.Count == 1 && _framer.HasPartial))
                    throw new InvalidDataException("Réponse dupliquée ou octets après la réponse.");
                foreach (var line in lines)
                {
                    if (_pending is null || _pending.Task.IsCompleted)
                        throw new InvalidDataException("Réponse BLE inattendue ou dupliquée.");
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Objet JSON attendu.");
                    _pending.TrySetResult(doc.RootElement.Clone());
                }
            }
        }
        catch (Exception ex)
        {
            Invalidate(ex);
        }
    }

    /// <summary>Ferme définitivement la session et fait échouer la requête en cours.</summary>
    /// <param name="error">Cause transmise à la requête et aux abonnés.</param>
    public void Invalidate(Exception error)
    {
        lock (_sync)
        {
            if (!_valid)
                return;
            _valid = false;
            _pending?.TrySetException(error);
        }

        _transport.Disconnect();
        Invalidated?.Invoke(error);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Invalidate(new IOException("Déconnexion explicite."));
        _transport.BytesReceived -= OnBytes;
        _transport.Disconnected -= Invalidate;
        _transport.Dispose();
    }
}
