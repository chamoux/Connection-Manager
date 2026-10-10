using ConnectionManager.Core.Reports;

namespace ConnectionManager.Core.Tests;

public class ReportExclusionsTests
{
    private const string GameExe = @"D:\Games\Witcher\witcher3.exe";
    private const string ServiceExe = @"C:\Program Files\ConnectionManager\ConnectionManager.Service.exe";

    private static readonly Func<string, bool> Excluded = ReportExclusions.Create(
        gameExes: [GameExe],
        steamFolders: [@"C:\Program Files (x86)\Steam", @"E:\SteamLibrary\", null, "relative"],
        isProtected: p => p.EndsWith(@"\discord.exe", StringComparison.OrdinalIgnoreCase),
        serviceExe: ServiceExe);

    [Theory]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Elden Ring\Game\eldenring.exe")] // under a library
    [InlineData(@"C:\Program Files (x86)\Steam\steamwebhelper.exe")]                 // under the Steam folder
    [InlineData(@"d:\games\witcher\WITCHER3.EXE")]                                   // the game, any case
    [InlineData(@"D:\Games\Witcher\..\Witcher\witcher3.exe")]                        // the game, not normalised
    [InlineData(@"C:\Users\me\AppData\Local\Discord\app-1.0\discord.exe")]           // protected
    [InlineData(ServiceExe)]
    [InlineData("System")]
    [InlineData("Other")]
    [InlineData("Unknown")]
    [InlineData("chrome.exe")]                                                       // folder unknown: nothing to block
    [InlineData("")]
    public void Excludes(string path) => Assert.True(Excluded(path));

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"E:\SteamLibraryOld\uploader.exe")] // a sibling folder whose name starts the same
    [InlineData(@"D:\Games\Witcher\launcher.exe")]   // only the game exe itself is excluded
    public void Keeps_normal_apps(string path) => Assert.False(Excluded(path));
}
