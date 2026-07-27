using System.Diagnostics;
using System.Text.Json;
using Cauldron.Abstractions;
using CauldronServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CauldronServer.Services;

/// <summary>
/// Launches the Witchspire headless host, runs the host package's launch prep
/// before every start, watches the process, and restarts on unexpected exit.
///
/// Crash policy: if Witchspire exits within MinHealthyUptimeSeconds, treat as
/// "boot loop" and back off exponentially. After a stable run, exit codes
/// reset the backoff.
///
/// In a managed deploy the host's launch script owns the game lifecycle, so
/// GameInstallRoot/GameExecutablePath are left EMPTY and this supervisor stays
/// idle (it returns immediately from ExecuteAsync).
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

    // Witchspire is an AngelScript title with a session-based join transport, not
    // stock UE IpNetDriver. The host comes up as a session-based listen server driven
    // by the AngelScript host mod:
    //   - The host package's launch prep (IHostLaunchPrep, resolved by
    //     HostLaunchPrepLoader) satisfies the Steam/EOS platform prerequisites and
    //     writes the user-scope Engine.ini before EVERY launch — the engine rewrites
    //     that file each run.
    //   - The CauldronHost.as mod (deployed into <game>\Hercules\Script\Mods\Cauldron\)
    //     signs in, calls UHercOnlineSubsystem::CreateSession(L_StarterIsland, name),
    //     and opens the listen world.
    //   - LAUNCH MUST BE IN SESSION 1 (interactive): the host login persists its
    //     credential through Windows DPAPI, which fails in session 0 (a service). When
    //     this supervisor owns the lifecycle (self-host) it MUST run interactively —
    //     see the session-0 guard in LaunchGame.
    // In a managed deploy GameInstallRoot is EMPTY, so this supervisor stays idle and
    // the host's launch script owns the launch; the path below is the SELF-HOST path
    // and runs the same prep.
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

                // Host prep, every launch (the engine rewrites the Engine.ini each run):
                //   1. Run the host package's launch prep (Engine.ini + platform prereqs).
                //   2. Emit the AngelScript settings sidecar (CauldronHost.as reads it).
                RunLaunchPrep();
                EmitPluginConfig();
                var start = DateTime.UtcNow;

                using var proc = LaunchGame();
                _log.LogInformation("Witchspire launched: pid={Pid}", proc.Id);
                while (!stoppingToken.IsCancellationRequested && !proc.HasExited)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }
                if (stoppingToken.IsCancellationRequested)
                {
                    if (!proc.HasExited)
                    {
                        _log.LogInformation("Stopping — sending Ctrl+C / Close to Witchspire (pid={Pid})", proc.Id);
                        try { proc.CloseMainWindow(); } catch { }
                        if (!proc.WaitForExit(10_000)) proc.Kill(true);
                    }
                    return;
                }

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

        // SESSION-1 HARD REQUIREMENT: the host login persists its credential through
        // Windows DPAPI, which fails without an interactive user profile. If this
        // supervisor is running in session 0 (a Windows service / non-interactive) the
        // login fails and the host can never come up. Warn loudly so the operator runs
        // it interactively (a managed deploy launches the host interactively, where
        // GameInstallRoot is empty and this path is skipped).
        if (OperatingSystem.IsWindows() && Process.GetCurrentProcess().SessionId == 0)
        {
            _log.LogWarning(
                "Launching Witchspire from SESSION 0 — the host login (DPAPI) will likely FAIL. " +
                "The headless host requires an INTERACTIVE (session 1) launch. " +
                "Run CauldronServer interactively, or let the hosting panel own the launch.");
        }

        // Launch args (no map URL — the AngelScript CauldronHost mod drives
        // CreateSession from the menu; an -ExecCmds=open route reverts to the start
        // menu on this build).
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
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.EnvironmentVariables["CAULDRON_INSTANCE"] = _opts.InstanceId;
        // NO_PROXY=* keeps the game's online REST traffic off any host HTTP proxy.
        // (The session transport also needs the SYSTEM WinHTTP proxy set to DIRECT,
        // which is a host-level prereq and not settable from here.)
        psi.EnvironmentVariables["NO_PROXY"] = "*";
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
    }

    // Run the host package's launch prep. The prep implementation manages the
    // Steam/EOS auth prerequisites a headless Witchspire host needs and writes the
    // user-scope Engine.ini; see HostLaunchPrepLoader for how one is resolved.
    private void RunLaunchPrep()
    {
        try
        {
            var exe = ResolveExecutablePath(_opts);
            var context = new HostLaunchContext(
                InstallRoot: string.IsNullOrWhiteSpace(_opts.GameInstallRoot) ? null : _opts.GameInstallRoot,
                ExecutablePath: exe,
                UserDir: _opts.GameUserDir,
                ServerName: _opts.ServerName ?? string.Empty,
                InstanceId: _opts.InstanceId,
                PackageDirectory: AppContext.BaseDirectory);

            _launchPrep.Prepare(context, msg => _log.LogInformation("{Prep}: {Message}", _launchPrep.Name, msg));
        }
        catch (Exception ex) { _log.LogError(ex, "Host launch prep failed"); }
    }

    // Emit the AngelScript settings sidecar (cauldron_settings.json) next to the
    // CauldronHost.as mod. The host mod reads it and applies the difficulty settings
    // via DifficultySettings::Apply on world load. (Replaces the old native-plugin
    // config json — Witchspire's host is AngelScript-driven, not a native plugin.)
    private void EmitPluginConfig()
    {
        try
        {
            var exe = ResolveExecutablePath(_opts);
            var projectDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!)!; // <root>\Hercules
            var modDir = Path.Combine(projectDir, "Script", "Mods", "Cauldron");
            Directory.CreateDirectory(modDir);
            var sidecarPath = Path.Combine(modDir, "cauldron_settings.json");

            // Self-host default settings. In a managed deploy the host writes this
            // sidecar with the operator's difficulty settings; here we emit a sane
            // default so a self-host boots.
            var payload = new
            {
                WorldName = "Land of Hercules",
                ServerName = string.IsNullOrWhiteSpace(_opts.ServerName) ? "Cauldron Server" : _opts.ServerName,
                Difficulty = "Standard",
                MapAssetName = CauldronMapAssetName,
                MapPath = "/Game/Levels/FlyingIslands/" + CauldronMapAssetName,
                Settings = new
                {
                    DeathPenalty = "DropAllExceptHotbar",
                    FamiliarDamage = 1.0,
                    EnemyDamage = 1.0,
                    FamiliarPermadeath = false,
                    ExperienceMultiplier = 1.0,
                    FamiliarExperienceMultiplier = 0.8,
                    PlayerSkillExperienceMultiplier = 1.0,
                    ShareExperience = true,
                    LootAbundance = 1.0,
                    FamiliarLingeringChanceMultiplier = 1.0,
                    DayNightTimescale = 1.0,
                },
            };
            // Do NOT overwrite a sidecar the host already wrote (it carries the real
            // operator settings); only seed one if absent.
            if (!File.Exists(sidecarPath))
            {
                File.WriteAllText(sidecarPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                _log.LogInformation("Emitted default AngelScript settings sidecar at {Path}", sidecarPath);
            }
        }
        catch (Exception ex) { _log.LogError(ex, "EmitPluginConfig (AngelScript sidecar) failed"); }
    }

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

    // API-parity helpers only. The Witchspire EOS host does NOT travel via a map URL —
    // the AngelScript CauldronHost mod drives UHercOnlineSubsystem::CreateSession from
    // the menu (a command-line/-ExecCmds open route reverts to the StartMenu on this
    // build). These describe the listen world the mod opens, for diagnostics/tests.
    public static string BuildHostTravelUrl(string saveSlot = CauldronCanonicalSaveSlot)
        => CauldronMapAssetName + BuildHostTravelOptions(saveSlot);

    public static string BuildHostTravelOptions(string saveSlot = CauldronCanonicalSaveSlot)
    {
        // saveSlot is accepted for API parity but the listen world is just ?listen.
        return "?listen";
    }
}
