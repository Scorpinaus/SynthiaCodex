namespace NativeCodexAssistant.Core.Logging;

public interface IAppLogger
{
    void Log(
        AppLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? properties = null,
        Exception? exception = null);
}
