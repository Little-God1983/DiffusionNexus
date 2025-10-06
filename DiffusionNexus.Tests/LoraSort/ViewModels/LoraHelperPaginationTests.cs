using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.Classes;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace DiffusionNexus.Tests.LoraSort.ViewModels;

public class LoraHelperPaginationTests
{
    private static LoraHelperViewModel CreateViewModel()
    {
        var mock = new Mock<ISettingsService>();
        mock.Setup(s => s.LoadAsync()).ReturnsAsync(new SettingsModel());
        mock.Setup(s => s.SaveAsync(It.IsAny<SettingsModel>())).Returns(Task.CompletedTask);
        return new LoraHelperViewModel(mock.Object);
    }

    private static LoraCardViewModel CreateCard(string fileName)
    {
        var model = new ModelClass
        {
            SafeTensorFileName = fileName,
            ModelVersionName = fileName,
            AssociatedFilesInfo = new List<FileInfo>()
        };

        var card = new LoraCardViewModel();
        card.InitializeVariants(new[]
        {
            new ModelVariantViewModel(model, LoraVariantClassifier.DefaultVariantLabel)
        });

        return card;
    }

    [Fact]
    public void HasMoreCards_ReturnsTrueWhenFilteredExceedsLoaded()
    {
        var vm = CreateViewModel();
        var filteredField = typeof(LoraHelperViewModel)
            .GetField("_filteredCards", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var filtered = new List<LoraCardViewModel>
        {
            CreateCard("a"),
            CreateCard("b"),
        };

        filteredField!.SetValue(vm, filtered);

        vm.Cards.Clear();
        vm.Cards.Add(filtered[0]);

        vm.HasMoreCards.Should().BeTrue();
    }

    [Fact]
    public void HasMoreCards_ReturnsFalseWhenAllCardsLoaded()
    {
        var vm = CreateViewModel();
        var filteredField = typeof(LoraHelperViewModel)
            .GetField("_filteredCards", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var filtered = new List<LoraCardViewModel>
        {
            CreateCard("a"),
            CreateCard("b"),
        };

        filteredField!.SetValue(vm, filtered);

        vm.Cards.Clear();
        vm.Cards.Add(filtered[0]);
        vm.Cards.Add(filtered[1]);

        vm.HasMoreCards.Should().BeFalse();
    }
}
