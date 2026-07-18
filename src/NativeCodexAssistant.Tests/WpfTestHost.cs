using System.Windows;
using System.Windows.Threading;

internal static class WpfTestHost
{
    private static readonly Task<Dispatcher> DispatcherTask = StartDispatcherAsync();

    public static async Task RunAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = await DispatcherTask.ConfigureAwait(false);
        await dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task.ConfigureAwait(false);
    }

    private static Task<Dispatcher> StartDispatcherAsync()
    {
        var completion = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            _ = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            completion.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "NativeCodexAssistant WPF test host"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
