using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.Tests;

public class GameMatcherTests
{
    private const string Exe = "javaw.exe";
    private const string Path = @"C:\Games\Java\javaw.exe";

    private static GameProfile Game(string name, string? title = null, bool matchPath = false, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ExecutableName = Exe,
        DisplayName = name,
        ExecutablePath = Path,
        MatchExecutablePath = matchPath,
        WindowTitleContains = title,
    };

    private static GameProfile? Winner(string windowTitle, params GameProfile[] games) =>
        GameMatcher.FindBest(games, Exe, Path, () => windowTitle);

    [Fact]
    public void ExeFile_BeatsTitle_BeatsExeNameAlone()
    {
        var any = Game("Any");
        var titled = Game("Titled", title: "Minecraft");
        var path = Game("Path", matchPath: true);

        Assert.Same(path, Winner("Minecraft", any, titled, path));
        Assert.Same(titled, Winner("Minecraft", any, titled));
        Assert.Same(any, Winner("Something else", any, titled));
    }

    [Fact]
    public void EquallySpecific_LongerTitleFilterWins_InAnyOrder()
    {
        var shorter = Game("Alpha", title: "Modpack");
        var longer = Game("Beta", title: "Minecraft");

        Assert.Same(longer, Winner("Minecraft Modpack", shorter, longer));
        Assert.Same(longer, Winner("Minecraft Modpack", longer, shorter));
    }

    [Fact]
    public void SameFilterLength_TheWinnerDoesNotDependOnListOrderOrName()
    {
        var low = Game("Zulu", title: "Alpha", id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var high = Game("Alpha", title: "Bravo", id: Guid.Parse("00000000-0000-0000-0000-000000000002"));

        Assert.Same(low, Winner("Alpha Bravo", low, high));
        Assert.Same(low, Winner("Alpha Bravo", high, low));

        // A rename moves the game in the A–Z list, but doesn't change the winner
        var renamed = low with { DisplayName = "Aardvark" };
        Assert.Same(renamed, Winner("Alpha Bravo", high, renamed));
    }

    [Fact]
    public void TitleIsOnlyRead_WhenAGameHasATitleFilter()
    {
        var read = 0;
        var result = GameMatcher.FindBest([Game("Any")], Exe, Path, () => { read++; return "x"; });

        Assert.NotNull(result);
        Assert.Equal(0, read);
    }
}
