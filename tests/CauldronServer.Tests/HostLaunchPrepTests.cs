using Cauldron.Abstractions;
using CauldronServer.Services;

namespace CauldronServer.Tests;

public sealed class HostLaunchPrepTests
{
    private static HostLaunchContext Context(string userDir, string packageDir, string? installRoot = null) =>
        new(
            InstallRoot: installRoot,
            ExecutablePath: Path.Combine(installRoot ?? @"C:\game", "Hercules", "Binaries", "Win64", "Hercules-Win64-Shipping.exe"),
            UserDir: userDir,
            ServerName: "Test Server",
            InstanceId: "test",
            PackageDirectory: packageDir);

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cauldron-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void LoaderFallsBackToBuiltInWhenNoPluginPresent()
    {
        var pkg = TempDir();
        try
        {
            var prep = HostLaunchPrepLoader.Load(pkg, _ => { });

            Assert.IsType<HostPackageLaunchPrep>(prep);
        }
        finally { Directory.Delete(pkg, recursive: true); }
    }

    [Fact]
    public void LoaderIgnoresAnUnloadablePluginAndStillReturnsAPrep()
    {
        var pkg = TempDir();
        try
        {
            // Not a managed assembly — Assembly.LoadFrom throws; the loader must
            // swallow it rather than take the supervisor down.
            File.WriteAllText(Path.Combine(pkg, "Cauldron.HostPrep.Broken.dll"), "not an assembly");

            var logged = new List<string>();
            var prep = HostLaunchPrepLoader.Load(pkg, logged.Add);

            Assert.IsType<HostPackageLaunchPrep>(prep);
            Assert.Contains(logged, l => l.Contains("Cauldron.HostPrep.Broken.dll", StringComparison.Ordinal));
        }
        finally { Directory.Delete(pkg, recursive: true); }
    }

    [Fact]
    public void LoaderProbesThePackageRootAndTheHostprepSubdirectory()
    {
        var pkg = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(pkg, HostLaunchPrepLoader.PluginSubdirectory));
            File.WriteAllText(Path.Combine(pkg, "Cauldron.HostPrep.dll"), "");
            File.WriteAllText(Path.Combine(pkg, HostLaunchPrepLoader.PluginSubdirectory, "Cauldron.HostPrep.Extra.dll"), "");
            File.WriteAllText(Path.Combine(pkg, "SomethingElse.dll"), "");

            var found = HostLaunchPrepLoader.EnumeratePluginCandidates(pkg).Select(Path.GetFileName).ToList();

            Assert.Equal(["Cauldron.HostPrep.dll", "Cauldron.HostPrep.Extra.dll"], found);
        }
        finally { Directory.Delete(pkg, recursive: true); }
    }

    [Fact]
    public void BuiltInPrepAppliesThePackageEngineIniTemplate()
    {
        var pkg = TempDir();
        var userDir = TempDir();
        try
        {
            var templateDir = Path.Combine(pkg, "engine-ini");
            Directory.CreateDirectory(templateDir);
            File.WriteAllText(Path.Combine(templateDir, "Engine.host.ini"), "[Test]\r\nApplied=1\r\n");

            new HostPackageLaunchPrep().Prepare(Context(userDir, pkg), _ => { });

            var written = Path.Combine(userDir, "Saved", "Config", "Windows", "Engine.ini");
            Assert.True(File.Exists(written));
            Assert.Contains("Applied=1", File.ReadAllText(written), StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(userDir, "Saved", "PersistentDownloadDir", "EOSCache")));
        }
        finally
        {
            Directory.Delete(pkg, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void BuiltInPrepIsSilentlyInertWhenThePackageShipsNoTemplate()
    {
        var pkg = TempDir();
        var userDir = TempDir();
        try
        {
            var logged = new List<string>();
            new HostPackageLaunchPrep().Prepare(Context(userDir, pkg), logged.Add);

            Assert.False(File.Exists(Path.Combine(userDir, "Saved", "Config", "Windows", "Engine.ini")));
            Assert.Contains(logged, l => l.Contains("not found", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(pkg, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Witchspire")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Witchspire")]
    [InlineData(@"C:\Program Files\Epic Games\Witchspire")]
    [InlineData(@"C:\Program Files\WindowsApps\Witchspire")]
    public void VanillaGuardRefusesStoreInstallPaths(string path)
    {
        Assert.True(VanillaInstallGuard.LooksLikeVanillaInstallPath(path));
    }

    [Theory]
    [InlineData(@"C:\Cauldron\UserDir")]
    [InlineData("")]
    [InlineData(null)]
    public void VanillaGuardAllowsDedicatedUserDirs(string? path)
    {
        Assert.False(VanillaInstallGuard.LooksLikeVanillaInstallPath(path));
    }

    [Fact]
    public void BuiltInPrepRefusesToWriteIntoAVanillaInstall()
    {
        var pkg = TempDir();
        try
        {
            var templateDir = Path.Combine(pkg, "engine-ini");
            Directory.CreateDirectory(templateDir);
            File.WriteAllText(Path.Combine(templateDir, "Engine.host.ini"), "[Test]\r\n");

            var logged = new List<string>();
            new HostPackageLaunchPrep().Prepare(
                Context(@"C:\SteamLibrary\steamapps\common\Witchspire", pkg), logged.Add);

            Assert.Contains(logged, l => l.Contains("refused", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(pkg, recursive: true); }
    }
}
