using Moq;
using FluentAssertions;
using DiffusionNexus.Service.Services;

namespace DiffusionNexus.Tests.LoraSort.Services;

public class CivitaiMetaDataServiceTests
    {
        private readonly Mock<ICivitaiApiClient> _mockApiClient;
        private readonly CivitaiMetaDataService _service;
        private const string TestApiKey = "test-api-key";

        public CivitaiMetaDataServiceTests()
        {
            _mockApiClient = new Mock<ICivitaiApiClient>();
            _service = new CivitaiMetaDataService(_mockApiClient.Object, TestApiKey);
        }

        [Fact]
        public async Task GetModelVersionInformationFromCivitaiAsync_ShouldCallApiClientWithCorrectParameters()
        {
            // Arrange
            var sha256Hash = "test-hash";
            var expectedResponse = "test-response";
            _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(sha256Hash, TestApiKey))
                         .ReturnsAsync(expectedResponse);

            // Act
            var result = await _service.GetModelVersionInformationFromCivitaiAsync(sha256Hash);

            // Assert
            result.Should().Be(expectedResponse);
            _mockApiClient.Verify(x => x.GetModelVersionByHashAsync(sha256Hash, TestApiKey), Times.Once);
        }

        [Fact]
        public void GetModelType_WhenTypeIsInRoot_ShouldReturnCorrectType()
        {
            // Arrange
            var json = @"{
                ""type"": ""LORA""
            }";

            // Act
            var result = _service.GetModelType(json);

            // Assert
            result.Should().Be(DiffusionTypes.LORA);
        }

        [Fact]
        public void GetModelType_WhenTypeIsInModel_ShouldReturnCorrectType()
        {
            // Arrange
            var json = @"{
                ""model"": {
                    ""type"": ""LOCON""
                }
            }";

            // Act
            var result = _service.GetModelType(json);

            // Assert
            result.Should().Be(DiffusionTypes.LOCON);
        }

        [Fact]
        public void GetModelType_WhenTypeIsNotFound_ShouldReturnOther()
        {
            // Arrange
            var json = @"{
                ""someOtherProperty"": ""value""
            }";

            // Act
            var result = _service.GetModelType(json);

            // Assert
            result.Should().Be(DiffusionTypes.UNASSIGNED);
        }

        [Fact]
        public void GetTagsFromModelInfo_ShouldReturnCorrectTags()
        {
            // Arrange
            var json = @"{
                ""tags"": [""tag1"", ""tag2"", ""tag3""]
            }";

            // Act
            var result = _service.GetTagsFromModelInfo(json);

            // Assert
            result.Should().BeEquivalentTo(new[] { "tag1", "tag2", "tag3" });
        }

        [Fact]
        public void GetTagsFromModelInfo_WhenTagsAreEmpty_ShouldReturnEmptyList()
        {
            // Arrange
            var json = @"{
                ""tags"": []
            }";

            // Act
            var result = _service.GetTagsFromModelInfo(json);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public void GetTagsFromModelInfo_WhenTagsAreNotPresent_ShouldReturnEmptyList()
        {
            // Arrange
            var json = @"{
                ""otherProperty"": ""value""
            }";

            // Act
            var result = _service.GetTagsFromModelInfo(json);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public void GetBaseModelName_ShouldReturnCorrectModelName()
        {
            // Arrange
            var json = @"{
                ""baseModel"": ""SD 1.5""
            }";

            // Act
            var result = _service.GetBaseModelName(json);

            // Assert
            result.Should().Be("SD 1.5");
        }

        [Fact]
        public void GetModelId_ShouldReturnCorrectId()
        {
            // Arrange
            var json = @"{
                ""modelId"": ""12345""
            }";

            // Act
            var result = _service.GetModelId(json);

            // Assert
            result.Should().Be("12345");
        }
    }

