using System.Reflection;
using System.Text.RegularExpressions;

namespace CauldronServer.Services;

/// <summary>
/// Single source of truth for version strings surfaced to operators, A2S
/// queries, and the startup banner. Cauldron's own version comes from the
/// assembly; Witchspire's build number is read at runtime from the host's UE log
/// (<c>ws-ue.log</c>, by <see cref="HostRosterLogService"/>), and stays "unknown" until then.
/// </summary>
public static class CauldronVersionInfo
{
    public static string CauldronVersion { get; } = ResolveCauldronVersion();

    public static string CauldronBuild { get; private set; } = "unknown";

    /// <summary>
    /// Called once the host log is parsed. Subsequent A2S query responses +
    /// banner refreshes include it.
    /// </summary>
    public static void SetCauldronBuild(string build)
    {
        if (!string.IsNullOrWhiteSpace(build)) CauldronBuild = build.Trim();
    }

    private static readonly Regex BuildBannerRegex = new(
        @"LogInit:\s+Build:\s+(?<build>[A-Za-z0-9\-_.]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Reads the game's build banner, written near the top of the host log at every start
    /// (<c>LogInit: Build: Hercules-CL-32175</c>), as the short <c>CL-32175</c>: "Hercules" is the
    /// engine module's name, which no player knows, and the number is what decides whether a
    /// player's game can join this host. Null for any other line.
    /// </summary>
    internal static string? TryParseBuildBanner(string line)
    {
        if (string.IsNullOrEmpty(line) || line.IndexOf("LogInit:", StringComparison.Ordinal) < 0) return null;
        var m = BuildBannerRegex.Match(line);
        if (!m.Success) return null;
        var build = m.Groups["build"].Value;
        if (build.StartsWith("Hercules-", StringComparison.OrdinalIgnoreCase)) build = build["Hercules-".Length..];
        return build.Length == 0 ? null : build;
    }

    private static string ResolveCauldronVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip "+commitsha" suffix MSBuild appends in default release builds.
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
