namespace DiffusionNexus.DataAccess.Infrastructure.Serialization
{
    public interface ISerializer
    {
        string Serialize<T>(T obj);
        T Deserialize<T>(string payload);
    }
}
