using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.Classes;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.LoraSort.ViewModels;

public class LoraHelperSortTests
{
    private static LoraHelperViewModel CreateViewModel()
    {
        var mock = new Mock<ISettingsService>();
        mock.Setup(s => s.LoadAsync()).ReturnsAsync(new SettingsModel());
        mock.Setup(s => s.SaveAsync(It.IsAny<SettingsModel>())).Returns(Task.CompletedTask);
        return new LoraHelperViewModel(mock.Object);
    }

    private static LoraCardViewModel CreateCard(string filePath)
    {
        var model = new ModelClass
        {
            SafeTensorFileName = Path.GetFileNameWithoutExtension(filePath),
            AssociatedFilesInfo = new List<FileInfo> { new FileInfo(filePath) }
        };
        return new LoraCardViewModel { Model = model };
    }

    [Fact]
    public void ApplySort_ByName_SortsAlphabetically()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.safetensors");
            var b = Path.Combine(dir, "b.safetensors");
            var c = Path.Combine(dir, "c.safetensors");
            File.WriteAllText(b, string.Empty);
            File.WriteAllText(a, string.Empty);
            File.WriteAllText(c, string.Empty);

            var cards = new List<LoraCardViewModel>
            {
                CreateCard(b),
                CreateCard(a),
                CreateCard(c)
            };

            var vm = CreateViewModel();
            vm.SortMode = SortMode.Name;
            var result = vm.ApplySort(cards).ToList();
            result.Select(r => r.Model!.SafeTensorFileName).Should().Equal("a", "b", "c");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ApplySort_ByCreationDate_NewestFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var oldFile = Path.Combine(dir, "old.safetensors");
            var newFile = Path.Combine(dir, "new.safetensors");
            File.WriteAllText(oldFile, string.Empty);
            File.WriteAllText(newFile, string.Empty);
            File.SetCreationTime(oldFile, DateTime.Now.AddHours(-1));
            File.SetCreationTime(newFile, DateTime.Now);

            var cards = new List<LoraCardViewModel>
            {
                CreateCard(oldFile),
                CreateCard(newFile)
            };

            var vm = CreateViewModel();
            vm.SortMode = SortMode.CreationDate;
            var result = vm.ApplySort(cards).ToList();
            result.First().Model!.SafeTensorFileName.Should().Be("new");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
