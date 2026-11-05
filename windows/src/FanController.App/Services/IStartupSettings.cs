namespace FanController.App.Services;

/// <summary>Reads and changes per-user launch-at-sign-in integration.</summary>
public interface IStartupSettings
{
    /// <summary>Checks whether the current executable is configured for startup.</summary>
    bool Enabled();

    /// <summary>Changes startup integration and verifies the saved value.</summary>
    void SetEnabled(bool enabled);
}
