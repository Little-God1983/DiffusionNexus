namespace DiffusionNexus.DataAccess.Interfaces;

public interface IConfigStore
{
    T Load<T>(string key) where T : class, new();
    void Save<T>(string key, T config) where T : class, new();
}
