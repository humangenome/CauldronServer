using System.Diagnostics;
using Cauldron.Abstractions;
using CauldronServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CauldronServer.Services;

/// <summary>
/// Self-host mode: launches the Witchspire headless host, prepares the game before
/// every start, watches the process, and restarts it when it exits.
///
/// Before every start it:
///   1. copies the host mod into <c>&lt;game&gt;\Hercules\Script\Mods\Cauldron</c> with
///      this server's gameplay port (<see cref="HostModStager"/>); without the mod the
///      game sits at its menu and never opens a world, so a failed copy means no start;
///   2. runs the host launch prep (<see cref="HostPackageLaunchPrep"/> unless a package
///      ships its own): the host Engine.ini with the gameplay port, and
///      <c>steam_appid.txt</c> beside the game executable.
/// The game then starts headless (<c>-nullrhi</c>), the host mod opens
/// <c>L_StarterIsland</c> as a listen server on the gameplay port, and players join
/// by address.
///
/// Crash policy: if Witchspire exits within MinHealthyUptimeSeconds, treat as
/// "boot loop" and back off exponentially. After a stable run, exit codes
/// reset the backoff.
///
/// When a hosting panel owns the game's lifecycle it leaves GameInstallRoot and
/// GameExecutablePath EMPTY and this supervisor stays idle (it returns immediately
/// from ExecuteAsync).
/// </summary>
public sealed class CauldronProcessSupervisorService : BackgroundService
{
    private readonly ILogger<CauldronProcessSupervisorService> _log;
    private readonly CauldronServerOptions _opts;
    private readonly HmacKeyService _hmac;
    private readonly CauldronRestartCoordinator _coordinator;
    private readonly IHostLaunchPrep _launchPrep;

    private const int MinHealthyUptimeSeconds = 60;
    private const int MaxBackoffSeconds = 300;
    public const string CauldronCanonicalSaveSlot = "savegame_0";

    // The world the host mod opens (see CauldronHost.as HOST_MAP_PATH).
    private const string CauldronMapAssetName = "L_StarterIsland";

    // Witchspire ships ONE shipping exe (host + client). The packaged project dir is
    // "Hercules" (the UE module name); "Witchspire" is probed defensively in case a
    // package nests under the .uproject name.
    private static readonly string[][] CauldronExeRelativePaths =
    [
        ["Hercules", "Binaries", "Win64", "Hercules-Win64-Shipping.exe"],
        ["Witchspire", "Binaries", "Win64", "Hercules-Win64-Shipping.exe"],
        ["Binaries", "Win64", "Hercules-Win64-Shipping.exe"],
        ["Hercules-Win64-Shipping.exe"],
    ];

    public CauldronProcessSupervisorService(
        ILogger<CauldronProcessSupervisorService> log,
        IOptions<CauldronServerOptions> opts,
        HmacKeyService hmac,
        CauldronRestartCoordinator coordinator,
        IHostLaunchPrep launchPrep)
    {
        _log = log;
        _opts = opts.Value;
        _hmac = hmac;
        _coordinator = coordinator;
        _launchPrep = launchPrep;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if ((string.IsNullOrEmpty(_opts.GameInstallRoot) && string.IsNullOrEmpty(_opts.GameExecutablePath))
            || !OperatingSystem.IsWindows())
        {
            _log.LogWarning("Process supervisor idle: Witchspire executable not configured or not on Windows");
            return;
        }

        // A host from this game folder still running here was left by an earlier
        // CauldronServer that did not get to stop it. Starting a second one would
        // fight it for the port, so stop it first.
        if (_coordinator.IsOwnedGameRunning())
        {
            _log.LogWarning("A Witchspire host from {Root} is still running from an earlier run; stopping it before starting a fresh one",
                _opts.GameInstallRoot);
            _coordinator.KillGame(TimeSpan.FromSeconds(20));
        }

        var backoffSeconds = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for any in-flight restore to finish before relaunching.
                // The restore code holds the gate while it mutates SaveGames;
                // launching Witchspire while that's in progress would race the file
                // copy and corrupt the world.
                await _coordinator.WaitForNoRestoreAsync(stoppingToken).ConfigureAwait(false);

                // Every launch: the host mod with this server's port, then the launch
                // prep (Engine.ini, steam_appid.txt). The engine rewrites its Engine.ini
                // each run and a game update can replace script files, so neither is a
                // one-time install step.
                if (!StageHostMod())
                {
                    backoffSeconds = Math.Min(MaxBackoffSeconds, Math.Max(30, backoffSeconds * 2));
                    _log.LogError("Witchspire not started: the host mod could not be put in place (see above). Retrying in {Seconds}s", backoffSeconds);
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }
                RunLaunchPrep();
                var start = DateTime.UtcNow;

                using var proc = LaunchGame();
                _log.LogInformation("Witchspire launched: pid={Pid}", proc.Id);
                KillOnCloseJob.TryAdd(proc, msg => _log.LogWarning("{Message}", msg));
                WritePidFile(proc.Id);
                try
                {
                    while (!stoppingToken.IsCancellationRequested && !proc.HasExited)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                if (stoppingToken.IsCancellationRequested)
                {
                    if (!proc.HasExited)
                    {
                        _log.LogInformation("Stopping Witchspire (pid={Pid})", proc.Id);
                        try { proc.CloseMainWindow(); } catch { }
                        if (!proc.WaitForExit(3_000)) proc.Kill(true);
                    }
                    DeletePidFile();
                    return;
                }
                DeletePidFile();

                var uptime = DateTime.UtcNow - start;
                _log.LogWarning("Witchspire exited code={Code} uptime={Uptime}s", proc.ExitCode, (int)uptime.TotalSeconds);

                if (uptime.TotalSeconds >= MinHealthyUptimeSeconds)
                {
                    backoffSeconds = 1; // stable run — reset backoff
                }
                else
                {
                    backoffSeconds = Math.Min(MaxBackoffSeconds, backoffSeconds * 2);
                    _log.LogWarning("Boot-loop suspected — backing off {Seconds}s before restart", backoffSeconds);
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Supervisor loop error — retry in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private Process LaunchGame()
    {
        var exe = ResolveExecutablePath(_opts);
        if (!File.Exists(exe))
            throw new FileNotFoundException($"Witchspire binary not found at {exe}");

        // Launch args: the host mod opens the world from the menu, so no map URL here.
        var args = string.Join(' ',
            "-nullrhi",
            "-unattended",
            "-nosound",
            "-nosplash",
            "-log",
            $"-UserDir={EscapeArg(_opts.GameUserDir)}",
            $"-abslog={EscapeArg(Path.Combine(_opts.GameUserDir, "ws-ue.log"))}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            // Its own hidden console: sharing ours printed the game's whole log (thousands of
            // lines an hour) into the server's window. The game's log is <GameUserDir>\ws-ue.log.
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.EnvironmentVariables["CAULDRON_INSTANCE"] = _opts.InstanceId;
        // NO_PROXY=* keeps the game's own web requests off any machine-wide HTTP proxy.
        psi.EnvironmentVariables["NO_PROXY"] = "*";
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
    }

    // The game's pid, for the online check (A2S, /api/v1/health) and for scoped kills.
    private void WritePidFile(int pid)
    {
        if (string.IsNullOrWhiteSpace(_opts.GamePidFile)) return;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_opts.GamePidFile));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            HostModStager.WriteIfChanged(_opts.GamePidFile, pid.ToString());
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not write the game pid file {Path}", _opts.GamePidFile); }
    }

    private void DeletePidFile()
    {
        if (string.IsNullOrWhiteSpace(_opts.GamePidFile)) return;
        try { File.Delete(_opts.GamePidFile); } catch { }
    }

    // Put the host mod into the game with this server's port. False = do not start.
    private bool StageHostMod()
    {
        var exe = ResolveExecutablePath(_opts);
        if (!File.Exists(exe))
        {
            _log.LogError("Witchspire not found at {Exe}. Copy the game into GameInstallRoot ({Root}) or set GameExecutablePath.",
                exe, _opts.GameInstallRoot);
            return false;
        }
        var projectDir = ProjectDirFromExe(exe);
        return HostModStager.Stage(AppContext.BaseDirectory, projectDir, _opts.GameplayPort, msg =>
        {
            if (msg.Contains("NOT staged", StringComparison.Ordinal)) _log.LogError("{Message}", msg);
            else _log.LogInformation("{Message}", msg);
        });
    }

    // Run the host launch prep: the host Engine.ini (direct-IP networking and the
    // gameplay port) and steam_appid.txt. See HostLaunchPrepLoader for how one is chosen.
    private void RunLaunchPrep()
    {
        try
        {
            var exe = ResolveExecutablePath(_opts);
            var context = new HostLaunchContext(
                InstallRoot: string.IsNullOrWhiteSpace(_opts.GameInstallRoot) ? FindInstallRootFromExe(exe) : _opts.GameInstallRoot,
                ExecutablePath: exe,
                UserDir: _opts.GameUserDir,
                ServerName: _opts.ServerName ?? string.Empty,
                InstanceId: _opts.InstanceId,
                PackageDirectory: AppContext.BaseDirectory,
                GameplayPort: _opts.GameplayPort);

            _launchPrep.Prepare(context, msg => _log.LogInformation("{Prep}: {Message}", _launchPrep.Name, msg));
        }
        catch (Exception ex) { _log.LogError(ex, "Host launch prep failed"); }
    }

    // <root>\Hercules\Binaries\Win64\exe -> <root>\Hercules
    internal static string ProjectDirFromExe(string exe) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!)!;

    // Given <root>\Hercules\Binaries\Win64\exe, walk up to the install <root>.
    private static string? FindInstallRootFromExe(string exe)
    {
        try
        {
            var win64 = Path.GetDirectoryName(exe);              // ...\Binaries\Win64
            var binaries = Path.GetDirectoryName(win64);          // ...\Binaries
            var project = Path.GetDirectoryName(binaries);        // ...\Hercules
            return Path.GetDirectoryName(project);                // <root>
        }
        catch { return null; }
    }

    private static string EscapeArg(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    internal static string ResolveExecutablePath(CauldronServerOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.GameExecutablePath))
        {
            return Path.GetFullPath(opts.GameExecutablePath);
        }

        foreach (var relativePath in CauldronExeRelativePaths)
        {
            var candidate = Path.Combine([opts.GameInstallRoot, .. relativePath]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine([opts.GameInstallRoot, .. CauldronExeRelativePaths[0]]);
    }

    // Describe the listen world the host mod opens, for diagnostics and tests.
    public static string BuildHostTravelUrl(string saveSlot = CauldronCanonicalSaveSlot)
        => CauldronMapAssetName + BuildHostTravelOptions(saveSlot);

    public static string BuildHostTravelOptions(string saveSlot = CauldronCanonicalSaveSlot)
    {
        // saveSlot is accepted for API parity but the listen world is just ?listen.
        return "?listen";
    }
}
