namespace FanController.Core;

/// <summary>Transport BLE des octets du protocole, avec notification des ruptures.</summary>
public interface IBleTransport : IDisposable
{
    /// <summary>Signale les octets reçus, éventuellement fragmentés entre plusieurs notifications.</summary>
    event Action<byte[]>? BytesReceived;

    /// <summary>Signale une rupture inattendue et son erreur.</summary>
    event Action<Exception>? Disconnected;

    /// <summary>Ouvre une connexion et active les notifications avant toute écriture.</summary>
    /// <param name="address">Adresse Bluetooth du contrôleur.</param>
    /// <param name="cancellationToken">Jeton d'annulation de la connexion.</param>
    /// <returns>Une tâche terminée lorsque le transport est prêt.</returns>
    /// <exception cref="IOException">La connexion ou la configuration GATT échoue.</exception>
    /// <exception cref="OperationCanceledException">La connexion a été annulée.</exception>
    Task ConnectAsync(ulong address, CancellationToken cancellationToken);

    /// <summary>Écrit un fragment de commande avec confirmation du périphérique.</summary>
    /// <param name="bytes">Fragment à envoyer.</param>
    /// <param name="cancellationToken">Jeton d'annulation de l'écriture.</param>
    /// <returns>Une tâche terminée après confirmation de l'écriture.</returns>
    /// <exception cref="IOException">Le transport n'est pas prêt ou l'écriture échoue.</exception>
    /// <exception cref="OperationCanceledException">L'écriture a été annulée.</exception>
    Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Ferme la connexion et libère les ressources Bluetooth ; les appels répétés sont sans effet.</summary>
    void Disconnect();
}
