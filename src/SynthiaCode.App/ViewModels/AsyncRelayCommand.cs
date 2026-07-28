using System.Windows.Input;

namespace SynthiaCode.App.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly Predicate<object?>? canExecute;
    private bool isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !isExecuting && (canExecute?.Invoke(parameter) ?? true);

    public Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return Task.CompletedTask;
        }

        return ExecuteCoreAsync(parameter);
    }

    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

    private async Task ExecuteCoreAsync(object? parameter)
    {
        try
        {
            isExecuting = true;
            RaiseCanExecuteChanged();
            await execute(parameter).ConfigureAwait(true);
        }
        finally
        {
            isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
