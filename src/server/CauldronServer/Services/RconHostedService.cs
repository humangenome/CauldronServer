using Cauldron.Protocol;
using Cauldron.Rcon;
using CauldronServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CauldronServer.Services;

/// <summary>
/// Source RCON server. Translates RCON commands into <see cref="FrameType.RconCommand"/>
/// frames sent to the plugin and awaits the matching response, with a fallback
/// for purely server-side commands (status, players, save).
/// </summary>
public sealed class RconHostedService : IHostedService
{
    private readonly ILogger<RconHostedService> _log;
    private readonly CauldronServerOptions _opts;
    private readonly PipeServerState _state;
    private readonly SaveOrchestratorService _saves;
    private RconServer? _server;

    public RconHostedService(
        ILogger<RconHostedService> log,
        IOptions<CauldronServerOptions> opts,
        PipeServerState state,
        SaveOrchestratorService saves)
    {
        _log = log;
        _opts = opts.Value;
        _state = state;
        _saves = saves;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_opts.RconPassword))
        {
            _log.LogWarning("RCON disabled (no password set in Cauldron:RconPassword)");
            return Task.CompletedTask;
        }
        _server = new RconServer(_opts.RconPort, _opts.RconPassword, ExecuteAsync, _log);
        _server.Start(ct);
        _log.LogInformation("RCON listening on TCP {Port}", _server.BoundPort);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken _)
    {
        if (_server is not null) await _server.StopAsync().ConfigureAwait(false);
    }

    private async Task<string> ExecuteAsync(string command)
    {
        var trimmed = command.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "";

        var parts = trimmed.Split(' ', 2);
        var head = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1] : "";
        // Log the verb + arg length only — never the raw arg, which may carry
        // chat text or operator-entered content.
        _log.LogDebug("RCON dispatch: cmd={Cmd} argLen={ArgLen}", head, rest.Length);
        return head switch
        {
            "help"     => "commands: status, players, ping, save snapshot, save list",
            "status"   => BuildStatus(),
            "players"  => BuildPlayers(),
            "ping"     => "pong",
            "save"     => await HandleSaveAsync(rest).ConfigureAwait(false),
            "snapshot" => await HandleSaveAsync("snapshot").ConfigureAwait(false),
            // Witchspire shows none of these: no in-game overlay or launcher view reads the
            // chat buffer, so answering "chat ok" told an admin a message went out when no
            // player could ever see it. Say so instead.
            "say" or "announce" or "motd" => NotDelivered,
            _          => $"unknown rcon command: {head} (try: help)",
        };
    }

    internal const string NotDelivered =
        "not sent: Witchspire has no in-game message channel Cauldron can post to, so say, announce and motd are not available";

    private async Task<string> HandleSaveAsync(string sub)
    {
        var arg = sub.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(arg) || arg == "snapshot")
        {
            // The game host auto-saves to <userdir>/Saved/SaveGames every ~60s.
            // We snapshot the on-disk save file directly; the plugin SaveQuiesce
            // ack is not required because the game's own save writer is atomic
            // (writes to temp then renames). The FileSystemWatcher in
            // SaveOrchestratorService handles auto-snapshots; this RCON path is
            // for admin-triggered.
            var rec = await _saves.SnapshotAsync("rcon").ConfigureAwait(false);
            return rec is null
                ? "snapshot failed (check cauldron log; save dir likely missing)"
                : $"snapshot ok: {rec.SnapshotId} ({rec.SizeBytes} bytes, sha={rec.Sha256Hex[..16]})";
        }
        if (arg == "list")
        {
            var snaps = _saves.Database.ListSnapshots(20);
            if (snaps.Count == 0) return "no snapshots yet";
            var sb = new System.Text.StringBuilder();
            foreach (var s in snaps)
                sb.AppendLine($"{s.SnapshotId}  {s.SizeBytes}B  age={(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.TakenUnix)}s  sha={s.Sha256Hex[..16]}");
            return sb.ToString().TrimEnd();
        }
        return "usage: save snapshot | save list";
    }

    private string BuildStatus()
    {
        // Same "online" rule as /api/v1/health: the plugin is pinging or the game process is up.
        // The old "plugin=disconnected" line was true of every direct-IP host (no plugin pipe)
        // and told an admin nothing about the world.
        var heartbeatTimeout = TimeSpan.FromSeconds(Math.Max(1, _opts.PluginHeartbeatTimeoutSeconds));
        var running = _state.HasFreshHeartbeat(heartbeatTimeout) || GameProcessProbe.IsAlive(_opts.GamePidFile);
        var name = string.IsNullOrWhiteSpace(_opts.ServerName) ? $"Cauldron - {_opts.InstanceId}" : _opts.ServerName.Trim();
        return FormatStatus(name, running, _state.EffectivePlayerCount, _opts.MaxPlayers, CauldronVersionInfo.CauldronVersion);
    }

    private string BuildPlayers() =>
        FormatPlayers(_state.Players, _opts.MaxPlayers, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    internal static string FormatStatus(string serverName, bool worldRunning, int players, int maxPlayers, string version) =>
        $"{serverName}: world {(worldRunning ? "running" : "not running")}, {players} of {maxPlayers} players, Cauldron {version}";

    // One line per player: name and time on the server. The names come from the host mod's
    // roster line (HostRosterLogService); the reply used to be a count with a builder's note.
    internal static string FormatPlayers(IReadOnlyList<PlayerSnapshot> players, int maxPlayers, long nowUnixMs)
    {
        if (players.Count == 0)
            return "no players online";
        var lines = players.Select(p =>
        {
            var name = string.IsNullOrEmpty(p.DisplayName) ? p.CauldronUserId : p.DisplayName;
            return $"{name}  {FormatOnlineFor(nowUnixMs - p.ConnectedAtUnixMs)}";
        });
        return $"{players.Count} of {maxPlayers} players online\n" + string.Join("\n", lines);
    }

    private static string FormatOnlineFor(long ms)
    {
        var minutes = Math.Max(0, ms) / 60_000;
        if (minutes < 1) return "just joined";
        if (minutes < 60) return $"{minutes}m";
        return $"{minutes / 60}h {minutes % 60:00}m";
    }
}
