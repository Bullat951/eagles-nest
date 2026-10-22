using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class DcsLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dcb-locator-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string FakeDcs(string rel, string bin = "bin", params string[] terrains)
    {
        var dir = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.Combine(dir, bin));
        File.WriteAllText(Path.Combine(dir, bin, "DCS.exe"), "");
        foreach (var t in terrains) Directory.CreateDirectory(Path.Combine(dir, "Mods", "terrains", t));
        Directory.CreateDirectory(Path.Combine(dir, "Mods", "terrains"));
        return dir;
    }

    [Fact]
    public void RecognisesARealInstallOnly()
    {
        var real = FakeDcs("A", "bin-mt", "Syria", "Caucasus");
        var noTerrains = Path.Combine(_root, "B");
        Directory.CreateDirectory(Path.Combine(noTerrains, "bin"));
        File.WriteAllText(Path.Combine(noTerrains, "bin", "DCS.exe"), "");
        Assert.True(DcsLocator.IsDcsFolder(real));
        Assert.False(DcsLocator.IsDcsFolder(noTerrains));
        Assert.False(DcsLocator.IsDcsFolder(Path.Combine(_root, "missing")));
        Assert.Equal(["Caucasus", "Syria"], DcsLocator.Terrains(real));
    }

    [Fact]
    public void ReadsSteamLibraryFolders()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path"  "C:\\Program Files (x86)\\Steam"  "apps" { "228980" "1" } }
                "1" { "path"  "D:\\SteamLibrary"  "apps" { "223750" "2" } }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], DcsLocator.SteamLibraries(vdf));
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\DCSWorld", DcsLocator.InSteamLibrary(@"D:\SteamLibrary"));
    }

    [Fact]
    public void VerifyKeepsRealInstallsOnceInOrder()
    {
        var steam = FakeDcs(@"SteamLibrary\steamapps\common\DCSWorld", "bin", "Syria");
        var standalone = FakeDcs("DCS World", "bin", "Caucasus");
        var found = DcsLocator.Verify([
            (Path.Combine(_root, "nothing here"), "registry"),
            (standalone, "registry"),
            (steam, "Steam"),
            (standalone + "\\", "drive search"),  // the same folder again
            .. DcsLocator.UsualFolders(_root).Select(p => (p, "drive search")),
        ]);
        Assert.Equal(2, found.Count);
        Assert.Equal((standalone, "registry"), (found[0].Path, found[0].Source));
        Assert.Equal((steam, "Steam"), (found[1].Path, found[1].Source));
        Assert.Equal(["Syria"], found[1].Terrains);
    }
}
