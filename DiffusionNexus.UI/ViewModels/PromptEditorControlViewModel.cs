using DiffusionNexus.UI.Models;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.Reactive;

namespace DiffusionNexus.UI.ViewModels
{
    public class PromptEditorControlViewModel : ReactiveObject
    {
        public ObservableCollection<PromptProfileModel> Profiles { get; }
        public PromptProfileModel SelectedProfile { get; set; }
        public string Blacklist { get; set; }
        public string Whitelist { get; set; }
        public string Prompt { get; set; }
        public string NegativePrompt { get; set; }

        public ReactiveCommand<Unit, Unit> SaveCommand { get; }
        public ReactiveCommand<Unit, Unit> LoadCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearCommand { get; }
        public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

        public PromptEditorControlViewModel()
        {
            SaveCommand = ReactiveCommand.Create(SavePrompt);
            LoadCommand = ReactiveCommand.Create(LoadPrompt);
            ClearCommand = ReactiveCommand.Create(ClearPrompt);
            DeleteCommand = ReactiveCommand.Create(() =>
            {
                if (SelectedProfile != null)
                {
                    Profiles.Remove(SelectedProfile);
                    SelectedProfile = null;
                }
            });
        }

        private void SavePrompt() { /* … */ }
        private void LoadPrompt() { /* … */ }
        private void ClearPrompt() { Prompt = string.Empty; NegativePrompt = string.Empty; }
    }
}

