using CauldronServer.Services;
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
}
