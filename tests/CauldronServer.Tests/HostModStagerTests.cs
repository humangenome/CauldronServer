using CauldronServer.Services;

namespace CauldronServer.Tests;

public sealed class HostModStagerTests
{
    private const string HostModSource =
        "// host mod\nconst int HOST_MODE = 2;\nconst int HOST_PORT = 17777;\nconst FString HOST_SAVE_SLOT = \"savegame_0\";\n";

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cauldron-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // A package dir with the mod where the release zip puts it, and a game project dir.
    private static (string Pkg, string Project, string Root) Layout(string hostMod = HostModSource)
    {
        var root = TempDir();
        var pkg = Path.Combine(root, "CauldronServer");
        var modDir = Path.Combine(pkg, "angelscript-mods", "cauldron_host");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "CauldronHost.as"), hostMod);
        File.WriteAllText(Path.Combine(modDir, "CauldronClient.as"), "// client mod\n");
        var project = Path.Combine(root, "game", "Hercules");
        Directory.CreateDirectory(project);
        return (pkg, project, root);
    }

    [Fact]
    public void StagesEveryShippedScriptAndSetsTheGameplayPort()
    {
        var (pkg, project, root) = Layout();
        try
        {
            var logged = new List<string>();
            Assert.True(HostModStager.Stage(pkg, project, 18177, logged.Add));

            var dest = HostModStager.ModDestination(project);
            var host = File.ReadAllText(Path.Combine(dest, "CauldronHost.as"));
            Assert.Contains("const int HOST_PORT = 18177;", host, StringComparison.Ordinal);
            Assert.DoesNotContain("17777", host, StringComparison.Ordinal);
            Assert.Contains("HOST_SAVE_SLOT", host, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dest, "CauldronClient.as")));
            Assert.Equal(Path.Combine(project, "Script", "Mods", "Cauldron"), dest);
            Assert.Empty(Directory.GetFiles(dest, "*.cauldron-tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RestagingWithANewPortRewritesTheStagedCopy()
    {
        var (pkg, project, root) = Layout();
        try
        {
            Assert.True(HostModStager.Stage(pkg, project, 18177, _ => { }));
            Assert.True(HostModStager.Stage(pkg, project, 19077, _ => { }));

            var host = File.ReadAllText(Path.Combine(HostModStager.ModDestination(project), "CauldronHost.as"));
            Assert.Equal(19077, HostModStager.ReadHostPort(host));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RemovesScriptsThatAreNotInThePackageAndLeavesOtherFiles()
    {
        var (pkg, project, root) = Layout();
        try
        {
            var dest = HostModStager.ModDestination(project);
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(dest, "OldLeftover.as"), "broken script");
            File.WriteAllText(Path.Combine(dest, "notes.json"), "{}");

            var logged = new List<string>();
            Assert.True(HostModStager.Stage(pkg, project, 17777, logged.Add));

            Assert.False(File.Exists(Path.Combine(dest, "OldLeftover.as")));
            Assert.True(File.Exists(Path.Combine(dest, "notes.json")));
            Assert.Contains(logged, l => l.Contains("OldLeftover.as", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LeavesOtherModFoldersAlone()
    {
        var (pkg, project, root) = Layout();
        try
        {
            var other = Path.Combine(project, "Script", "Mods", "SomeoneElsesMod");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "Theirs.as"), "// theirs");

            Assert.True(HostModStager.Stage(pkg, project, 17777, _ => { }));

            Assert.True(File.Exists(Path.Combine(other, "Theirs.as")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Witchspire\Hercules")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Witchspire\Hercules")]
    public void RefusesAGameInsideAStoreLibrary(string project)
    {
        var (pkg, _, root) = Layout();
        try
        {
            var logged = new List<string>();
            Assert.False(HostModStager.Stage(pkg, project, 17777, logged.Add));
            Assert.Contains(logged, l => l.Contains("NOT staged", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FailsWhenThePackageHasNoHostMod()
    {
        var root = TempDir();
        try
        {
            var project = Path.Combine(root, "game", "Hercules");
            Directory.CreateDirectory(project);
            var logged = new List<string>();

            Assert.False(HostModStager.Stage(Path.Combine(root, "CauldronServer"), project, 17777, logged.Add));
            Assert.Contains(logged, l => l.Contains("not found", StringComparison.Ordinal));
            Assert.False(Directory.Exists(HostModStager.ModDestination(project)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FailsWhenTheHostModHasNoPortLine()
    {
        var (pkg, project, root) = Layout("// a host mod with the port line edited out\n");
        try
        {
            var logged = new List<string>();
            Assert.False(HostModStager.Stage(pkg, project, 17777, logged.Add));
            Assert.Contains(logged, l => l.Contains("HOST_PORT", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void FailsWithoutAValidPort(int port)
    {
        var (pkg, project, root) = Layout();
        try
        {
            Assert.False(HostModStager.Stage(pkg, project, port, _ => { }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProjectDirIsThreeLevelsAboveTheExecutable()
    {
        var exe = Path.Combine("C:", "Cauldron", "game", "Hercules", "Binaries", "Win64", "Hercules-Win64-Shipping.exe");

        Assert.Equal(Path.Combine("C:", "Cauldron", "game", "Hercules"), CauldronProcessSupervisorService.ProjectDirFromExe(exe));
    }
}
