using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace FanController.App.ViewModels;

/// <summary>Provides equality-aware property changes for presentation models.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Notifies bindings that a property has changed.</summary>
    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));

    /// <summary>Updates and notifies a property only when its value differs.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Changed(name);
        return true;
    }
}

/// <summary>An awaitable, non-reentrant command with explicit error reporting.</summary>
/// <param name="execute">The operation to run.</param>
/// <param name="canExecute">The current eligibility predicate.</param>
/// <param name="onError">Reports failures to the application's error surface.</param>
public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onError) : ICommand
{
    private bool _running;

    /// <summary>The most recent execution, retained so shutdown and tests can await completion.</summary>
    public Task ExecutionTask { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && canExecute();

    /// <inheritdoc />
    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    /// <summary>Runs the command when eligible and returns a task covering its error handling and cleanup.</summary>
    public Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
            return Task.CompletedTask;
        return ExecutionTask = RunAsync();
    }

    private async Task RunAsync()
    {
        _running = true;
        Refresh();

        try
        {
            await execute();
        }
        catch (Exception ex) { onError(ex); }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    /// <summary>Notifies controls to reevaluate command eligibility.</summary>
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
