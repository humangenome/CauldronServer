namespace Cauldron.Abstractions;

/// <summary>
/// Guard against writing Cauldron's per-instance files into a store-managed
/// (vanilla) Witchspire install. Shared by the supervisor and by any
/// <see cref="IHostLaunchPrep"/> implementation, which both write into the user
/// dir and would corrupt a vanilla install if it were misconfigured.
/// </summary>
public static class VanillaInstallGuard
{
    /// <summary>
    /// Does this path look like a Steam / Epic / MS Store install root for vanilla
    /// Witchspire? Used to refuse Engine.ini and config writes when GameUserDir or
    /// GameInstallRoot points at a store copy of the game.
    /// </summary>
    public static bool LooksLikeVanillaInstallPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // Check the resolved real target too — a user can junction their Cauldron
        // user dir at C:\Cauldron\userdir over a vanilla Witchspire install, and the
        // literal-string check passes while the Engine.ini write lands inside the
        // vanilla folder.
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
            // DirectoryInfo.ResolveLinkTarget(true) walks a junction/symlink chain on
            // .NET 6+ and returns null when the path is not a link.
            var di = new DirectoryInfo(path);
            if (!di.Exists) return null;
            return di.ResolveLinkTarget(true)?.FullName;
        }
        catch { return null; }
    }
}
