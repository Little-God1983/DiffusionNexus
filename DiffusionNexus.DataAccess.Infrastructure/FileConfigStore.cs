using DiffusionNexus.DataAccess.Interfaces;
using DiffusionNexus.DataAccess.Infrastructure.Serialization;
using System.IO;

namespace DiffusionNexus.DataAccess.Infrastructure
{
    public class FileConfigStore : IConfigStore
    {
        private readonly string _basePath;
        private readonly ISerializer _serializer;

        public FileConfigStore(string basePath, ISerializer serializer)
        {
            _basePath = basePath;
            _serializer = serializer;
            Directory.CreateDirectory(_basePath);
        }

        private string GetFilePath(string key)
        {
            var ext = _serializer switch
            {
                JsonSerializerAdapter => "json",
                XmlSerializerAdapter => "xml",
                _ => "dat"
            };
            return Path.Combine(_basePath, $"{key}.{ext}");
        }

        public T Load<T>(string key) where T : class, new()
        {
            var path = GetFilePath(key);
            if (!File.Exists(path))
                return new T();
            var payload = File.ReadAllText(path);
            return _serializer.Deserialize<T>(payload) ?? new T();
        }

        public void Save<T>(string key, T config) where T : class, new()
        {
            var path = GetFilePath(key);
            var payload = _serializer.Serialize(config);
            File.WriteAllText(path, payload);
        }
    }
}
