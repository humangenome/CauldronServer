using Cauldron.Abstractions;
using CauldronServer.Services;

namespace CauldronServer.Tests;

public sealed class HostLaunchPrepTests
{
    private static HostLaunchContext Context(string userDir, string packageDir, string? installRoot = null, int port = 17777) =>
        new(
            InstallRoot: installRoot,
            ExecutablePath: Path.Combine(installRoot ?? @"C:\game", "Hercules", "Binaries", "Win64", "Hercules-Win64-Shipping.exe"),
            UserDir: userDir,
            ServerName: "Test Server",
            InstanceId: "test",
            PackageDirectory: packageDir,
            GameplayPort: port);

    private static string EngineIniPath(string userDir) =>
        Path.Combine(userDir, "Saved", "Config", "Windows", "Engine.ini");

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
    public void BuiltInPrepAppliesThePackageEngineIniTemplateWithThePort()
    {
        var pkg = TempDir();
        var userDir = TempDir();
        try
        {
            var templateDir = Path.Combine(pkg, "engine-ini");
            Directory.CreateDirectory(templateDir);
            File.WriteAllText(Path.Combine(templateDir, "Engine.host.ini"), "[Test]\r\nApplied=1\r\n[URL]\r\nPort=<GAMEPLAY_PORT>\r\n");

            new HostPackageLaunchPrep().Prepare(Context(userDir, pkg, port: 18077), _ => { });

            var written = File.ReadAllText(EngineIniPath(userDir));
            Assert.Contains("Applied=1", written, StringComparison.Ordinal);
            Assert.Contains("Port=18077", written, StringComparison.Ordinal);
            Assert.DoesNotContain("<GAMEPLAY_PORT>", written, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(userDir, "Saved", "PersistentDownloadDir", "EOSCache")));
        }
        finally
        {
            Directory.Delete(pkg, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void BuiltInPrepFindsTheTemplateOneFolderUpTheWayTheReleaseZipShipsIt()
    {
        // Release zip: <root>\CauldronServer\CauldronServer.exe and <root>\engine-ini\Engine.host.ini.
        var root = TempDir();
        var userDir = TempDir();
        try
        {
            var pkg = Path.Combine(root, "CauldronServer");
            Directory.CreateDirectory(pkg);
            Directory.CreateDirectory(Path.Combine(root, "engine-ini"));
            File.WriteAllText(Path.Combine(root, "engine-ini", "Engine.host.ini"), "[URL]\r\nPort=<GAMEPLAY_PORT>\r\nFromParent=1\r\n");

            new HostPackageLaunchPrep().Prepare(Context(userDir, pkg + Path.DirectorySeparatorChar, port: 17777), _ => { });

            var written = File.ReadAllText(EngineIniPath(userDir));
            Assert.Contains("FromParent=1", written, StringComparison.Ordinal);
            Assert.Contains("Port=17777", written, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void BuiltInPrepWritesItsOwnEngineIniWhenThePackageShipsNoTemplate()
    {
        var pkg = TempDir();
        var userDir = TempDir();
        try
        {
            var logged = new List<string>();
            new HostPackageLaunchPrep().Prepare(Context(userDir, pkg, port: 17877), logged.Add);

            var written = File.ReadAllText(EngineIniPath(userDir));
            Assert.Equal(HostPackageLaunchPrep.BuiltInEngineIni(17877), written);
            Assert.Contains("DefaultPlatformService=Null", written, StringComparison.Ordinal);
            Assert.Contains("DriverClassName=\"/Script/OnlineSubsystemUtils.IpNetDriver\"", written, StringComparison.Ordinal);
            Assert.Contains("NetworkAuthenticationMode=Off", written, StringComparison.Ordinal);
            Assert.Contains("Port=17877", written, StringComparison.Ordinal);
            Assert.Contains("FixedFrameRate=60.000000", written, StringComparison.Ordinal);
            Assert.Contains(logged, l => l.Contains("not found", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(pkg, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void BuiltInPrepWritesNoEngineIniWithoutAValidPort(int port)
    {
        var pkg = TempDir();
        var userDir = TempDir();
        try
        {
            var logged = new List<string>();
            new HostPackageLaunchPrep().Prepare(Context(userDir, pkg, port: port), logged.Add);

            Assert.False(File.Exists(EngineIniPath(userDir)));
            Assert.Contains(logged, l => l.Contains("not a valid port", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(pkg, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void BuiltInPrepPutsSteamAppIdBesideTheGameExecutable()
    {
        var pkg = TempDir();
        var userDir = TempDir();
        var game = TempDir();
        try
        {
            var win64 = Path.Combine(game, "Hercules", "Binaries", "Win64");
            Directory.CreateDirectory(win64);

            new HostPackageLaunchPrep().Prepare(Context(userDir, pkg, installRoot: game), _ => { });

            Assert.Equal("2679100", File.ReadAllText(Path.Combine(win64, "steam_appid.txt")).Trim());
        }
        finally
        {
            Directory.Delete(pkg, recursive: true);
            Directory.Delete(userDir, recursive: true);
            Directory.Delete(game, recursive: true);
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
    public void BuiltInPrepRefusesAGameFolderInsideAStoreLibrary()
    {
        var pkg = TempDir();
        var userDir = TempDir();
        try
        {
            var logged = new List<string>();
            new HostPackageLaunchPrep().Prepare(
                Context(userDir, pkg, installRoot: @"D:\SteamLibrary\steamapps\common\Witchspire"), logged.Add);

            Assert.False(File.Exists(EngineIniPath(userDir)));
            Assert.Contains(logged, l => l.Contains("refused", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(pkg, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
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
