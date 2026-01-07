using DiffusionNexus.UI.Utilities;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Utilities;

/// <summary>
/// Unit tests for <see cref="MediaFileExtensions"/> utility class.
/// Tests file type detection, extension handling, and edge cases.
/// </summary>
public class MediaFileExtensionsTests
{
    #region IsImageFile Tests

    [Theory]
    [InlineData("image.png", true)]
    [InlineData("image.PNG", true)]
    [InlineData("image.jpg", true)]
    [InlineData("image.jpeg", true)]
    [InlineData("image.webp", true)]
    [InlineData("image.bmp", true)]
    [InlineData("image.gif", true)]
    [InlineData("path/to/image.png", true)]
    [InlineData(@"C:\folder\image.JPG", true)]
    public void IsImageFile_WhenValidImageExtension_ReturnsTrue(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsImageFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("video.mp4", false)]
    [InlineData("document.txt", false)]
    [InlineData("file.pdf", false)]
    [InlineData("no_extension", false)]
    public void IsImageFile_WhenNotImageExtension_ReturnsFalse(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsImageFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsImageFile_WhenNullOrEmpty_ReturnsFalse(string? filePath)
    {
        var result = MediaFileExtensions.IsImageFile(filePath!);
        result.Should().BeFalse();
    }

    #endregion

    #region IsVideoFile Tests

    [Theory]
    [InlineData("video.mp4", true)]
    [InlineData("video.MP4", true)]
    [InlineData("video.mov", true)]
    [InlineData("video.webm", true)]
    [InlineData("video.avi", true)]
    [InlineData("video.mkv", true)]
    [InlineData("video.wmv", true)]
    [InlineData("video.flv", true)]
    [InlineData("video.m4v", true)]
    [InlineData("path/to/video.mp4", true)]
    public void IsVideoFile_WhenValidVideoExtension_ReturnsTrue(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsVideoFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("image.png", false)]
    [InlineData("document.txt", false)]
    [InlineData("file.pdf", false)]
    public void IsVideoFile_WhenNotVideoExtension_ReturnsFalse(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsVideoFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsVideoFile_WhenNullOrEmpty_ReturnsFalse(string? filePath)
    {
        var result = MediaFileExtensions.IsVideoFile(filePath!);
        result.Should().BeFalse();
    }

    #endregion

    #region IsMediaFile Tests

    [Theory]
    [InlineData("image.png", true)]
    [InlineData("video.mp4", true)]
    [InlineData("image.JPEG", true)]
    [InlineData("video.MKV", true)]
    public void IsMediaFile_WhenImageOrVideo_ReturnsTrue(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsMediaFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("caption.txt", false)]
    [InlineData("document.pdf", false)]
    [InlineData("data.json", false)]
    public void IsMediaFile_WhenNotMedia_ReturnsFalse(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsMediaFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsMediaFile_WhenNullOrEmpty_ReturnsFalse(string? filePath)
    {
        var result = MediaFileExtensions.IsMediaFile(filePath!);
        result.Should().BeFalse();
    }

    #endregion

    #region IsCaptionFile Tests

    [Theory]
    [InlineData("caption.txt", true)]
    [InlineData("caption.TXT", true)]
    [InlineData("caption.caption", true)]
    [InlineData("path/to/file.txt", true)]
    public void IsCaptionFile_WhenValidCaptionExtension_ReturnsTrue(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsCaptionFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("image.png", false)]
    [InlineData("video.mp4", false)]
    [InlineData("document.pdf", false)]
    public void IsCaptionFile_WhenNotCaptionExtension_ReturnsFalse(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsCaptionFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCaptionFile_WhenNullOrEmpty_ReturnsFalse(string? filePath)
    {
        var result = MediaFileExtensions.IsCaptionFile(filePath!);
        result.Should().BeFalse();
    }

    #endregion

    #region IsVideoThumbnailFile Tests

    [Theory]
    [InlineData("video_thumb.webp", true)]
    [InlineData("video_thumb.jpg", true)]
    [InlineData("video_thumb.png", true)]
    [InlineData("my_video_thumb.webp", true)]
    [InlineData("path/to/video_thumb.WEBP", true)]
    public void IsVideoThumbnailFile_WhenValidThumbnail_ReturnsTrue(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsVideoThumbnailFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("video.webp", false)]
    [InlineData("thumb_video.webp", false)]
    [InlineData("video_thumb.gif", false)]
    [InlineData("video_thumb.mp4", false)]
    [InlineData("image.png", false)]
    public void IsVideoThumbnailFile_WhenNotThumbnail_ReturnsFalse(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsVideoThumbnailFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsVideoThumbnailFile_WhenNullOrEmpty_ReturnsFalse(string? filePath)
    {
        var result = MediaFileExtensions.IsVideoThumbnailFile(filePath!);
        result.Should().BeFalse();
    }

    #endregion

    #region GetVideoThumbnailPath Tests

    [Fact]
    public void GetVideoThumbnailPath_WhenValidPath_ReturnsThumbnailPath()
    {
        var videoPath = Path.Combine("folder", "video.mp4");
        var expected = Path.Combine("folder", "video_thumb.webp");

        var result = MediaFileExtensions.GetVideoThumbnailPath(videoPath);

        result.Should().Be(expected);
    }

    [Fact]
    public void GetVideoThumbnailPath_WhenNoDirectory_ReturnsThumbnailInSameLocation()
    {
        var videoPath = "video.mp4";
        var expected = "video_thumb.webp";

        var result = MediaFileExtensions.GetVideoThumbnailPath(videoPath);

        result.Should().Be(expected);
    }

    [Fact]
    public void GetVideoThumbnailPath_WhenAbsolutePath_ReturnsThumbnailPath()
    {
        var videoPath = Path.Combine("C:", "Users", "Test", "Videos", "clip.mov");
        var expected = Path.Combine("C:", "Users", "Test", "Videos", "clip_thumb.webp");

        var result = MediaFileExtensions.GetVideoThumbnailPath(videoPath);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetVideoThumbnailPath_WhenNullOrEmpty_ThrowsArgumentException(string? videoPath)
    {
        var act = () => MediaFileExtensions.GetVideoThumbnailPath(videoPath!);

        act.Should().Throw<ArgumentException>()
           .WithParameterName("videoPath");
    }

    #endregion

    #region IsDisplayableMediaFile Tests

    [Theory]
    [InlineData("image.png", true)]
    [InlineData("video.mp4", true)]
    [InlineData("photo.jpg", true)]
    public void IsDisplayableMediaFile_WhenMediaNotThumbnail_ReturnsTrue(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsDisplayableMediaFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("video_thumb.webp", false)]
    [InlineData("image_thumb.jpg", false)]
    public void IsDisplayableMediaFile_WhenThumbnail_ReturnsFalse(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsDisplayableMediaFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("caption.txt", false)]
    [InlineData("document.pdf", false)]
    public void IsDisplayableMediaFile_WhenNotMedia_ReturnsFalse(string filePath, bool expected)
    {
        var result = MediaFileExtensions.IsDisplayableMediaFile(filePath);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsDisplayableMediaFile_WhenNullOrEmpty_ReturnsFalse(string? filePath)
    {
        var result = MediaFileExtensions.IsDisplayableMediaFile(filePath!);
        result.Should().BeFalse();
    }

    #endregion

    #region Extension Arrays Tests

    [Fact]
    public void ImageExtensions_ContainsExpectedExtensions()
    {
        MediaFileExtensions.ImageExtensions.Should().Contain(new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" });
    }

    [Fact]
    public void VideoExtensions_ContainsExpectedExtensions()
    {
        MediaFileExtensions.VideoExtensions.Should().Contain(new[] { ".mp4", ".mov", ".webm", ".avi", ".mkv", ".wmv", ".flv", ".m4v" });
    }

    [Fact]
    public void CaptionExtensions_ContainsExpectedExtensions()
    {
        MediaFileExtensions.CaptionExtensions.Should().Contain(new[] { ".txt", ".caption" });
    }

    [Fact]
    public void MediaExtensions_ContainsAllImageAndVideoExtensions()
    {
        MediaFileExtensions.MediaExtensions.Should()
            .Contain(MediaFileExtensions.ImageExtensions)
            .And.Contain(MediaFileExtensions.VideoExtensions);
    }

    #endregion
}
