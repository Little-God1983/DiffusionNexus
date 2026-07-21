using DiffusionNexus.Service.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services.Updates;

/// <summary>
/// Pure-function tests for <see cref="GitBranchParser"/>, exercising the detached-HEAD
/// vs attached-branch decision against realistic captured <c>git</c> stdout. These cover
/// the parsing that <see cref="ComfyUIUpdateService"/> / <see cref="AIToolkitUpdateService"/>
/// used to do inline while also invoking git — split out per issue #439.
/// </summary>
public class GitBranchParserTests
{
    #region ParseAttachedBranch (git rev-parse --abbrev-ref HEAD)

    [Theory]
    [InlineData("main\n", "main")]
    [InlineData("master\n", "master")]
    [InlineData("develop", "develop")]
    [InlineData("feature/foo-bar\r\n", "feature/foo-bar")]
    [InlineData("  release/1.2  ", "release/1.2")]
    public void WhenOnNamedBranchThenReturnsBranchName(string gitOutput, string expected)
    {
        var result = GitBranchParser.ParseAttachedBranch(success: true, gitOutput);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("HEAD\n")]       // detached HEAD — git prints the literal "HEAD"
    [InlineData("HEAD")]
    [InlineData("")]             // no output
    [InlineData("   \n")]        // whitespace only
    public void WhenDetachedOrEmptyThenReturnsNull(string gitOutput)
    {
        var result = GitBranchParser.ParseAttachedBranch(success: true, gitOutput);

        result.Should().BeNull();
    }

    [Fact]
    public void WhenCommandFailedThenReturnsNull()
    {
        var result = GitBranchParser.ParseAttachedBranch(success: false, "main");

        result.Should().BeNull();
    }

    #endregion

    #region ParseSymbolicRefBranch (git symbolic-ref refs/remotes/origin/HEAD --short)

    [Theory]
    [InlineData("origin/main\n", "main")]
    [InlineData("origin/master\r\n", "master")]
    [InlineData("origin/develop", "develop")]
    public void WhenSymbolicRefResolvesThenStripsOriginPrefix(string gitOutput, string expected)
    {
        var result = GitBranchParser.ParseSymbolicRefBranch(success: true, gitOutput);

        result.Should().Be(expected);
    }

    [Fact]
    public void WhenSymbolicRefCommandFailedThenReturnsNull()
    {
        var result = GitBranchParser.ParseSymbolicRefBranch(success: false, "origin/main");

        result.Should().BeNull();
    }

    [Fact]
    public void WhenSymbolicRefEmptyThenReturnsNull()
    {
        var result = GitBranchParser.ParseSymbolicRefBranch(success: true, "   \n");

        result.Should().BeNull();
    }

    #endregion
}
