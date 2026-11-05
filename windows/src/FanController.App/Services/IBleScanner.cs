using FanController.Core;

namespace FanController.App.Services;

/// <summary>Discovers controller advertisements independently of the presentation layer.</summary>
public interface IBleScanner : IDisposable
{
    /// <summary>Reports a discovered or updated candidate.</summary>
    event Action<BleCandidate>? Found;

    /// <summary>Reports scanning failures suitable for presentation.</summary>
    event Action<string>? Failed;

    /// <summary>Starts active scanning until disposal.</summary>
    void Start();
}
