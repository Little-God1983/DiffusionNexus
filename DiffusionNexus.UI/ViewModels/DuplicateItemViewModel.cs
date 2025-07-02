using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.LoraSort.Service.Classes;
using DiffusionNexus.LoraSort.Service.Services;
using DiffusionNexus.UI.Classes;
using System.IO;

namespace DiffusionNexus.UI.ViewModels;

public partial class DuplicateItemViewModel : ViewModelBase
{
    public FileInfo FileA { get; }
    public FileInfo FileB { get; }
    public ModelClass? MetaA { get; }
    public ModelClass? MetaB { get; }

    [ObservableProperty]
    private bool keepA;

    [ObservableProperty]
    private bool keepB;

    public double SizeMb => FileA.Length / 1024d / 1024d;

    public LoraCardViewModel? CardA { get; }
    public LoraCardViewModel? CardB { get; }

    public IRelayCommand ToggleKeepACommand { get; }
    public IRelayCommand ToggleKeepBCommand { get; }

    private readonly LoraHelperViewModel _parent;

    public DuplicateItemViewModel(LoraHelperViewModel parent, DuplicateSet set)
    {
        _parent = parent;
        FileA = set.FileA;
        FileB = set.FileB;
        MetaA = set.MetaA;
        MetaB = set.MetaB;
        if (MetaA != null)
            CardA = new LoraCardViewModel { Name = MetaA.ModelName, Model = MetaA, Parent = parent };
        if (MetaB != null)
            CardB = new LoraCardViewModel { Name = MetaB.ModelName, Model = MetaB, Parent = parent };
        ToggleKeepACommand = new RelayCommand(OnToggleA);
        ToggleKeepBCommand = new RelayCommand(OnToggleB);
    }

    partial void OnKeepAChanged(bool value)
    {
        if (value)
            _parent.AddKeepFile(FileA.FullName);
        else
            _parent.RemoveKeepFile(FileA.FullName);
        OnPropertyChanged(nameof(IsResolved));
    }

    partial void OnKeepBChanged(bool value)
    {
        if (value)
            _parent.AddKeepFile(FileB.FullName);
        else
            _parent.RemoveKeepFile(FileB.FullName);
        OnPropertyChanged(nameof(IsResolved));
    }

    private void OnToggleA() => KeepA = !KeepA;
    private void OnToggleB() => KeepB = !KeepB;

    public bool IsResolved => KeepA || KeepB;
}
