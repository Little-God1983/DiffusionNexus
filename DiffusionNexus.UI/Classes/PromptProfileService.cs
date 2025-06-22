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

        private async Task<Dictionary<string, PromptProfileModel>> LoadDictionaryAsync()
        {
            if (!File.Exists(_filePath))
                return new();

            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, PromptProfileModel>>(json) ?? new();
        }

        private async Task SaveDictionaryAsync(Dictionary<string, PromptProfileModel> dict)
        {
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json);
        }

        public async Task<List<PromptProfileModel>> LoadAllAsync()
        {
            var dict = await LoadDictionaryAsync();
            return dict.Values.ToList();
        }

        public async Task<PromptProfileModel?> GetAsync(string name)
        {
            var dict = await LoadDictionaryAsync();
            return dict.TryGetValue(name, out var profile) ? profile : null;
        }

        public async Task<bool> ExistsAsync(string name)
        {
            var dict = await LoadDictionaryAsync();
            return dict.ContainsKey(name);
        }

        public async Task SaveAsync(PromptProfileModel profile)
        {
            var dict = await LoadDictionaryAsync();
            dict[profile.Name] = profile;
            await SaveDictionaryAsync(dict);
        }

        public async Task DeleteAsync(string name)
        {
            var dict = await LoadDictionaryAsync();
            if (dict.Remove(name))
                await SaveDictionaryAsync(dict);
        }
    }
}
