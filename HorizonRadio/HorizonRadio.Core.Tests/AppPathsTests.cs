using HorizonRadio.Core.IO;
using Xunit;

namespace HorizonRadio.Core.Tests;

public sealed class AppPathsTests
{
    // The arguments, separated by '|' (one can hold a space).
    private static string[] Args(string commandLine) =>
        commandLine.Length == 0 ? [] : commandLine.Split('|');

    [Theory]
    [InlineData("")]
    [InlineData("--other|value")]
    public void No_profile_argument_is_the_default_profile(string commandLine)
    {
        Assert.True(AppPaths.TryParseProfile(Args(commandLine), out var profile, out var error));
        Assert.Null(profile);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--profile|bob")]
    [InlineData("--profile=bob")]
    [InlineData("--verbose|--profile|bob|--other")]
    public void Reads_the_profile_in_either_form(string commandLine)
    {
        Assert.True(AppPaths.TryParseProfile(Args(commandLine), out var profile, out _));
        Assert.Equal("bob", profile);
    }

    [Theory]
    [InlineData("--profile")]
    [InlineData("--profile=")]
    [InlineData("--profile|../escape")]
    [InlineData("--profile|a b")]
    [InlineData(@"--profile|C:\elsewhere")]
    public void Rejects_a_missing_or_unsafe_name(string commandLine)
    {
        Assert.False(AppPaths.TryParseProfile(Args(commandLine), out var profile, out var error));
        Assert.Null(profile);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("bob", true)]
    [InlineData("test-2_b", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("con.txt", false)]
    [InlineData("con", false)]
    [InlineData("NUL", false)]
    [InlineData("com1", false)]
    [InlineData("lpt9", false)]
    [InlineData("com", true)]
    [InlineData("com10", true)]
    [InlineData("ünïcode", false)]
    public void Profile_names_are_plain_folder_names(string name, bool valid) =>
        Assert.Equal(valid, AppPaths.IsValidProfileName(name));

    [Fact]
    public void Profile_names_have_a_length_limit()
    {
        Assert.True(AppPaths.IsValidProfileName(new string('a', AppPaths.MaxProfileLength)));
        Assert.False(AppPaths.IsValidProfileName(new string('a', AppPaths.MaxProfileLength + 1)));
    }

    [Fact]
    public void A_profile_lives_in_its_own_folder_under_the_shared_one()
    {
        Assert.Equal(AppPaths.SharedRoot, AppPaths.RootFor(null));
        Assert.Equal(Path.Combine(AppPaths.SharedRoot, "profiles", "bob"), AppPaths.RootFor("bob"));
    }

    [Fact]
    public void Folders_put_everything_under_their_root_and_mark_names_with_the_profile()
    {
        var root = Path.Combine(Path.GetTempPath(), "hzr-root");
        var folders = new AppFolders(root, "bob");

        Assert.Equal(Path.Combine(root, "cache", "audio"), folders.Data("cache", "audio"));
        Assert.Equal("Horizon Radio [bob]", folders.Label("Horizon Radio"));
        Assert.Equal("Horizon Radio", new AppFolders(root).Label("Horizon Radio"));
    }
}
