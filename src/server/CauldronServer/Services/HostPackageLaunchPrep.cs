using System.Text;
using Cauldron.Abstractions;

namespace CauldronServer.Services;

/// <summary>
/// Built-in <see cref="IHostLaunchPrep"/>: writes the host's Engine.ini into the
/// instance user dir before every launch, and puts <c>steam_appid.txt</c> beside
/// the game executable.
///
/// The Engine.ini is what lets players join by address. It switches the game to
/// Unreal's plain IP networking (<c>OnlineSubsystem=Null</c>, IpNetDriver, network
/// authentication off) and sets <c>[URL] Port</c> to the gameplay port. The engine
/// rewrites its user-scope Engine.ini on every run, so it is written fresh each
/// start rather than once at install time.
///
/// The text comes from the package's <c>engine-ini/Engine.host.ini</c> when one is
/// shipped (beside the supervisor or one folder up, which is where the release zip
/// puts it), with <c>&lt;GAMEPLAY_PORT&gt;</c> replaced by the real port. Without a
/// template the same lines are written from <see cref="BuiltInEngineIni"/>.
/// </summary>
public sealed class HostPackageLaunchPrep : IHostLaunchPrep
{
    public const string TemplateRelativePath = "engine-ini/Engine.host.ini";
    public const string PortPlaceholder = "<GAMEPLAY_PORT>";
    public const string SteamAppId = "2679100";

    /// <summary>Host frame cap. Uncapped, an empty world spins at several hundred fps.</summary>
    public const int HostFrameRate = 60;

    public string Name => "host-package template";

    public void Prepare(HostLaunchContext context, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(log);

        // Refuse to write into a vanilla Witchspire install (misconfigured paths).
        if (VanillaInstallGuard.LooksLikeVanillaInstallPath(context.UserDir))
        {
            log($"Engine.ini write refused: user dir {context.UserDir} looks like a vanilla Witchspire install path. " +
                "Cauldron's user dir must be a separate folder (e.g. C:\\Cauldron\\userdir).");
            return;
        }
        if (VanillaInstallGuard.LooksLikeVanillaInstallPath(context.InstallRoot))
        {
            log($"Launch prep refused: game folder {context.InstallRoot} is inside a store library. " +
                "Copy the game to its own folder (e.g. C:\\Cauldron\\game).");
            return;
        }

        WriteEngineIni(context, log);
        WriteSteamAppId(context, log);

        // Pre-create the online subsystem's cache dir so the first launch does not
        // race the engine creating it.
        Directory.CreateDirectory(Path.Combine(context.UserDir, "Saved", "PersistentDownloadDir", "EOSCache"));
    }

    private static void WriteEngineIni(HostLaunchContext context, Action<string> log)
    {
        if (context.GameplayPort is <= 0 or > 65535)
        {
            log($"Engine.ini NOT written: gameplay port {context.GameplayPort} is not a valid port");
            return;
        }

        var configDir = Path.Combine(context.UserDir, "Saved", "Config", "Windows");
        Directory.CreateDirectory(configDir);
        var enginePath = Path.Combine(configDir, "Engine.ini");

        string text;
        var template = FindTemplate(context.PackageDirectory);
        if (template is not null)
        {
            text = File.ReadAllText(template).Replace(PortPlaceholder, context.GameplayPort.ToString(), StringComparison.Ordinal);
            log($"Host Engine.ini from {template} (port {context.GameplayPort})");
        }
        else
        {
            text = BuiltInEngineIni(context.GameplayPort);
            log($"Host Engine.ini template not found beside the server; writing the built-in one (port {context.GameplayPort})");
        }

        HostModStager.WriteIfChanged(enginePath, text);
        log($"Applied host Engine.ini at {enginePath}");
    }

    private static void WriteSteamAppId(HostLaunchContext context, Action<string> log)
    {
        try
        {
            var exeDir = Path.GetDirectoryName(context.ExecutablePath);
            if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir)) return;
            var path = Path.Combine(exeDir, "steam_appid.txt");
            if (File.Exists(path) && File.ReadAllText(path).Trim() == SteamAppId) return;
            HostModStager.WriteIfChanged(path, SteamAppId);
            log($"Wrote steam_appid.txt ({SteamAppId}) at {path}");
        }
        catch (Exception ex) { log($"steam_appid.txt not written: {ex.Message}"); }
    }

    /// <summary>The release zip puts engine-ini\ beside CauldronServer\, so look there too.</summary>
    internal static string? FindTemplate(string packageDirectory)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory)) return null;
        var rel = TemplateRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var here = Path.Combine(packageDirectory, rel);
        if (File.Exists(here)) return here;
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(packageDirectory));
        if (parent is null) return null;
        var up = Path.Combine(parent.FullName, rel);
        return File.Exists(up) ? up : null;
    }

    /// <summary>
    /// The host Engine.ini. Keep in step with <c>engine-ini/Engine.host.ini</c> in the
    /// package (a test compares them).
    /// </summary>
    public static string BuiltInEngineIni(int gameplayPort)
    {
        var lines = new[]
        {
            "[OnlineSubsystem]",
            "DefaultPlatformService=Null",
            "",
            "[OnlineSubsystemNull]",
            "bEnabled=true",
            "",
            "[OnlineSubsystemSteam]",
            "bEnabled=false",
            "",
            "[EpicOnlineServices]",
            "NetworkAuthenticationMode=Off",
            "",
            "[/Script/Engine.GameEngine]",
            "!NetDriverDefinitions=ClearArray",
            "+NetDriverDefinitions=(DefName=\"GameNetDriver\",DriverClassName=\"/Script/OnlineSubsystemUtils.IpNetDriver\",DriverClassNameFallback=\"/Script/OnlineSubsystemUtils.IpNetDriver\")",
            "+NetDriverDefinitions=(DefName=\"DemoNetDriver\",DriverClassName=\"/Script/Engine.DemoNetDriver\",DriverClassNameFallback=\"/Script/Engine.DemoNetDriver\")",
            "",
            "[/Script/Engine.Engine]",
            "!NetDriverDefinitions=ClearArray",
            "+NetDriverDefinitions=(DefName=\"GameNetDriver\",DriverClassName=\"/Script/OnlineSubsystemUtils.IpNetDriver\",DriverClassNameFallback=\"/Script/OnlineSubsystemUtils.IpNetDriver\")",
            "+NetDriverDefinitions=(DefName=\"DemoNetDriver\",DriverClassName=\"/Script/Engine.DemoNetDriver\",DriverClassNameFallback=\"/Script/Engine.DemoNetDriver\")",
            "bUseFixedFrameRate=true",
            $"FixedFrameRate={HostFrameRate}.000000",
            "",
            "[/Script/OnlineSubsystemUtils.IpNetDriver]",
            "NetConnectionClassName=/Script/OnlineSubsystemUtils.IpConnection",
            "InitialConnectTimeout=600.0",
            "ConnectionTimeout=600.0",
            "",
            "[URL]",
            $"Port={gameplayPort}",
            "",
            "[ConsoleVariables]",
            $"t.MaxFPS={HostFrameRate}",
        };
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l).Append("\r\n");
        return sb.ToString();
    }
}
