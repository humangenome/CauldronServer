using System.Text;
using System.Text.RegularExpressions;
using Cauldron.Abstractions;

namespace CauldronServer.Services;

/// <summary>
/// Copies the package's host mod into the game before every launch.
///
/// The host mod (<c>CauldronHost.as</c>) is what turns a headless Witchspire into a
/// server: once the menu settles it runs <c>open L_StarterIsland?listen?Port=N</c>.
/// Witchspire compiles loose AngelScript from <c>&lt;game&gt;\Hercules\Script\Mods</c>,
/// so the mod has to sit there, with <c>HOST_PORT</c> set to this server's gameplay
/// port, every time the game starts.
///
/// Rules this follows:
///   - Only <c>Script\Mods\Cauldron</c> is touched. Any other <c>.as</c> file left in
///     that folder is removed, because one stale file that no longer compiles stops
///     the world from opening at all.
///   - It refuses a game folder inside a Steam, Epic or Microsoft Store library: the
///     host mod would turn that copy of the game into a server the next time its
///     owner played.
///   - Files are written to a temp name and moved into place, so a crash mid-write
///     never leaves half a script behind.
/// </summary>
public static class HostModStager
{
    public const string HostModFileName = "CauldronHost.as";

    /// <summary>Where the mod sits in the package, relative to the supervisor folder.</summary>
    public static readonly string[] PackageModDirs =
    [
        Path.Combine("angelscript-mods", "cauldron_host"),
        Path.Combine("angelscript-mods", "Cauldron"),
    ];

    private static readonly Regex HostPortConst =
        new(@"const\s+int\s+HOST_PORT\s*=\s*\d+\s*;", RegexOptions.CultureInvariant);

    /// <summary>The game's mod folder for a given <c>Hercules</c> project dir.</summary>
    public static string ModDestination(string projectDir) =>
        Path.Combine(projectDir, "Script", "Mods", "Cauldron");

    public static string? FindPackageModDir(string packageDirectory)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory)) return null;
        foreach (var rel in PackageModDirs)
        {
            var dir = Path.Combine(packageDirectory, rel);
            if (File.Exists(Path.Combine(dir, HostModFileName))) return dir;
        }
        return null;
    }

    /// <summary>
    /// Stage the host mod. Returns true when <c>CauldronHost.as</c> is in place with
    /// <paramref name="gameplayPort"/> as its port; false means the game must not be
    /// started (it would sit at the menu and never open a world).
    /// </summary>
    public static bool Stage(string packageDirectory, string projectDir, int gameplayPort, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (gameplayPort is <= 0 or > 65535)
        {
            log($"Host mod NOT staged: GameplayPort {gameplayPort} is not a valid port. Set Cauldron:GameplayPort in appsettings.json.");
            return false;
        }

        if (VanillaInstallGuard.LooksLikeVanillaInstallPath(projectDir))
        {
            log($"Host mod NOT staged: {projectDir} is inside a Steam, Epic or Microsoft Store library. " +
                "Copy the game to its own folder (for example C:\\Cauldron\\game) and point GameInstallRoot there; " +
                "the host mod would otherwise turn your own copy of the game into a server.");
            return false;
        }

        var source = FindPackageModDir(packageDirectory);
        if (source is null)
        {
            log($"Host mod NOT staged: {HostModFileName} not found under {packageDirectory} " +
                $"({string.Join(" or ", PackageModDirs)}). Re-extract the server package.");
            return false;
        }

        try
        {
            var dest = ModDestination(projectDir);
            Directory.CreateDirectory(dest);

            var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(source, "*.as", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                shipped.Add(name);
                var text = File.ReadAllText(file);

                if (name.Equals(HostModFileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!HostPortConst.IsMatch(text))
                    {
                        log($"Host mod NOT staged: the HOST_PORT line is missing from {file}.");
                        return false;
                    }
                    text = SetHostPort(text, gameplayPort);
                }

                WriteIfChanged(Path.Combine(dest, name), text);
            }

            foreach (var stale in Directory.EnumerateFiles(dest, "*.as", SearchOption.TopDirectoryOnly))
            {
                if (shipped.Contains(Path.GetFileName(stale))) continue;
                File.Delete(stale);
                log($"Removed {Path.GetFileName(stale)} from {dest}: it is not part of this package, and a script that no longer compiles stops the world from opening");
            }

            var staged = Path.Combine(dest, HostModFileName);
            if (ReadHostPort(File.ReadAllText(staged)) != gameplayPort)
            {
                log($"Host mod NOT staged: {staged} does not carry port {gameplayPort} after the copy.");
                return false;
            }

            log($"Host mod staged at {staged} (port {gameplayPort})");
            return true;
        }
        catch (Exception ex)
        {
            log($"Host mod NOT staged: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static string SetHostPort(string text, int port) =>
        HostPortConst.Replace(text, $"const int HOST_PORT = {port};", 1);

    internal static int? ReadHostPort(string text)
    {
        var m = Regex.Match(text, @"const\s+int\s+HOST_PORT\s*=\s*(\d+)\s*;", RegexOptions.CultureInvariant);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) ? p : null;
    }

    /// <summary>Temp file + move, and only when the content changed.</summary>
    internal static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        var tmp = path + ".cauldron-tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }
}
