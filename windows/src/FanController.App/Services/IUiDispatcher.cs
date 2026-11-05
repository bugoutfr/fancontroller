namespace FanController.App.Services;

/// <summary>Marshals presentation changes to the UI without exposing WinUI to view models.</summary>
public interface IUiDispatcher
{
    /// <summary>Runs on the UI thread, or queues the action and reports an unavailable dispatcher.</summary>
    void Post(Action action);
}
