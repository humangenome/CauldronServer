using CauldronServer.Services;
using CauldronServer.Configuration;

namespace CauldronServer.Tests;

public sealed class CauldronProcessSupervisorServiceTests
{
    [Fact]
    public void BuildHostTravelUrlOpensStarterIslandListen()
    {
        var url = CauldronProcessSupervisorService.BuildHostTravelUrl();

        Assert.Equal("L_StarterIsland?listen", url);
    }

    [Fact]
    public void BuildHostTravelOptionsIsListenOnly()
    {
        var options = CauldronProcessSupervisorService.BuildHostTravelOptions("save slot 1");

        Assert.Equal("?listen", options);
    }

    [Fact]
    public void ResolveExecutablePathDetectsProjectDirLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "cauldron-tests", Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(root, "Witchspire", "Binaries", "Win64", "Hercules-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");

        try
        {
            var resolved = CauldronProcessSupervisorService.ResolveExecutablePath(new CauldronServerOptions
            {
                GameInstallRoot = root,
            });

            Assert.Equal(exe, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveExecutablePathPrefersExplicitExecutablePath()
    {
        var explicitPath = Path.Combine(Path.GetTempPath(), "Hercules-Win64-Shipping.exe");

        var resolved = CauldronProcessSupervisorService.ResolveExecutablePath(new CauldronServerOptions
        {
            GameInstallRoot = @"C:\Cauldron\game",
            GameExecutablePath = explicitPath,
        });

        Assert.Equal(Path.GetFullPath(explicitPath), resolved);
    }
}
