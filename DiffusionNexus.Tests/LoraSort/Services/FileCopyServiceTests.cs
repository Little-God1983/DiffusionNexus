using FluentAssertions;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Enums;
using DiffusionNexus.Service.Services;
using System.Collections.ObjectModel;

namespace DiffusionNexus.Tests.LoraSort.Services;

public class FileCopyServiceTests : IDisposable
    {
        private readonly string _testSourcePath;
        private readonly string _testTargetPath;
        private readonly FileCopyService _service;
        private readonly string _mappingFilePath;

        public FileCopyServiceTests()
        {
            _testSourcePath = Path.Combine(Path.GetTempPath(), "LoraAutoSortTests_Source");
            _testTargetPath = Path.Combine(Path.GetTempPath(), "LoraAutoSortTests_Target");
            _service = new FileCopyService();
            _mappingFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mappings.xml");

            SetupTestDirectories();
            DeleteMappingFile();
        }

        private void SetupTestDirectories()
        {
            foreach (var path in new[] { _testSourcePath, _testTargetPath })
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
                Directory.CreateDirectory(path);
            }
        }

        private void DeleteMappingFile()
        {
            if (File.Exists(_mappingFilePath))
            {
                File.Delete(_mappingFilePath);
            }
        }

        [Fact]
        public void ProcessModelClasses_ShouldCopyFilesCorrectly()
        {
            // Arrange
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.LORA
                }
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = true,
                IsMoveOperation = false,
                OverrideFiles = true,
                UseCustomMappings = false
            };

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            // Act
            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            // Assert
            result.Should().BeFalse(); // No errors
            var expectedPath = Path.Combine(_testTargetPath, "SD 1.5", "CHARACTER", modelFile);
            File.Exists(expectedPath).Should().BeTrue();
            // Original file should still exist because it was a copy operation
            File.Exists(modelFilePath).Should().BeTrue();
        }

        [Fact]
        public void ProcessModelClasses_ShouldMoveFilesCorrectly()
        {
            // Arrange
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.LORA
                }
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = true,
                IsMoveOperation = true,
                OverrideFiles = true,
                UseCustomMappings = false
            };

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            // Act
            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            // Assert
            result.Should().BeFalse(); // No errors
            var expectedPath = Path.Combine(_testTargetPath, "SD 1.5", "CHARACTER", modelFile);
            File.Exists(expectedPath).Should().BeTrue();
            // Original file should not exist because it was a move operation
            File.Exists(modelFilePath).Should().BeFalse();
        }

        [Fact]
        public void ProcessModelClasses_WithCustomMapping_ShouldUseCorrectPath()
        {
            // Arrange
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.LORA,
                    Tags = new List<string> { "custom_tag" }
                }
            };

            // Mock the CustomTagMapXmlService by creating a test mapping file
            var customMapping = new CustomTagMap
            {
                LookForTag = new List<string> { "custom_tag" },
                MapToFolder = "CustomFolder",
                Priority = 0
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = true,
                IsMoveOperation = false,
                OverrideFiles = true,
                UseCustomMappings = true
            };

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            // Create test mapping file
            var xmlService = new CustomTagMapXmlService();
            xmlService.SaveMappings(new ObservableCollection<CustomTagMap> { customMapping });

            // Act
            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            // Assert
            result.Should().BeFalse(); // No errors
            var expectedPath = Path.Combine(_testTargetPath, "SD 1.5", "CustomFolder", modelFile);
            File.Exists(expectedPath).Should().BeTrue();
        }

        [Fact]
        public void ProcessModelClasses_WithCustomMapping_NoMatch_ShouldUseDefaultPath()
        {
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.LORA,
                    Tags = new List<string> { "custom_tag" }
                }
            };

            var customMapping = new CustomTagMap
            {
                LookForTag = new List<string> { "other_tag" },
                MapToFolder = "OtherFolder",
                Priority = 0
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = true,
                IsMoveOperation = false,
                OverrideFiles = true,
                UseCustomMappings = true
            };

            var xmlService = new CustomTagMapXmlService();
            xmlService.SaveMappings(new ObservableCollection<CustomTagMap> { customMapping });

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            result.Should().BeFalse();
            var expectedPath = Path.Combine(_testTargetPath, "SD 1.5", "CHARACTER", modelFile);
            File.Exists(expectedPath).Should().BeTrue();
        }

        [Fact]
        public void ProcessModelClasses_WithMultipleTagsInMapping_ShouldMatchAnyTag()
        {
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.LORA,
                    Tags = new List<string> { "TAG2" }
                }
            };

            var customMapping = new CustomTagMap
            {
                LookForTag = new List<string> { "tag1", "tag2" },
                MapToFolder = "CustomFolder",
                Priority = 0
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = true,
                IsMoveOperation = false,
                OverrideFiles = true,
                UseCustomMappings = true
            };

            var xmlService = new CustomTagMapXmlService();
            xmlService.SaveMappings(new ObservableCollection<CustomTagMap> { customMapping });

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            result.Should().BeFalse();
            var expectedPath = Path.Combine(_testTargetPath, "SD 1.5", "CustomFolder", modelFile);
            File.Exists(expectedPath).Should().BeTrue();
        }

        [Fact]
        public void ProcessModelClasses_MultipleMappings_ShouldRespectPriority()
        {
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.LORA,
                    Tags = new List<string> { "foo", "priority_tag", "bar" }
                }
            };

            var mapping1 = new CustomTagMap
            {
                LookForTag = new List<string> { "x", "priority_tag", "y" },
                MapToFolder = "Folder1",
                Priority = 3
            };
            var mapping2 = new CustomTagMap
            {
                LookForTag = new List<string> { "z", "priority_tag", "w" },
                MapToFolder = "Folder2",
                Priority = 2
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = true,
                IsMoveOperation = false,
                OverrideFiles = true,
                UseCustomMappings = true
            };

            var xmlService = new CustomTagMapXmlService();
            xmlService.SaveMappings(new ObservableCollection<CustomTagMap> { mapping1, mapping2 });

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            result.Should().BeFalse();
            var expectedPath = Path.Combine(_testTargetPath, "SD 1.5", "Folder2", modelFile);
            File.Exists(expectedPath).Should().BeTrue();
        }

        [Fact]
        public void ProcessModelClasses_ThreeMappings_SecondMapMatches()
        {
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.LORA,
                    Tags = new List<string> { "alpha", "beta", "gamma" }
                }
            };

            var mapping1 = new CustomTagMap
            {
                LookForTag = new List<string> { "one", "two", "three" },
                MapToFolder = "Folder1",
                Priority = 1
            };
            var mapping2 = new CustomTagMap
            {
                LookForTag = new List<string> { "four", "beta", "five" },
                MapToFolder = "MatchSecond",
                Priority = 2
            };
            var mapping3 = new CustomTagMap
            {
                LookForTag = new List<string> { "six", "seven", "eight" },
                MapToFolder = "Folder3",
                Priority = 3
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = true,
                IsMoveOperation = false,
                OverrideFiles = true,
                UseCustomMappings = true
            };

            var xmlService = new CustomTagMapXmlService();
            xmlService.SaveMappings(new ObservableCollection<CustomTagMap> { mapping1, mapping2, mapping3 });

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            result.Should().BeFalse();
            var expectedPath = Path.Combine(_testTargetPath, "SD 1.5", "MatchSecond", modelFile);
            File.Exists(expectedPath).Should().BeTrue();
        }

        [Fact]
        public void ProcessModelClasses_MultipleTags_SecondModelTagMatches()
        {
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.LORA,
                    Tags = new List<string> { "first", "special", "third" }
                }
            };

            var mapping1 = new CustomTagMap
            {
                LookForTag = new List<string> { "special", "foo", "bar" },
                MapToFolder = "SpecialFolder",
                Priority = 1
            };
            var mapping2 = new CustomTagMap
            {
                LookForTag = new List<string> { "x", "y", "z" },
                MapToFolder = "OtherFolder",
                Priority = 2
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = true,
                IsMoveOperation = false,
                OverrideFiles = true,
                UseCustomMappings = true
            };

            var xmlService = new CustomTagMapXmlService();
            xmlService.SaveMappings(new ObservableCollection<CustomTagMap> { mapping1, mapping2 });

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            result.Should().BeFalse();
            var expectedPath = Path.Combine(_testTargetPath, "SD 1.5", "SpecialFolder", modelFile);
            File.Exists(expectedPath).Should().BeTrue();
        }

        [Fact]
        public void ProcessModelClasses_AllMappingsMatch_ShouldUseHighestPriority()
        {
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.LORA,
                    Tags = new List<string> { "tagA", "tagB", "tagC" }
                }
            };

            var mapping1 = new CustomTagMap
            {
                LookForTag = new List<string> { "tagA", "x", "y" },
                MapToFolder = "Folder1",
                Priority = 3
            };
            var mapping2 = new CustomTagMap
            {
                LookForTag = new List<string> { "tagB", "m", "n" },
                MapToFolder = "Folder2",
                Priority = 2
            };
            var mapping3 = new CustomTagMap
            {
                LookForTag = new List<string> { "tagC", "p", "q" },
                MapToFolder = "Folder3",
                Priority = 1
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = true,
                IsMoveOperation = false,
                OverrideFiles = true,
                UseCustomMappings = true
            };

            var xmlService = new CustomTagMapXmlService();
            xmlService.SaveMappings(new ObservableCollection<CustomTagMap> { mapping1, mapping2, mapping3 });

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            result.Should().BeFalse();
            var expectedPath = Path.Combine(_testTargetPath, "SD 1.5", "Folder3", modelFile);
            File.Exists(expectedPath).Should().BeTrue();
        }

        [Fact]
        public void ProcessModelClasses_CustomMappingWithoutBaseFolders_ShouldOmitBaseModel()
        {
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.LORA,
                    Tags = new List<string> { "custom_tag" }
                }
            };

            var customMapping = new CustomTagMap
            {
                LookForTag = new List<string> { "custom_tag" },
                MapToFolder = "CustomFolder",
                Priority = 0
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = false,
                IsMoveOperation = false,
                OverrideFiles = true,
                UseCustomMappings = true
            };

            var xmlService = new CustomTagMapXmlService();
            xmlService.SaveMappings(new ObservableCollection<CustomTagMap> { customMapping });

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            result.Should().BeFalse();
            var expectedPath = Path.Combine(_testTargetPath, "CustomFolder", modelFile);
            File.Exists(expectedPath).Should().BeTrue();
        }

        [Fact]
        public void ProcessModelClasses_TagMatching_ShouldBeCaseInsensitive()
        {
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.LORA,
                    Tags = new List<string> { "CUSTOM_TAG" }
                }
            };

            var customMapping = new CustomTagMap
            {
                LookForTag = new List<string> { "custom_tag" },
                MapToFolder = "CaseFolder",
                Priority = 0
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = true,
                IsMoveOperation = false,
                OverrideFiles = true,
                UseCustomMappings = true
            };

            var xmlService = new CustomTagMapXmlService();
            xmlService.SaveMappings(new ObservableCollection<CustomTagMap> { customMapping });

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            result.Should().BeFalse();
            var expectedPath = Path.Combine(_testTargetPath, "SD 1.5", "CaseFolder", modelFile);
            File.Exists(expectedPath).Should().BeTrue();
        }

        [Fact]
        public void ProcessModelClasses_ShouldSkipNonLoraFiles()
        {
            // Arrange
            var modelFile = "test_model.safetensors";
            var modelFilePath = Path.Combine(_testSourcePath, modelFile);
            File.WriteAllText(modelFilePath, "test content");

            var models = new List<ModelClass>
            {
                new ModelClass
                {
                    SafeTensorFileName = "test_model",
                    AssociatedFilesInfo = new List<FileInfo> { new FileInfo(modelFilePath) },
                    NoMetaData = false,
                    DiffusionBaseModel = "SD 1.5",
                    CivitaiCategory = CivitaiBaseCategories.CHARACTER,
                    ModelType = DiffusionTypes.EMBEDDING // Not a LORA
                }
            };

            var options = new SelectedOptions
            {
                TargetPath = _testTargetPath,
                CreateBaseFolders = true,
                IsMoveOperation = false,
                OverrideFiles = true,
                UseCustomMappings = false
            };

            var progress = new Progress<ProgressReport>();
            var cts = new CancellationTokenSource();

            // Act
            bool result = _service.ProcessModelClasses(progress, cts.Token, models, options);

            // Assert
            result.Should().BeTrue(); // Should have errors because file was skipped
            var expectedPath = Path.Combine(_testTargetPath, "SD 1.5", "CHARACTER", modelFile);
            File.Exists(expectedPath).Should().BeFalse();
            // Original file should still exist
            File.Exists(modelFilePath).Should().BeTrue();
        }

        void IDisposable.Dispose()
        {
            foreach (var path in new[] { _testSourcePath, _testTargetPath })
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            DeleteMappingFile();
        }
    }
