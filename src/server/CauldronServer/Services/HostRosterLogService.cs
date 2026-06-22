using System.Text;
using System.Text.RegularExpressions;
using Cauldron.Protocol;
using CauldronServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CauldronServer.Services;

/// <summary>
/// Tails the Witchspire host UE log (<c>{GameUserDir}\ws-ue.log</c>) for the host
/// mod's authoritative roster line and pushes the live player list into
/// <see cref="PipeServerState"/> so <see cref="SourceQueryHostedService"/> reports
/// the real A2S player count (and the HTTP /players endpoint shows the same).
///
/// Why this exists (v0.1.6): the original count source was a UE4SS Lua roster mod
/// writing <c>roster.json</c> (see <see cref="RosterFileWatcherService"/>), but UE4SS
/// sig-scan is dead on this game build so that producer never runs — A2S reported a
/// static 0 even with players connected. The AngelScript host mod (CauldronHost.as)
/// DOES run and has direct UE reflection on <c>GameState.PlayerArray</c>, so it now
/// emits one greppable line per change:
///
///   CAULDRON_HOST: roster count=N players=name1\tname2\t...
///
/// (count excludes the headless host's own local PlayerState; names are tab-delimited).
/// This service is the authoritative count consumer. It runs alongside the legacy
/// log-tail / roster-file watchers but those are best-effort inference; this line is
/// ground truth straight from PlayerArray.
/// </summary>
public sealed class HostRosterLogService : BackgroundService
{
    // "CAULDRON_HOST: roster count=N players=a\tb\tc"  (players may be empty)
    private static readonly Regex RosterRegex = new(
        @"CAULDRON_HOST:\s+roster\s+count=(?<count>\d+)\s+players=(?<players>.*)$",
        RegexOptions.Compiled);

    private readonly ILogger<HostRosterLogService> _log;
    private readonly CauldronServerOptions _opts;
    private readonly PipeServerState _state;

    // Preserve connected-at timestamps across roster updates so A2S_PLAYER connect
    // time stays monotonic instead of resetting every poll. Keyed by display name.
    private readonly Dictionary<string, long> _connectedAt = new(StringComparer.Ordinal);
    private long _position;
    private int _lastCount = int.MinValue;

    public HostRosterLogService(ILogger<HostRosterLogService> log, IOptions<CauldronServerOptions> opts, PipeServerState state)
    {
        _log = log;
        _opts = opts.Value;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.GameUserDir))
        {
            _log.LogInformation("[roster] host roster tail idle: GameUserDir not configured");
            return;
        }

        var logPath = Path.Combine(_opts.GameUserDir, "ws-ue.log");
        _log.LogInformation("[roster] host roster tail watching {Path}", logPath);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(logPath))
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }

                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _position) _position = 0; // log rotated/truncated on restart
                fs.Seek(_position, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = await sr.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    ProcessLine(line);
                }
                _position = fs.Position;
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { /* mid-write race / rotation — retry next tick */ }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[roster] host roster tail loop error");
            }

            try { await Task.Delay(1500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Pure parser for the host mod's roster line. Returns null if the line is not a
    /// roster line. On success returns the reported count and the (possibly empty) name
    /// list. Exposed internal for unit tests.
    /// </summary>
    internal static (int Count, string[] Names)? TryParseRoster(string line)
    {
        var m = RosterRegex.Match(line);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups["count"].Value, out var count) || count < 0) return null;
        var playersField = m.Groups["players"].Value;
        var names = playersField.Length == 0
            ? Array.Empty<string>()
            : playersField.Split('\t', StringSplitOptions.RemoveEmptyEntries);
        return (count, names);
    }

    private void ProcessLine(string line)
    {
        var parsed = TryParseRoster(line);
        if (parsed is null) return;
        var (count, names) = parsed.Value;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshots = new List<PlayerSnapshot>(names.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in names)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            seen.Add(name);
            if (!_connectedAt.TryGetValue(name, out var connectedAt))
            {
                connectedAt = now;
                _connectedAt[name] = connectedAt;
            }
            snapshots.Add(new PlayerSnapshot(
                CauldronUserId: $"ws:{name}",
                DisplayName: name,
                ConnectedAtUnixMs: connectedAt,
                LastPacketUnixMs: now,
                PingMs: 0));
        }

        // Drop connect timestamps for players no longer present so a reconnect gets a
        // fresh clock (and the dictionary can't grow unbounded).
        foreach (var stale in _connectedAt.Keys.Where(k => !seen.Contains(k)).ToList())
            _connectedAt.Remove(stale);

        // The authoritative count is the host mod's reported count. If the mod reports a
        // count but emitted no parseable names (shouldn't happen, but be defensive), still
        // honor the count by leaving the names empty — A2S_INFO uses Players.Count which we
        // keep aligned by trusting the snapshot list we just built.
        _state.SetPlayers(snapshots);
        // The roster mod owns the live count; clear the legacy log-derived player set so the
        // two sources can't double-count (merge in PipeServerState.Players is by id/name, but
        // the log-tail uses different ids and could inflate the total).
        _state.ClearLogPlayers();
        _state.LastReportedPlayerCount = snapshots.Count;

        if (count != _lastCount)
        {
            _lastCount = count;
            _log.LogInformation("[roster] player count = {Count}{Names}",
                count,
                snapshots.Count > 0 ? " (" + string.Join(", ", snapshots.Select(s => s.DisplayName)) + ")" : "");
        }
    }
}
