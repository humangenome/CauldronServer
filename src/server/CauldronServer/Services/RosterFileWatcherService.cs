using System.Text.Json;
using System.Text.Json.Serialization;
using Cauldron.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CauldronServer.Services;

/// <summary>
/// Polls <c>roster.json</c> next to CauldronServer.exe and pushes the
/// snapshot into <see cref="PipeServerState"/> so the A2S info response
/// (<see cref="SourceQueryHostedService"/>) and the HTTP players endpoint
/// (<see cref="CauldronHttpService"/>) reflect live player counts and names.
///
/// LEGACY / DORMANT on Witchspire. This is the inherited file-drop roster
/// path: it expects a UE4SS Lua mod to write <c>roster.json</c> next to the
/// executable. Cauldron ships no such mod, so on a real Witchspire host this
/// service never sees a file and stays idle.
///
/// The LIVE roster producer is the AngelScript host mod, which emits the
/// single-line <c>CAULDRON_HOST: roster count=N players=...</c> contract into
/// the UE log; <see cref="HostRosterLogService"/> tails that line and is what
/// actually populates <see cref="PipeServerState"/> today.
///
/// Kept because it is harmless when the file is absent and it remains the
/// drop-in path if a future host mod prefers a JSON file over a log line.
/// Remove it, and its registration in <c>Program.cs</c>, if that never happens.
/// </summary>
public sealed class RosterFileWatcherService : BackgroundService
{
    private readonly ILogger<RosterFileWatcherService> _log;
    private readonly PipeServerState _state;
    private readonly string _rosterPath;
    private DateTimeOffset _lastReadAt = DateTimeOffset.MinValue;
    private long _lastFileSize = -1;

    public RosterFileWatcherService(ILogger<RosterFileWatcherService> log, PipeServerState state)
    {
        _log = log;
        _state = state;
        // Lua mod writes to <CauldronServer dir>\roster.json. CauldronServer's
        // cwd is its own install directory, so the relative path resolves.
        _rosterPath = Path.Combine(AppContext.BaseDirectory, "roster.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Roster watcher started: path={Path}", _rosterPath);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ReadIfChanged();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Roster read failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
        _log.LogInformation("Roster watcher stopping");
    }

    private void ReadIfChanged()
    {
        if (!File.Exists(_rosterPath)) return;
        var info = new FileInfo(_rosterPath);
        if (info.Length == _lastFileSize && info.LastWriteTimeUtc <= _lastReadAt) return;
        _lastFileSize = info.Length;
        _lastReadAt = info.LastWriteTimeUtc;

        string json;
        try
        {
            json = File.ReadAllText(_rosterPath);
        }
        catch (IOException) { return; }   // race with Lua's atomic write — try again next tick

        RosterFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RosterFile>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            _log.LogDebug(ex, "Roster JSON parse failed");
            return;
        }
        if (parsed?.Players is null) return;

        var snapshots = new List<PlayerSnapshot>(parsed.Players.Count);
        foreach (var p in parsed.Players)
        {
            snapshots.Add(new PlayerSnapshot(
                CauldronUserId: p.CauldronUserId ?? "",
                DisplayName: p.DisplayName ?? "Unknown",
                ConnectedAtUnixMs: p.ConnectedAtUnixMs,
                LastPacketUnixMs: p.LastPacketUnixMs,
                PingMs: p.PingMs));
        }
        _state.SetPlayers(snapshots);
        _state.LastReportedPlayerCount = snapshots.Count;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class RosterFile
    {
        [JsonPropertyName("unix_ms")]
        public long UnixMs { get; set; }

        [JsonPropertyName("players")]
        public List<RosterPlayer>? Players { get; set; }
    }

    private sealed class RosterPlayer
    {
        public string? CauldronUserId { get; set; }
        public string? DisplayName { get; set; }
        public long ConnectedAtUnixMs { get; set; }
        public long LastPacketUnixMs { get; set; }
        public int PingMs { get; set; }
    }
}
