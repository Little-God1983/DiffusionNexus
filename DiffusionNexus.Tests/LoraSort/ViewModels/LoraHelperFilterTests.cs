using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Search;
using DiffusionNexus.UI.Classes;
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

    private static LoraCardViewModel Card(string safeName, string? versionName = null)
    {
        return new LoraCardViewModel
        {
            Model = new ModelClass
            {
                SafeTensorFileName = safeName,
                ModelVersionName = versionName,
                AssociatedFilesInfo = new List<FileInfo>()
            }
        };
    }

    private static void BuildIndex(LoraHelperViewModel vm, List<LoraCardViewModel> cards)
    {
        var indexNamesField = typeof(LoraHelperViewModel).GetField("_indexNames", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var searchIndexField = typeof(LoraHelperViewModel).GetField("_searchIndex", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var searchIndex = (SearchIndex)searchIndexField.GetValue(vm)!;

        var names = cards.Select(c =>
            string.Join(' ', new[] { c.Model.SafeTensorFileName, c.Model.ModelVersionName }
                .Where(s => !string.IsNullOrWhiteSpace(s)))).ToList();

        indexNamesField.SetValue(vm, names);
        searchIndex.Build(names);
    }

    private static List<LoraCardViewModel> InvokeFilter(LoraHelperViewModel vm, string search)
    {
        var filter = typeof(LoraHelperViewModel).GetMethod("FilterCards", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (List<LoraCardViewModel>)filter.Invoke(vm, new object?[] { search, null })!;
    }

    [Fact]
    public void FilterCards_PartialMatchesAcrossFields()
    {
        var vm = CreateViewModel();
        var cards = new List<LoraCardViewModel>
        {
            Card("Fright Night 1985 style v2"),
            Card("0403 Halloween Nightmare_v1_pony"),
            Card("T2V - Nightmare Fuel - 14b")
        };
        var allCardsField = typeof(LoraHelperViewModel).GetField("_allCards", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<LoraCardViewModel>)allCardsField.GetValue(vm)!;
        list.AddRange(cards);
        BuildIndex(vm, list);

        var result = InvokeFilter(vm, "night");
        result.Select(c => c.Model!.SafeTensorFileName).Should().BeEquivalentTo(
            cards.Select(c => c.Model!.SafeTensorFileName));
    }

    [Fact]
    public void FilterCards_CanMatchModelVersionName()
    {
        var vm = CreateViewModel();
        var card = Card("random_file", "Midnight Fury");
        var allCardsField = typeof(LoraHelperViewModel).GetField("_allCards", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<LoraCardViewModel>)allCardsField.GetValue(vm)!;
        list.Add(card);
        BuildIndex(vm, list);

        var result = InvokeFilter(vm, "MIDNIGHT");
        result.Should().ContainSingle().Which.Model!.ModelVersionName.Should().Be("Midnight Fury");
    }

    [Fact]
    public void FilterCards_PartialModelVersionMatch()
    {
        var vm = CreateViewModel();
        var card = Card("random_file", "Midnight Fury");
        var allCardsField = typeof(LoraHelperViewModel).GetField("_allCards", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<LoraCardViewModel>)allCardsField.GetValue(vm)!;
        list.Add(card);
        BuildIndex(vm, list);

        var result = InvokeFilter(vm, "fur");
        result.Should().ContainSingle().Which.Model!.ModelVersionName.Should().Be("Midnight Fury");
    }
}

