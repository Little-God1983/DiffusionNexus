
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using System.Collections.ObjectModel;
using System.IO;
using Xunit;

namespace DiffusionNexus.Tests.Service
{
    public class CustomTagMapXmlServiceTests : IDisposable
    {
        private readonly CustomTagMapXmlService _service;
        private readonly string _testFilePath;

        public CustomTagMapXmlServiceTests()
        {
            _testFilePath = Path.Combine(Path.GetTempPath(), $"test_mappings_{Guid.NewGuid()}.xml");
            _service = new CustomTagMapXmlService(_testFilePath);
        }

        public void Dispose()
        {
            if (File.Exists(_testFilePath))
            {
                File.Delete(_testFilePath);
            }
        }

        private ObservableCollection<CustomTagMap> CreateSampleMappings()
        {
            return new ObservableCollection<CustomTagMap>
            {
                new CustomTagMap { LookForTag = new List<string> { "tag1" }, MapToFolder = "Folder1", Priority = 1 },
                new CustomTagMap { LookForTag = new List<string> { "tag2" }, MapToFolder = "Folder2", Priority = 2 }
            };
        }

        [Fact]
        public void SaveMappings_ShouldCreateAndWriteToFile()
        {
            // Arrange
            var mappings = CreateSampleMappings();

            // Act
            _service.SaveMappings(mappings);

            // Assert
            File.Exists(_testFilePath).Should().BeTrue();
            var loadedMappings = _service.LoadMappings();
            loadedMappings.Should().BeEquivalentTo(mappings);
        }

        [Fact]
        public void SaveMappings_ShouldOverwriteExistingFile()
        {
            // Arrange
            var initialMappings = CreateSampleMappings();
            _service.SaveMappings(initialMappings);

            var newMappings = new ObservableCollection<CustomTagMap>
            {
                new CustomTagMap { LookForTag = new List<string> { "tag3" }, MapToFolder = "Folder3", Priority = 3 }
            };

            // Act
            _service.SaveMappings(newMappings);

            // Assert
            var loadedMappings = _service.LoadMappings();
            loadedMappings.Should().NotBeEquivalentTo(initialMappings);
            loadedMappings.Should().BeEquivalentTo(newMappings);
        }

        [Fact]
        public void LoadMappings_ShouldReturnEmptyCollection_WhenFileDoesNotExist()
        {
            // Act
            var mappings = _service.LoadMappings();

            // Assert
            mappings.Should().NotBeNull();
            mappings.Should().BeEmpty();
        }

        [Fact]
        public void LoadMappings_ShouldLoadMappingsFromFile()
        {
            // Arrange
            var mappingsToSave = CreateSampleMappings();
            _service.SaveMappings(mappingsToSave);

            // Act
            var loadedMappings = _service.LoadMappings();

            // Assert
            loadedMappings.Should().BeEquivalentTo(mappingsToSave);
        }

        [Fact]
        public void LoadMappings_ShouldReturnEmptyCollection_OnException()
        {
            // Arrange
            File.WriteAllText(_testFilePath, "invalid xml");

            // Act
            var mappings = _service.LoadMappings();

            // Assert
            mappings.Should().NotBeNull();
            mappings.Should().BeEmpty();
        }

        [Fact]
        public void DeleteAllMappings_ShouldDeleteFile()
        {
            // Arrange
            var mappings = CreateSampleMappings();
            _service.SaveMappings(mappings);
            File.Exists(_testFilePath).Should().BeTrue();

            // Act
            _service.DeleteAllMappings(_testFilePath);

            // Assert
            File.Exists(_testFilePath).Should().BeFalse();
        }

        [Fact]
        public void DeleteAllMappings_ShouldNotThrow_WhenFileDoesNotExist()
        {
            // Arrange
            File.Exists(_testFilePath).Should().BeFalse();

            // Act
            var act = () => _service.DeleteAllMappings(_testFilePath);

            // Assert
            act.Should().NotThrow();
        }
    }
}
