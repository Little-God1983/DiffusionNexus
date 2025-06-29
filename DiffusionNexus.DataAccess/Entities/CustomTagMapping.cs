namespace DiffusionNexus.DataAccess.Entities
{
    public class CustomTagMapping
    {
        public List<string> Tags { get; set; } = new();
        public string Folder { get; set; } = string.Empty;
        public int Priority { get; set; }
    }
}
