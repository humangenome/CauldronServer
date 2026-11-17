using CauldronServer.Configuration;
using CauldronServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CauldronServer.Tests;

public class HostRosterLogServiceTests
{
    [Fact]
    public void NonRosterLine_ReturnsNull()
    {
        Assert.Null(HostRosterLogService.TryParseRoster(
            "[2026.06.22-17.40.11:709][471]Cauldron: CAULDRON_HOST: tick=270 oss=true listen=true"));
    }

    [Fact]
    public void EmptyRoster_ReturnsZeroAndNoNames()
    {
        var r = HostRosterLogService.TryParseRoster(
            "[2026.06.22-17.40.11:709][471]Cauldron: CAULDRON_HOST: roster count=0 players=");
        Assert.NotNull(r);
        Assert.Equal(0, r!.Value.Count);
        Assert.Empty(r.Value.Names);
    }

    [Fact]
    public void SinglePlayer_ParsesCountAndName()
    {
        var r = HostRosterLogService.TryParseRoster(
            "[2026.06.22-17.40.11:709][471]Cauldron: CAULDRON_HOST: roster count=1 players=RyanPC");
        Assert.NotNull(r);
        Assert.Equal(1, r!.Value.Count);
        Assert.Equal(new[] { "RyanPC" }, r.Value.Names);
    }

    [Fact]
    public void MultiplePlayers_SplitOnTab()
    {
        var r = HostRosterLogService.TryParseRoster(
            "[x][0]Cauldron: CAULDRON_HOST: roster count=3 players=Alice\tBob\tCarol");
        Assert.NotNull(r);
        Assert.Equal(3, r!.Value.Count);
        Assert.Equal(new[] { "Alice", "Bob", "Carol" }, r.Value.Names);
    }

    [Fact]
    public void NameWithSpaces_Preserved()
    {
        var r = HostRosterLogService.TryParseRoster(
            "[x][0]Cauldron: CAULDRON_HOST: roster count=1 players=My Witch Name");
        Assert.NotNull(r);
        Assert.Equal(1, r!.Value.Count);
        Assert.Equal(new[] { "My Witch Name" }, r.Value.Names);
    }
    // The game writes its build banner near the top of ws-ue.log at every start. Health's cauldron_build
    // and the A2S version read "unknown" on every hosted server until 0.1.27, because the only reader of
    // the banner watched Saved\Logs\Witchspire.log, a file the game never writes.
    [Theory]
    [InlineData("[2026.11.11-21.50.03:123][  0]LogInit: Build: Hercules-CL-32175", "CL-32175")]
    [InlineData("LogInit: Build: Hercules-CL-32175", "CL-32175")]
    [InlineData("LogInit: Build: Witchspire-CL-31500", "Witchspire-CL-31500")]
    public void BuildBanner_IsReadAsTheShortBuild(string line, string expected)
    {
        Assert.Equal(expected, CauldronVersionInfo.TryParseBuildBanner(line));
    }

    [Theory]
    [InlineData("LogInit: Net CL: 32175")]
    [InlineData("LogInit: Engine Version: 5.7.4-32175+++hercules+dev-playtest")]
    [InlineData("LogInit: Branch Name: ++hercules+dev-playtest")]
    [InlineData("[x][0]Cauldron: CAULDRON_HOST: roster count=0 players=")]
    [InlineData("[x][0]Cauldron: CAULDRON_CHAT: Build: not a banner")]
    [InlineData("")]
    public void OtherLines_AreNotABuildBanner(string line)
    {
        Assert.Null(CauldronVersionInfo.TryParseBuildBanner(line));
    }

    [Fact]
    public async Task Tail_of_ws_ue_log_sets_the_game_build_and_the_roster()
    {
        var root = Path.Combine(Path.GetTempPath(), "cauldron-roster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var logPath = Path.Combine(root, "ws-ue.log");
        await File.WriteAllTextAsync(logPath, string.Join("\n", new[]
        {
            "Log file open, 11/12/26 19:25:44",
            "LogInit: Build: Hercules-CL-99123",
            "LogInit: Net CL: 99123",
            "[2026.11.12-19.26.01:000][100]Cauldron: CAULDRON_HOST: roster count=1 players=Witch One",
            ""
        }));

        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var service = new HostRosterLogService(
            NullLogger<HostRosterLogService>.Instance,
            Options.Create(new CauldronServerOptions { GameUserDir = root }),
            state);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while ((CauldronVersionInfo.CauldronBuild != "CL-99123" || state.EffectivePlayerCount != 1)
                   && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.Equal("CL-99123", CauldronVersionInfo.CauldronBuild);
            Assert.Equal(1, state.EffectivePlayerCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }
}
