namespace DiffusionNexus.Service.Classes
{
    public class CustomTagMap
    {
        //this is a list because in theory it should be possible to map multiple tags to a single folder
        public List<string> LookForTag { get; set; } = new List<string>();
        public string MapToFolder { get; set; } = string.Empty;
        public int Priority { get; set; }
    }

}
