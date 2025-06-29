using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace DiffusionNexus.DataAccess.Infrastructure.Serialization
{
    public class XmlSerializerAdapter : ISerializer
    {
        public string Serialize<T>(T obj)
        {
            var serializer = new XmlSerializer(typeof(T));
            using var ms = new MemoryStream();
            serializer.Serialize(ms, obj);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public T Deserialize<T>(string payload)
        {
            var serializer = new XmlSerializer(typeof(T));
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            return (T)serializer.Deserialize(ms)!;
        }
    }
}
