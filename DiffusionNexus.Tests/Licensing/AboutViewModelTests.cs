using System.Collections.Generic;
using System.Linq;
using DiffusionNexus.UI.Services.Licensing;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Licensing;

public class AboutViewModelTests
{
    private static ThirdPartyComponent Component(string id, string license) =>
        new(id, "1.0.0", license, $"Copyright (c) {id}", id, $"https://example.com/{id}", $"{license} text");

    private static readonly List<ThirdPartyComponent> Sample =
    [
        Component("Avalonia", "MIT"),
        Component("LibVLCSharp", "LGPL-2.1-or-later"),
        Component("HPPH.SkiaSharp", "LGPL-2.1-only"),
        Component("Serilog", "Apache-2.0")
    ];

    private static AboutViewModel CreateViewModel() => new(Sample);

    [Fact]
    public void WhenCreatedThenAllComponentsAreListed()
    {
        var vm = CreateViewModel();

        vm.Components.Should().HaveCount(4);
        vm.TotalComponentCount.Should().Be(4);
    }

    [Fact]
    public void WhenCreatedThenFirstComponentIsSelected()
    {
        var vm = CreateViewModel();

        vm.SelectedComponent.Should().NotBeNull();
        vm.SelectedComponent!.Id.Should().Be("Avalonia");
    }

    [Fact]
    public void WhenCreatedThenAppVersionIsPopulated()
    {
        var vm = CreateViewModel();

        vm.AppVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void WhenSearchingByIdThenOnlyMatchingComponentsRemain()
    {
        var vm = CreateViewModel();

        vm.SearchText = "serilog";

        vm.Components.Should().ContainSingle().Which.Id.Should().Be("Serilog");
    }

    [Fact]
    public void WhenSearchingByLicenseThenEveryComponentUnderThatLicenseIsFound()
    {
        // The question this screen exists to answer: what here is copyleft?
        var vm = CreateViewModel();

        vm.SearchText = "LGPL";

        vm.Components.Select(c => c.Id)
            .Should().BeEquivalentTo(["LibVLCSharp", "HPPH.SkiaSharp"]);
    }

    [Fact]
    public void WhenSearchIsClearedThenAllComponentsReturn()
    {
        var vm = CreateViewModel();
        vm.SearchText = "serilog";

        vm.SearchText = "";

        vm.Components.Should().HaveCount(4);
    }

    [Fact]
    public void WhenSearchMatchesNothingThenListIsEmptyAndSelectionIsCleared()
    {
        var vm = CreateViewModel();

        vm.SearchText = "no-such-package";

        vm.Components.Should().BeEmpty();
        vm.SelectedComponent.Should().BeNull();
    }

    [Fact]
    public void WhenFilterExcludesTheSelectedComponentThenSelectionMovesToTheFirstMatch()
    {
        var vm = CreateViewModel();
        vm.SelectedComponent = Sample.Single(c => c.Id == "Serilog");

        vm.SearchText = "LGPL";

        vm.SelectedComponent.Should().NotBeNull();
        vm.Components.Should().Contain(vm.SelectedComponent!);
    }

    [Fact]
    public void WhenFilterStillIncludesTheSelectedComponentThenSelectionIsKept()
    {
        var vm = CreateViewModel();
        vm.SelectedComponent = Sample.Single(c => c.Id == "LibVLCSharp");

        vm.SearchText = "LGPL";

        vm.SelectedComponent!.Id.Should().Be("LibVLCSharp");
    }

    [Fact]
    public void WhenSearchTextHasSurroundingWhitespaceThenItIsIgnored()
    {
        var vm = CreateViewModel();

        vm.SearchText = "  serilog  ";

        vm.Components.Should().ContainSingle();
    }

    [Fact]
    public void WhenComponentHasNoCopyrightThenAttributionFallsBackToAuthors()
    {
        var component = new ThirdPartyComponent("X", "1.0", "MIT", "", "Some Author", "", "text");

        component.Attribution.Should().Be("Copyright (c) Some Author");
    }

    [Fact]
    public void WhenComponentHasNeitherCopyrightNorAuthorsThenAttributionSaysSo()
    {
        var component = new ThirdPartyComponent("X", "1.0", "MIT", "", "", "", "text");

        component.Attribution.Should().Be("(no copyright notice declared)");
    }
}
