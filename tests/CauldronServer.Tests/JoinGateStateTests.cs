using CauldronServer.Services;

namespace CauldronServer.Tests;

// The health report's join_gate: a passworded server whose world is up but whose gate never said "host on" is open,
// and must say so (the hosting side's watcher reads it). A server without a password leaves the key out.
public sealed class JoinGateStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoPasswordMeansNoJoinGateField()
    {
        var gate = new JoinGateState();
        gate.Observe("[2026.11.04-18.00.00:000][  0]Cauldron: CAULDRON_HOST: roster count=0 players=", T0);
        Assert.Null(gate.Status(passwordRequired: false, T0.AddMinutes(10)));
    }

    [Fact]
    public void GateLineMakesItOn()
    {
        var gate = new JoinGateState();
        Assert.Equal("starting", gate.Status(true, T0));
        gate.Observe("[2026.11.04-18.00.01:000][  0]Cauldron: CAULDRON_GATE: host on proto=1 key=set", T0);
        gate.Observe("[2026.11.04-18.00.02:000][  0]Cauldron: CAULDRON_HOST: roster count=0 players=", T0);
        Assert.Equal("on", gate.Status(true, T0.AddHours(5)));
    }

    [Fact]
    public void WorldUpWithoutTheGateIsNotRunningAfterThreeMinutes()
    {
        var gate = new JoinGateState();
        gate.Observe("[2026.11.04-18.00.02:000][  0]Cauldron: CAULDRON_HOST: roster count=0 players=", T0);
        Assert.Equal("starting", gate.Status(true, T0.AddMinutes(2)));
        Assert.Null(gate.NotRunningSinceUnix(true, T0.AddMinutes(2)));
        Assert.Equal("not_running", gate.Status(true, T0.AddMinutes(3)));
        Assert.Equal(T0.AddMinutes(3).ToUnixTimeSeconds(), gate.NotRunningSinceUnix(true, T0.AddMinutes(9)));
        Assert.Null(gate.NotRunningSinceUnix(false, T0.AddMinutes(9)));
        gate.Observe("Cauldron: CAULDRON_GATE: host on proto=1 key=set", T0.AddMinutes(4));
        Assert.Equal("on", gate.Status(true, T0.AddMinutes(5)));
        Assert.Null(gate.NotRunningSinceUnix(true, T0.AddMinutes(5)));
    }

    [Fact]
    public void ANewGameRunForgetsTheLastOne()
    {
        var gate = new JoinGateState();
        gate.Observe("CAULDRON_GATE: host on proto=1 key=set", T0);
        gate.Reset();
        Assert.Equal("starting", gate.Status(true, T0));
        gate.Observe("CAULDRON_HOST: roster count=1 players=Ann", T0);
        Assert.Equal("not_running", gate.Status(true, T0 + JoinGateState.GraceAfterWorldUp));
    }

    [Fact]
    public void OtherLinesChangeNothing()
    {
        var gate = new JoinGateState();
        gate.Observe("CAULDRON_CHAT: host on proto=1", T0);
        gate.Observe("", T0);
        gate.Observe("LogNet: Join succeeded: Ann", T0);
        Assert.Equal("starting", gate.Status(true, T0.AddHours(1)));
    }
}
