namespace DiffusionNexus.UI.Models
{
    public class PromptProfileModel
    {
        public string Name { get; set; } = string.Empty;
        public string Blacklist { get; set; } = string.Empty;
        public string Whitelist { get; set; } = string.Empty;
        public override string ToString() => Name;
    }
}
