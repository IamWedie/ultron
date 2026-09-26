using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

public class PathPolicyTests
{
    private static UserProfilePathPolicy Policy()
    {
        var root = Path.Combine(Path.GetTempPath(), "ultron-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new UserProfilePathPolicy(root);
    }

    [Fact]
    public void AcceptsAPathInsideTheProfile()
    {
        var policy = Policy();
        var inside = Path.Combine(policy.Root, "notes", "todo.txt");

        var verdict = policy.Validate(inside);

        Assert.True(verdict.Allowed);
        Assert.Equal(Path.GetFullPath(inside), verdict.FullPath);
    }

    [Fact]
    public void AcceptsANestedPathThatDoesNotExistYet()
    {
        var policy = Policy();

        Assert.True(policy.Validate(Path.Combine(policy.Root, "new", "deep", "file.txt")).Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RejectsAnEmptyPath(string? raw)
    {
        var verdict = Policy().Validate(raw);

        Assert.False(verdict.Allowed);
        Assert.Contains("path is required", verdict.Error);
    }

    [Fact]
    public void RejectsAPathOutsideTheProfile()
    {
        var policy = Policy();
        var temp = Path.GetTempPath();

        var verdict = policy.Validate(Path.Combine(temp, "elsewhere.txt"));

        Assert.False(verdict.Allowed);
        Assert.Contains("user profile", verdict.Error);
    }

    [Fact]
    public void AllowsTheProfileRootItself()
    {
        // Listing your home folder is legitimate; the scope check governs reach,
        // not destructiveness. Refusing the root belongs to the file service.
        var policy = Policy();

        Assert.True(policy.Validate(policy.Root).Allowed);
        Assert.True(policy.Validate(policy.Root.TrimEnd(Path.DirectorySeparatorChar)).Allowed);
    }

    [Fact]
    public void RejectsASiblingFolderThatMerelySharesThePrefix()
    {
        // "C:\Users\me-backup" must not pass as "inside C:\Users\me".
        var policy = Policy();
        var sibling = policy.Root.TrimEnd(Path.DirectorySeparatorChar) + "-backup";

        Assert.False(policy.Validate(sibling).Allowed);
    }

    [Theory]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("//server/share/file.txt")]
    [InlineData(@"\\?\C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void RejectsUncAndDevicePaths(string raw)
    {
        var verdict = Policy().Validate(raw);

        Assert.False(verdict.Allowed);
        Assert.Contains("UNC and device", verdict.Error);
    }

    [Fact]
    public void RejectsTraversalThatEscapesTheProfile()
    {
        // The dangerous case: the raw string starts with the allowed prefix, so
        // only checking the string would let this through.
        var policy = Policy();
        var sneaky = Path.Combine(policy.Root, "..", "..", "Windows", "System32", "config", "SAM");

        var verdict = policy.Validate(sneaky);

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void RejectsTraversalEvenWhenThePrefixStillMatches()
    {
        var policy = Policy();
        // Resolves back inside the root but leaves the root first: allowed only
        // because the canonical path is inside, which is the correct outcome.
        var verdict = policy.Validate(Path.Combine(policy.Root, "sub", "..", "file.txt"));

        Assert.True(verdict.Allowed);
        Assert.Equal(Path.Combine(policy.Root, "file.txt"), verdict.FullPath);
    }

    [Fact]
    public void ComparisonIsCaseInsensitive()
    {
        var policy = Policy();
        var path = policy.Root.TrimEnd(Path.DirectorySeparatorChar) + "\\MiXeD\\File.TXT";

        Assert.True(policy.Validate(path).Allowed);
    }

    [Theory]
    [InlineData("con:*file.txt")]
    [InlineData("what?.txt")]
    [InlineData("a|b.txt")]
    [InlineData("a<b.txt")]
    [InlineData("a\"b.txt")]
    public void RejectsWildcardsAndOtherForbiddenCharacters(string name)
    {
        var policy = Policy();

        var verdict = policy.Validate(Path.Combine(policy.Root, name));

        Assert.False(verdict.Allowed);
        Assert.Contains("not allowed", verdict.Error);
    }

    [Fact]
    public void RejectsControlCharacters()
    {
        var policy = Policy();

        var verdict = policy.Validate(Path.Combine(policy.Root, "bad\u0000name.txt"));

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void AcceptsOrdinaryPathsWithSpaces()
    {
        var policy = Policy();

        var verdict = policy.Validate(Path.Combine(policy.Root, "My Documents", "notes (2).txt"));

        Assert.True(verdict.Allowed);
    }
}
