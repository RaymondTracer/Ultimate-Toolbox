using System.Diagnostics;
using UltimateToolbox.Contracts;

namespace UltimateToolbox;

internal sealed class TraceFeatureLogger : IFeatureLogger
{
    private readonly string _featureId;

    public TraceFeatureLogger(string featureId)
    {
        _featureId = featureId;
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warning(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var suffix = exception is null ? string.Empty : $" {exception}";
        Trace.WriteLine($"{DateTimeOffset.Now:O} [{level}] [{_featureId}] {message}{suffix}");
    }
}
