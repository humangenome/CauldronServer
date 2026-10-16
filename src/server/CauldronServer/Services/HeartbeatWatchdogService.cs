using System.Diagnostics;
using CauldronServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CauldronServer.Services;

/// <summary>
/// Watches plugin heartbeats. If we haven't heard from the plugin in
/// <see cref="CauldronServerOptions.PluginHeartbeatTimeoutSeconds"/>, log a warning;
/// future work hooks process supervision into this to restart the Witchspire instance.
///
/// Witchspire servers have no join password (see CheckCauldronAuthLuaReady). The
/// <see cref="CheckServerPasswordReady"/> path is a leftover from a native-plugin
/// design: Witchspire hosts load no such plugin, so nothing ever connects to the
/// pipe and these checks stay quiet.
/// </summary>
public sealed class HeartbeatWatchdogService : BackgroundService
{
    private readonly ILogger<HeartbeatWatchdogService> _log;
    private readonly PipeServerState _state;
    private readonly CauldronRestartCoordinator _coordinator;
    private readonly CauldronServerOptions _opts;
    private readonly TimeSpan _timeout;

    // Grace period after we first see the plugin connect before we'll
    // start fail-closing. The plugin's bootstrap thread installs the
    // ApproveLogin hook AFTER the pipe handshake, so the first few
    // heartbeats can legitimately report hook-not-ready while the
    // bootstrap is still racing.
    private static readonly TimeSpan AuthGraceWindow = TimeSpan.FromSeconds(20);

    // Throttle for the "configured but native gate down" warning so we
    // don't spam the log every ~10s while waiting on the supervisor.
    private DateTimeOffset _lastFailClosedWarnAt = DateTimeOffset.MinValue;

    public HeartbeatWatchdogService(
        ILogger<HeartbeatWatchdogService> log,
        PipeServerState state,
        CauldronRestartCoordinator coordinator,
        IOptions<CauldronServerOptions> options)
    {
        _log = log;
        _state = state;
        _coordinator = coordinator;
        _opts = options.Value;
        _timeout = TimeSpan.FromSeconds(options.Value.PluginHeartbeatTimeoutSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, _timeout.TotalSeconds / 3));
        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                CheckHeartbeatStale();
                CheckServerPasswordReady();
                CheckCauldronAuthLuaReady();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HeartbeatWatchdog tick error");
            }
        }
    }

    // Witchspire servers have no join password. The password gate this setting fed
    // was a UE4SS Lua mod plus a native plugin from an earlier Cauldron design; neither
    // exists on a Witchspire host, so there is nothing to wait for. The old fail-closed
    // check stopped the game every three minutes once a password was set.
    private bool _authPasswordIgnoredLogged;

    private void CheckCauldronAuthLuaReady()
    {
        if (string.IsNullOrEmpty(_opts.CauldronAuthPassword) || _authPasswordIgnoredLogged) return;
        _authPasswordIgnoredLogged = true;
        _log.LogWarning(
            "CauldronAuthPassword is set, but Witchspire servers have no join password: nothing on the host can check it, " +
            "so it is ignored and anyone with the address can join.");
    }

    private static readonly string[] CauldronKillProcessNameWhitelist = new[]
    {
        // Process names are sometimes case-insensitive on Windows;
        // Process.ProcessName has no .exe suffix. The plugin is loaded
        // into the Witchspire binary.
        "Hercules-Win64-Shipping",
    };

    private void TryKillByPluginPid(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return;

            // PID-reuse guard. Cauldron.dll is loaded into the game process,
            // so the PluginPid reported on handshake must belong to one
            // of the game binaries. If the PID was recycled between the
            // handshake and the kill, the new owner's process name will
            // not match the game whitelist; refusing to kill prevents us
            // from terminating an unrelated user process. Case-insensitive
            // because Windows process names round-trip case unpredictably.
            var name = p.ProcessName ?? "";
            var isWhitelistedG2 = false;
            foreach (var allowed in CauldronKillProcessNameWhitelist)
            {
                if (string.Equals(name, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    isWhitelistedG2 = true;
                    break;
                }
            }
            if (!isWhitelistedG2)
            {
                _log.LogWarning(
                    "FAIL-CLOSED (Lua): PluginPid={Pid} now belongs to '{Name}' (not Witchspire); refusing to kill",
                    pid, name);
                return;
            }

            _log.LogWarning("FAIL-CLOSED (Lua): killing Witchspire plugin host pid={Pid} name={Name} via PluginPid path", pid, name);
            p.Kill(entireProcessTree: false);
        }
        catch (ArgumentException)
        {
            // pid no longer exists — race with normal supervisor exit
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FAIL-CLOSED (Lua): KillByPluginPid({Pid}) failed", pid);
        }
    }

    // Tracks the last staleness state we logged so we emit one line on each
    // transition (fresh -> stale, stale -> fresh) rather than spamming every tick.
    private bool _heartbeatStale;

    private void CheckHeartbeatStale()
    {
        var conn = _state.Connection;
        if (conn is null) return;
        var last = _state.LastHeartbeatAt;
        if (last is null) return;
        var age = DateTimeOffset.UtcNow - last.Value;
        var stale = age > _timeout;

        _log.LogDebug("Heartbeat: instance={Instance} pid={Pid} age={Age}s stale={Stale}",
            conn.InstanceId, conn.PluginPid, (int)age.TotalSeconds, stale);

        if (stale && !_heartbeatStale)
        {
            _log.LogWarning("Plugin heartbeat went STALE: instance={Instance} pid={Pid} age={Age}s",
                conn.InstanceId, conn.PluginPid, (int)age.TotalSeconds);
        }
        else if (!stale && _heartbeatStale)
        {
            _log.LogInformation("Plugin heartbeat RECOVERED: instance={Instance} pid={Pid} age={Age}s",
                conn.InstanceId, conn.PluginPid, (int)age.TotalSeconds);
        }
        else if (stale)
        {
            // Keep the original per-tick warning as a Debug heartbeat-still-stale
            // line so verbose logs retain the continuous signal.
            _log.LogDebug("Plugin heartbeat still stale: instance={Instance} pid={Pid} age={Age}s",
                conn.InstanceId, conn.PluginPid, (int)age.TotalSeconds);
        }

        _heartbeatStale = stale;
    }

    private void CheckServerPasswordReady()
    {
        // Steady-state under the Lua-only enforcement model is
        // ServerPasswordConfigured=0 across the entire fleet. The native
        // ApproveLogin hook is intentionally disabled (plugin-config.json
        // always has an empty ServerPassword), so if the plugin EVER
        // reports Configured=1, something upstream is broken and we
        // could be heading for the Witchspire crash loop that necessitated
        // the pivot to Lua-only enforcement in the first place. Treat that
        // as a fail-closed condition.
        var conn = _state.Connection;
        if (conn is null) return;

        // Wait for the bootstrap-grace window after first contact. The
        // plugin's bootstrap thread populates the heartbeat fields
        // shortly after the handshake; first heartbeat or two can race.
        var sinceConnect = DateTimeOffset.UtcNow - conn.ConnectedAt;
        if (sinceConnect < AuthGraceWindow) return;

        // Plugin reporting Configured=0 is the expected steady state.
        // Nothing to do.
        if (_state.LastServerPasswordConfigured == 0) return;

        // Plugin says "I see ServerPassword in plugin-config.json." Nothing in
        // Cauldron writes one any more, so this means a stale or hand-edited
        // plugin config from an earlier design. Stop the game before two
        // password layers race into the old crash.
        var now = DateTimeOffset.UtcNow;
        var sinceWarn = now - _lastFailClosedWarnAt;
        if (sinceWarn > TimeSpan.FromSeconds(15))
        {
            _log.LogCritical(
                "FAIL-CLOSED: native plugin reported ServerPasswordConfigured={Configured} " +
                "hookReady={Ready}, but Cauldron enforces password in CauldronAuth.lua only " +
                "and emits an empty ServerPassword in plugin-config.json. This usually means a " +
                "stale plugin-config.json from an earlier deploy or a manually-edited config. " +
                "Stopping Witchspire to prevent the native gate from racing with the Lua gate " +
                "(which reproduces the Witchspire fatal crash loop). Fix plugin-config.json " +
                "ServerPassword field to empty and restart.",
                _state.LastServerPasswordConfigured, _state.LastServerPasswordHookReady);
            _lastFailClosedWarnAt = now;
        }

        // Kill the game. Path-scoped KillGame covers standalone deploys (where
        // GameInstallRoot is configured); PluginPid-based kill covers
        // panel-managed deploys (where GameInstallRoot is intentionally
        // empty and KillGame logs "no anchor to scope by"). The IPC
        // handshake gives us the exact game PID — Cauldron.dll calls
        // GetCurrentProcessId() inside the game process when sending
        // handshake.
        try
        {
            _coordinator.KillGame(TimeSpan.FromSeconds(10));
            TryKillByPluginPid(conn.PluginPid);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FAIL-CLOSED: KillGame / KillByPluginPid threw");
        }
    }
}
