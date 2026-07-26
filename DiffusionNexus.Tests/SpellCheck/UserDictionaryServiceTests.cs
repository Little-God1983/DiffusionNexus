using DiffusionNexus.UI.Services.SpellCheck;
using FluentAssertions;

namespace DiffusionNexus.Tests.SpellCheck;

/// <summary>
/// Unit tests for <see cref="UserDictionaryService"/>. The constructor accepts an
/// explicit file path, so every test points it at a throwaway temp directory
/// instead of %LocalAppData%.
/// </summary>
public class UserDictionaryServiceTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly string _dictionaryPath;

    public UserDictionaryServiceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory();
        _dictionaryPath = Path.Combine(_tempDir.FullName, "user_dictionary.txt");
    }

    public void Dispose()
    {
        try { _tempDir.Delete(recursive: true); }
        catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private UserDictionaryService CreateSut(string? path = null) => new(path ?? _dictionaryPath);

    #region Loading

    [Fact]
    public void WhenFileDoesNotExistThenDictionaryLoadsEmptyWithoutThrowing()
    {
        var sut = CreateSut();

        sut.GetAll().Should().BeEmpty();
        File.Exists(_dictionaryPath).Should().BeFalse();
    }

    [Fact]
    public void WhenDirectoryDoesNotExistThenLoadStillSucceeds()
    {
        var missing = Path.Combine(_tempDir.FullName, "nope", "deeper", "dict.txt");

        var act = () => CreateSut(missing);

        act.Should().NotThrow();
    }

    [Fact]
    public void WhenFileHasWordsThenTheyAreLoaded()
    {
        File.WriteAllLines(_dictionaryPath, new[] { "alpha", "beta", "gamma" });

        var sut = CreateSut();

        sut.GetAll().Should().BeEquivalentTo(new[] { "alpha", "beta", "gamma" });
    }

    [Fact]
    public void WhenFileHasBlankAndPaddedLinesThenTheyAreTrimmedAndSkipped()
    {
        File.WriteAllLines(_dictionaryPath, new[] { "  padded  ", "", "   ", "\tkeep\t" });

        var sut = CreateSut();

        sut.GetAll().Should().BeEquivalentTo(new[] { "padded", "keep" });
    }

    [Fact]
    public void WhenFileHasCaseVariantsThenTheyCollapseToOneEntry()
    {
        File.WriteAllLines(_dictionaryPath, new[] { "Lora", "LORA", "lora" });

        var sut = CreateSut();

        sut.GetAll().Should().ContainSingle();
    }

    #endregion

    #region Add / Contains round-trip

    [Fact]
    public void WhenWordAddedThenContainsReturnsTrue()
    {
        var sut = CreateSut();

        sut.Add("checkpoint");

        sut.Contains("checkpoint").Should().BeTrue();
        sut.GetAll().Should().ContainSingle().Which.Should().Be("checkpoint");
    }

    [Theory]
    [InlineData("CHECKPOINT")]
    [InlineData("checkpoint")]
    [InlineData("ChEcKpOiNt")]
    public void WhenCasingDiffersThenContainsStillMatches(string probe)
    {
        var sut = CreateSut();
        sut.Add("Checkpoint");

        sut.Contains(probe).Should().BeTrue();
    }

    [Fact]
    public void WhenWordNotPresentThenContainsReturnsFalse()
    {
        var sut = CreateSut();
        sut.Add("checkpoint");

        sut.Contains("safetensors").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenProbeIsBlankThenContainsReturnsFalse(string probe)
    {
        var sut = CreateSut();

        sut.Contains(probe).Should().BeFalse();
    }

    [Fact]
    public void WhenWordHasSurroundingWhitespaceThenItIsStoredTrimmed()
    {
        var sut = CreateSut();

        sut.Add("  lycoris  ");

        sut.GetAll().Should().ContainSingle().Which.Should().Be("lycoris");
        sut.Contains("lycoris").Should().BeTrue();
        // Contains does not trim its argument — the caller must pass a clean token.
        sut.Contains("  lycoris  ").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenAddingBlankWordThenNothingIsStoredOrPersisted(string word)
    {
        var sut = CreateSut();

        sut.Add(word);

        sut.GetAll().Should().BeEmpty();
        File.Exists(_dictionaryPath).Should().BeFalse();
    }

    [Fact]
    public void WhenSameWordAddedTwiceInDifferentCasingThenItIsDeduplicated()
    {
        var sut = CreateSut();

        sut.Add("Lora");
        sut.Add("LORA");
        sut.Add("lora");

        sut.GetAll().Should().ContainSingle().Which.Should().Be("Lora");
    }

    #endregion

    #region Remove

    [Fact]
    public void WhenWordRemovedThenContainsReturnsFalse()
    {
        var sut = CreateSut();
        sut.Add("checkpoint");

        sut.Remove("checkpoint");

        sut.Contains("checkpoint").Should().BeFalse();
        sut.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void WhenRemovingWithDifferentCasingThenTheWordIsStillRemoved()
    {
        var sut = CreateSut();
        sut.Add("Checkpoint");

        sut.Remove("CHECKPOINT");

        sut.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void WhenRemovingWithSurroundingWhitespaceThenTheWordIsStillRemoved()
    {
        var sut = CreateSut();
        sut.Add("checkpoint");

        sut.Remove("  checkpoint  ");

        sut.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void WhenRemovingAWordThatIsNotPresentThenNothingChanges()
    {
        var sut = CreateSut();
        sut.Add("checkpoint");

        sut.Remove("safetensors");

        sut.GetAll().Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenRemovingBlankWordThenNothingChanges(string word)
    {
        var sut = CreateSut();
        sut.Add("checkpoint");

        sut.Remove(word);

        sut.GetAll().Should().ContainSingle();
    }

    #endregion

    #region Persistence

    [Fact]
    public void WhenWordAddedThenTheFileIsWrittenImmediately()
    {
        var sut = CreateSut();

        sut.Add("checkpoint");

        File.Exists(_dictionaryPath).Should().BeTrue();
        File.ReadAllLines(_dictionaryPath).Should().Equal("checkpoint");
    }

    [Fact]
    public void WhenWordRemovedThenTheFileIsRewrittenImmediately()
    {
        var sut = CreateSut();
        sut.Add("alpha");
        sut.Add("beta");

        sut.Remove("alpha");

        File.ReadAllLines(_dictionaryPath).Should().Equal("beta");
    }

    [Fact]
    public void WhenSavingThenWordsArePersistedInCaseInsensitiveOrder()
    {
        var sut = CreateSut();

        sut.Add("zebra");
        sut.Add("apple");
        sut.Add("Mango");

        File.ReadAllLines(_dictionaryPath).Should().Equal("apple", "Mango", "zebra");
    }

    [Fact]
    public void WhenTargetDirectoryIsMissingThenSaveCreatesIt()
    {
        var nested = Path.Combine(_tempDir.FullName, "created", "on", "demand", "dict.txt");
        var sut = CreateSut(nested);

        sut.Add("checkpoint");

        File.Exists(nested).Should().BeTrue();
    }

    [Fact]
    public void WhenANewInstanceOpensTheSameFileThenTheWordsSurvive()
    {
        var first = CreateSut();
        first.Add("checkpoint");
        first.Add("safetensors");

        var second = CreateSut();

        second.Contains("CHECKPOINT").Should().BeTrue();
        second.GetAll().Should().BeEquivalentTo(new[] { "checkpoint", "safetensors" });
    }

    #endregion

    #region GetAll snapshot semantics

    [Fact]
    public void WhenGetAllTakenThenLaterAddsDoNotMutateTheSnapshot()
    {
        var sut = CreateSut();
        sut.Add("alpha");

        var snapshot = sut.GetAll();
        sut.Add("beta");

        snapshot.Should().ContainSingle();
        sut.GetAll().Should().HaveCount(2);
    }

    [Fact]
    public void WhenGetAllReturnedThenItMatchesCaseInsensitively()
    {
        var sut = CreateSut();
        sut.Add("Checkpoint");

        sut.GetAll().Contains("CHECKPOINT").Should().BeTrue();
    }

    #endregion
}
