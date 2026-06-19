using System.Reflection;

namespace CauldronServer.Services;

/// <summary>
/// Single source of truth for version strings surfaced to operators, A2S
/// queries, and the startup banner. Cauldron's own version comes from the
/// assembly; Witchspire's build number is read at runtime from the host's UE log.
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
