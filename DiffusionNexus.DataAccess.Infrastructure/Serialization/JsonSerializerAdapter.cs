using System.Text.Json;

namespace DiffusionNexus.DataAccess.Infrastructure.Serialization
{
    public class JsonSerializerAdapter : ISerializer
    {
        public string Serialize<T>(T obj)
        {
            return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        }

        public T Deserialize<T>(string payload)
        {
            return JsonSerializer.Deserialize<T>(payload)!;
        }
    }
}
