using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using FluentAssertions;
using System.Collections.ObjectModel;
using Xunit;

namespace DiffusionNexus.Tests.Service.Helper;

public class CustomTagMapPriorityHelperTests
{
    [Theory]
    [InlineData(3,1,2)]
    [InlineData(5,2,1)]
    public void Normalize_SortsAndResetsPriorities(int first, int second, int third)
    {
        // Arrange
        var maps = new List<CustomTagMap>
        {
            new() { MapToFolder = "A", Priority = first },
            new() { MapToFolder = "B", Priority = second },
            new() { MapToFolder = "C", Priority = third }
        };

        // Act
        var result = CustomTagMapPriorityHelper.Normalize(maps);

        // Assert
        result.Select(m => m.Priority).Should().Equal(1,2,3);
    }

    [Fact]
    public void MoveUp_FirstItem_NoChange()
    {
        // Arrange
        var map1 = new CustomTagMap { MapToFolder = "one", Priority = 1 };
        var map2 = new CustomTagMap { MapToFolder = "two", Priority = 2 };
        var maps = new ObservableCollection<CustomTagMap> { map1, map2 };

        // Act
        CustomTagMapPriorityHelper.MoveUp(maps, map1);

        // Assert
        map1.Priority.Should().Be(1);
        map2.Priority.Should().Be(2);
    }

    [Fact]
    public void MoveUp_MiddleItem_SwapsWithPrevious()
    {
        // Arrange
        var map1 = new CustomTagMap { MapToFolder = "one", Priority = 1 };
        var map2 = new CustomTagMap { MapToFolder = "two", Priority = 2 };
        var map3 = new CustomTagMap { MapToFolder = "three", Priority = 3 };
        var maps = new ObservableCollection<CustomTagMap> { map1, map2, map3 };

        // Act
        CustomTagMapPriorityHelper.MoveUp(maps, map2);

        // Assert
        map2.Priority.Should().Be(1);
        map1.Priority.Should().Be(2);
        map3.Priority.Should().Be(3);
    }

    [Fact]
    public void MoveDown_LastItem_NoChange()
    {
        // Arrange
        var map1 = new CustomTagMap { MapToFolder = "one", Priority = 1 };
        var map2 = new CustomTagMap { MapToFolder = "two", Priority = 2 };
        var maps = new ObservableCollection<CustomTagMap> { map1, map2 };

        // Act
        CustomTagMapPriorityHelper.MoveDown(maps, map2);

        // Assert
        map1.Priority.Should().Be(1);
        map2.Priority.Should().Be(2);
    }

    [Fact]
    public void MoveDown_MiddleItem_SwapsWithNext()
    {
        // Arrange
        var map1 = new CustomTagMap { MapToFolder = "one", Priority = 1 };
        var map2 = new CustomTagMap { MapToFolder = "two", Priority = 2 };
        var map3 = new CustomTagMap { MapToFolder = "three", Priority = 3 };
        var maps = new ObservableCollection<CustomTagMap> { map1, map2, map3 };

        // Act
        CustomTagMapPriorityHelper.MoveDown(maps, map2);

        // Assert
        map1.Priority.Should().Be(1);
        map2.Priority.Should().Be(3);
        map3.Priority.Should().Be(2);
    }
}
