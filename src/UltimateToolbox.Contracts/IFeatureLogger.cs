namespace UltimateToolbox.Contracts;

public interface IFeatureLogger
{
    void Info(string message);

    void Warning(string message);

    void Error(string message, Exception? exception = null);
}
