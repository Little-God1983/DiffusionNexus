using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.Service.Search;
using FluentAssertions;
using Moq;
using Xunit;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DiffusionNexus.Tests.LoraSort.ViewModels;

public class LoraHelperFilterTests
{
    private static LoraHelperViewModel CreateViewModel()
    {
        var mock = new Mock<ISettingsService>();
        mock.Setup(s => s.LoadAsync()).ReturnsAsync(new SettingsModel());
        mock.Setup(s => s.SaveAsync(It.IsAny<SettingsModel>())).Returns(Task.CompletedTask);
        return new LoraHelperViewModel(mock.Object);
    }

    private static LoraCardViewModel CreateCard(string fileName, string? versionName = null)
    {
        var model = new ModelClass
        {
            SafeTensorFileName = fileName,
            ModelVersionName = versionName ?? fileName,
            AssociatedFilesInfo = new List<FileInfo>()
        };

        var card = new LoraCardViewModel();
        card.InitializeVariants(new[]
        {
            new ModelVariantViewModel(model, LoraVariantClassifier.DefaultVariantLabel)
        });

        return card;
    }

    private static List<LoraCardViewModel> InvokeFilter(LoraHelperViewModel vm, string term)
    {
        var filterMethod = typeof(LoraHelperViewModel)
            .GetMethod("FilterCards", BindingFlags.NonPublic | BindingFlags.Instance);
        return (List<LoraCardViewModel>)filterMethod!.Invoke(vm, new object?[] { term, null })!;
    }

    private static void BuildIndex(LoraHelperViewModel vm, List<LoraCardViewModel> cards)
    {
        var allCardsField = typeof(LoraHelperViewModel)
            .GetField("_allCards", BindingFlags.NonPublic | BindingFlags.Instance);
        var list = (List<LoraCardViewModel>)allCardsField!.GetValue(vm)!;
        list.Clear();
        list.AddRange(cards);

        var indexNames = cards
            .Select(c => c.GetSearchIndexText())
            .ToList();

        var indexNamesField = typeof(LoraHelperViewModel)
            .GetField("_indexNames", BindingFlags.NonPublic | BindingFlags.Instance);
        indexNamesField!.SetValue(vm, indexNames);

        var searchIndexField = typeof(LoraHelperViewModel)
            .GetField("_searchIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        var searchIndex = (SearchIndex)searchIndexField!.GetValue(vm)!;
        searchIndex.Build(indexNames);
    }

    [Fact]
    public void FilterCards_PartialTermMatchesNightmare()
    {
        var vm = CreateViewModel();
        var cards = new List<LoraCardViewModel>
        {
            CreateCard("Fright Night"),
            CreateCard("0403 Halloween Nightmare_v1_pony"),
            CreateCard("t2v_model", "Nightmare Fuel 14b")
        };

        BuildIndex(vm, cards);
        var result = InvokeFilter(vm, "night");
        result.Select(c => c.Model!.SafeTensorFileName).Should()
            .BeEquivalentTo(new[] { "Fright Night", "0403 Halloween Nightmare_v1_pony", "t2v_model" });
    }

    [Fact]
    public void FilterCards_SearchesModelVersionName()
    {
        var vm = CreateViewModel();
        var cards = new List<LoraCardViewModel>
        {
            CreateCard("spooky_model", "Spooky Version")
        };

        BuildIndex(vm, cards);
        var result = InvokeFilter(vm, "spooky");
        result.Should().HaveCount(1);
        result[0].Model!.ModelVersionName.Should().Be("Spooky Version");
    }
}

