namespace DiffusionNexus.DataAccess.Entities
{
    public class UserPreference
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string PreferenceKey { get; set; } = string.Empty;
        public string PreferenceValue { get; set; } = string.Empty;
    }
}
