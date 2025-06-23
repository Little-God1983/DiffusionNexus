using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.Classes
{
    public class PromptProfileService
    {
        private readonly string _filePath;

        public PromptProfileService()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiffusionNexus");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "prompt_profiles.json");
        }

        private async Task<List<PromptProfileModel>> LoadProfilesAsync()
        {
            if (!File.Exists(_filePath))
                return new List<PromptProfileModel>();

            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<List<PromptProfileModel>>(json) ?? new();
        }

        private async Task SaveProfilesToDiskAsync(List<PromptProfileModel> dict)
        {
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json);
        }

        public async Task<List<PromptProfileModel>> LoadAllAsync()
        {
            return await LoadProfilesAsync();
        }

        public async Task<PromptProfileModel?> GetAsync(string name)
        {
            IList<PromptProfileModel> dict = await LoadProfilesAsync();
            return dict.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<bool> ExistsByNameAsync(string name)
        {
            IList<PromptProfileModel> dict = await LoadProfilesAsync();
            return dict.Select(x => x.Name == name) == null ? false : true;
        }

        public async Task SaveAsync(PromptProfileModel profile)
        {
            List<PromptProfileModel> dict = await LoadProfilesAsync();
            dict.Add(profile);
            await SaveProfilesToDiskAsync(dict);
        }

        public async Task DeleteAsync(PromptProfileModel profile)
        {
            List<PromptProfileModel> list = await LoadProfilesAsync();
            if (list.Remove(profile))
                await SaveProfilesToDiskAsync(list);
        }
    }
}
