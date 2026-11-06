namespace CauldronServer.Services;

/// <summary>
/// What the game's own log says about the join password gate (dist/angelscript-mods/cauldron_gate), for the public
/// health report. The gate scripts log "CAULDRON_GATE: host on ..." once the world is up; the host mod logs
/// "CAULDRON_HOST: roster ..." every 30 s from then on. A server that should have a password
/// (<c>Cauldron:JoinPasswordRequired</c>) but whose world has been up for 3 minutes without the gate's line is open
/// to anyone with the address: the hosting panel could not place the gate (a game update changed a name it uses),
/// and the health report says <c>join_gate = not_running</c> so the hosting side's watcher can raise it.
/// </summary>
public sealed class JoinGateState
{
    public const string HostOnMarker = "CAULDRON_GATE: host on";
    public const string RosterMarker = "CAULDRON_HOST: roster";
    public static readonly TimeSpan GraceAfterWorldUp = TimeSpan.FromMinutes(3);

    /// <summary>The one the services share (the game log tail writes it, the health report reads it).</summary>
    public static JoinGateState Shared { get; } = new();

    private readonly object _gate = new();
    private DateTimeOffset? _hostOnAt;
    private DateTimeOffset? _worldUpAt;

    /// <summary>The game log started over (a new game process): forget what the last run said.</summary>
    public void Reset()
    {
        lock (_gate) { _hostOnAt = null; _worldUpAt = null; }
    }

    /// <summary>Called for every line of the game's log.</summary>
    public void Observe(string line, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_gate)
        {
            if (_hostOnAt is null && line.Contains(HostOnMarker, StringComparison.Ordinal)) _hostOnAt = now;
            if (_worldUpAt is null && line.Contains(RosterMarker, StringComparison.Ordinal)) _worldUpAt = now;
        }
    }

    /// <summary>
    /// null when no password is required (the key is left out of the health report);
    /// "on" once the gate said so; "not_running" when the world has been up for 3 minutes without it;
    /// "starting" before that.
    /// </summary>
    public string? Status(bool passwordRequired, DateTimeOffset now)
    {
        if (!passwordRequired) return null;
        lock (_gate)
        {
            if (_hostOnAt is not null) return "on";
            if (_worldUpAt is not null && now - _worldUpAt.Value >= GraceAfterWorldUp) return "not_running";
            return "starting";
        }
    }

    /// <summary>
    /// When this run became "not_running" (the world up time plus the grace), as Unix seconds; null otherwise. One
    /// value per game run, so a watcher can raise each episode once.
    /// </summary>
    public long? NotRunningSinceUnix(bool passwordRequired, DateTimeOffset now)
    {
        if (Status(passwordRequired, now) != "not_running") return null;
        lock (_gate)
        {
            return _worldUpAt is null ? null : (_worldUpAt.Value + GraceAfterWorldUp).ToUnixTimeSeconds();
        }
    }
}
