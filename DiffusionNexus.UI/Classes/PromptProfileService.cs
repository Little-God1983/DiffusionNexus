using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models;
using DiffusionNexus.DataAccess.Interfaces;
using DiffusionNexus.DataAccess.Infrastructure;
using DiffusionNexus.DataAccess.Infrastructure.Serialization;
using System;

namespace DiffusionNexus.UI.Classes
{
    public class PromptProfileService
    {
        private readonly IConfigStore _store;

        public PromptProfileService() : this(new FileConfigStore(AppDataHelper.GetDataFolder(), new JsonSerializerAdapter()))
        {
        }

        public PromptProfileService(IConfigStore store)
        {
            _store = store;
        }

        private Task<List<PromptProfileModel>> LoadProfilesAsync()
        {
            var list = _store.Load<List<PromptProfileModel>>("prompt_profiles");
            return Task.FromResult(list);
        }

        private Task SaveProfilesToDiskAsync(List<PromptProfileModel> dict)
        {
            _store.Save("prompt_profiles", dict);
            return Task.CompletedTask;
        }

        public async Task<List<PromptProfileModel>> LoadAllAsync()
        {
            return await LoadProfilesAsync();
        }

        public async Task<PromptProfileModel?> GetProfileAsync(PromptProfileModel profile)
        {
            IList<PromptProfileModel> list = await LoadProfilesAsync();
            return list.FirstOrDefault(x => x.Name == profile.Name);
        }

        public async Task<bool> ExistsByNameAsync(string name)
        {
            IList<PromptProfileModel> dict = await LoadProfilesAsync();
            return dict.Any(x => x.Name == name);
        }

        public async Task SaveAsync(PromptProfileModel profile)
        {
            List<PromptProfileModel> dict = await LoadProfilesAsync();
            var existing = dict.FirstOrDefault(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Blacklist = profile.Blacklist;
                existing.Whitelist = profile.Whitelist;
            }
            else
            {
                dict.Add(profile);
            }
            await SaveProfilesToDiskAsync(dict);
        }

        public async Task DeleteAsync(PromptProfileModel profile)
        {
            List<PromptProfileModel> list = await LoadProfilesAsync();
            var existing = list.FirstOrDefault(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                list.Remove(existing);
                await SaveProfilesToDiskAsync(list);
            }
        }
    }
}
