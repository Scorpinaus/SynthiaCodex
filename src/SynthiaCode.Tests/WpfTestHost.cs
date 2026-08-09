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

    public static T? FindNamedDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        if (root is FrameworkElement scope && scope.FindName(name) is T named)
        {
            return named;
        }

        if (root is T { Name: var candidateName } match &&
            string.Equals(candidateName, name, StringComparison.Ordinal))
        {
            return match;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (FindNamedDescendant<T>(child, name) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static Task<Dispatcher> StartDispatcherAsync()
    {
        var completion = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            foreach (var source in new[]
                     {
                         "Themes/LightTheme.xaml",
                         "Themes/Foundations.xaml",
                         "Themes/Typography.xaml",
                         "Themes/Icons.xaml",
                         "Themes/Controls.Buttons.xaml",
                         "Themes/Controls.Inputs.xaml",
                         "Themes/Controls.Navigation.xaml",
                         "Themes/Controls.Transient.xaml"
                     })
            {
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/SynthiaCode.App;component/{source}", UriKind.Relative)
                });
            }

            completion.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "SynthiaCode WPF test host"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
