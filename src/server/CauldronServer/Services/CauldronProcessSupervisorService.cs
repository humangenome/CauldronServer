using System.Diagnostics;
using System.Text.Json;
using CauldronServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CauldronServer.Services;

/// <summary>
/// Launches the Witchspire headless host, applies the host package's Engine.ini
/// template on every launch, watches the process, and restarts on unexpected exit.
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

    private const int MinHealthyUptimeSeconds = 60;
    private const int MaxBackoffSeconds = 300;
    public const string CauldronCanonicalSaveSlot = "savegame_0";

    // Witchspire is an AngelScript title with a session-based join transport, not stock
    // UE IpNetDriver. The host comes up as a session-based listen server driven by the
    // AngelScript host mod:
    //   - The host package supplies the headless-host launch prep (applied as an
    //     Engine.ini template before every launch — the engine rewrites it each run).
    //   - The host mod (deployed under <game>\Hercules\Script\Mods\Cauldron\) opens the
    //     listen world (L_StarterIsland) and publishes the joinable session.
    //   - The launch must be interactive (session 1); a non-interactive service
    //     (session 0) cannot complete the host's auth prep — see the session guard in
    //     LaunchGame.
    // In a managed deploy GameInstallRoot is empty, so this supervisor stays idle and
    // the host's launch script owns the launch. The path below is the self-host path.
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
        CauldronRestartCoordinator coordinator)
    {
        _log = log;
        _opts = opts.Value;
        _hmac = hmac;
        _coordinator = coordinator;
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
                //   1. Apply the host package's Engine.ini template.
                //   2. Emit the AngelScript settings sidecar (the host mod reads it).
                ApplyHostEngineIni();
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

        // SESSION-1 HARD REQUIREMENT: the host's auth prep stores its credential via
        // Windows DPAPI, which needs an interactive user profile. If this supervisor is
        // running in session 0 (a Windows service / non-interactive), the host login
        // will fail and the world can never come up. Warn loudly so the operator runs
        // it interactively (a managed deploy launches the host interactively, where
        // GameInstallRoot is empty and this path is skipped).
        if (OperatingSystem.IsWindows() && Process.GetCurrentProcess().SessionId == 0)
        {
            _log.LogWarning(
                "Launching Witchspire from SESSION 0 — the host login (DPAPI) will likely FAIL. " +
                "The headless host requires an INTERACTIVE (session 1) launch. " +
                "Run CauldronServer interactively, or let the hosting panel own the launch.");
        }

        // Launch args (no map URL — the AngelScript host mod drives CreateSession from
        // the menu; an -ExecCmds=open route reverts to the start menu on this build).
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
        // (A misconfigured system proxy can also break the session transport — make
        // sure the host's WinHTTP proxy is set to DIRECT, which is a host-level prereq
        // not settable from here.)
        psi.EnvironmentVariables["NO_PROXY"] = "*";
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
    }

    // Apply the host-package's Engine.ini template into the user-scope config dir
    // before every launch. The engine rewrites this file each run, so the supervisor
    // re-applies the host package's template (shipped as engine-ini\Engine.host.ini)
    // on each start. The managed host package supplies the headless-host launch prep;
    // self-hosters drop their own Engine.host.ini template into the package.
    private void ApplyHostEngineIni()
    {
        // Refuse to write into a vanilla Witchspire install (misconfigured GameUserDir).
        if (LooksLikeVanillaInstallPath(_opts.GameUserDir))
        {
            _log.LogError("Engine.ini write refused: GameUserDir={Dir} looks like a vanilla Witchspire install path. " +
                          "Cauldron's user dir must be a separate folder (e.g. C:\\Cauldron\\UserDir).",
                          _opts.GameUserDir);
            return;
        }

        var configDir = Path.Combine(_opts.GameUserDir, "Saved", "Config", "Windows");
        Directory.CreateDirectory(configDir);
        var enginePath = Path.Combine(configDir, "Engine.ini");

        // Copy the host package's Engine.host.ini template if present; otherwise leave
        // the engine's own config untouched. The template carries the host launch prep.
        var template = Path.Combine(AppContext.BaseDirectory, "engine-ini", "Engine.host.ini");
        if (File.Exists(template))
        {
            File.Copy(template, enginePath, overwrite: true);
            _log.LogInformation("Applied host Engine.ini template at {Path}", enginePath);
        }
        else
        {
            _log.LogWarning("Host Engine.ini template not found at {Template} — skipping (host package may supply it elsewhere)", template);
        }
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

            // Self-host default settings. In a panel-managed deploy the host writes this
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
            // Do NOT overwrite a sidecar the panel already wrote (it carries the real
            // customer settings); only seed one if absent.
            if (!File.Exists(sidecarPath))
            {
                File.WriteAllText(sidecarPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                _log.LogInformation("Emitted default AngelScript settings sidecar at {Path}", sidecarPath);
            }
        }
        catch (Exception ex) { _log.LogError(ex, "EmitPluginConfig (AngelScript sidecar) failed"); }
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

    /// <summary>
    /// Heuristic: does this path look like a Steam / Epic / MS Store install
    /// root for vanilla Witchspire? Used to refuse Engine.ini / plugin-config
    /// writes that would corrupt a vanilla install if GameUserDir/GameInstallRoot
    /// were misconfigured.
    /// </summary>
    internal static bool LooksLikeVanillaInstallPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // Check the resolved real target too — a customer can junction
        // their Cauldron user dir at C:\Cauldron\userdir over a vanilla
        // Witchspire install and the literal-string check passes while the
        // Engine.ini write lands inside the vanilla folder.
        return MatchesVanillaSubstring(path)
            || MatchesVanillaSubstring(TryResolveSymlinkTarget(path));
    }

    private static bool MatchesVanillaSubstring(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Replace('/', '\\').ToLowerInvariant();
        return p.Contains(@"\steamapps\common\")
            || p.Contains(@"\steamlibrary\")
            || p.Contains(@"\epicgameslauncher\")
            || p.Contains(@"\epic games\")
            || p.Contains(@"\windowsapps\");
    }

    private static string? TryResolveSymlinkTarget(string path)
    {
        try
        {
            // DirectoryInfo.LinkTarget on .NET 6+ returns the immediate
            // target of a junction/symlink; null otherwise. Path.GetFullPath
            // canonicalises any '..' segments. ResolveLinkTarget(true)
            // walks the chain (multiple junctions) but isn't strictly
            // needed for the common case.
            var di = new DirectoryInfo(path);
            if (!di.Exists) return null;
            var resolved = di.ResolveLinkTarget(true);
            return resolved?.FullName;
        }
        catch { return null; }
    }
}
