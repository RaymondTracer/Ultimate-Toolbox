namespace UltimateToolbox.Contracts;

public interface IFeatureSettingsStore
{
    T? Load<T>() where T : class;

    void Save<T>(T settings) where T : class;
}
