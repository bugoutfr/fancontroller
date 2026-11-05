using Microsoft.UI.Dispatching;

namespace FanController.App.Services;

internal sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
        }
        else if (!queue.TryEnqueue(() => action()))
        {
            Diagnostics.Record(new InvalidOperationException("The UI dispatcher is unavailable."));
        }
    }
}
