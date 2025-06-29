namespace DiffusionNexus.DataAccess.Entities
{
    public class UserPreference
    {
        public string UserId { get; set; } = string.Empty;
        public string PreferenceKey { get; set; } = string.Empty;
        public string PreferenceValue { get; set; } = string.Empty;
    }
}
