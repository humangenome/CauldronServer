using Cauldron.Protocol;

namespace CauldronServer.Services;

/// <summary>
/// Places (or removes) the in-game chat scripts on a self-hosted server before every start.
///
/// Source: the package's <c>angelscript-mods\cauldron_chat\</c> (CauldronChat.as, CauldronChatHost.as,
/// game-names.txt). Destination: <c>&lt;game&gt;\Hercules\Script\Mods\CauldronChat\</c>, a folder of
/// its own so the host mod folder stays exactly the package's.
///
/// Placement needs three things: chat switched on (<c>Cauldron:Chat:InGameEnabled</c>), all three
/// package files present, and every name in game-names.txt present in this game build (see
/// <see cref="ChatScriptNames"/>): a script that no longer compiles would freeze the game before the
/// world opens. Otherwise the folder's scripts are removed and the log says why. It never stops the
/// server from starting.
/// </summary>
public static class ChatModStager
{
    public const string SharedScript = "CauldronChat.as";
    public const string HostScript = "CauldronChatHost.as";
    public const string NamesFile = "game-names.txt";
    public static readonly string PackageDir = Path.Combine("angelscript-mods", "cauldron_chat");

    public static string Destination(string projectDir) => Path.Combine(projectDir, "Script", "Mods", "CauldronChat");

    /// <summary>True when the chat scripts are in place after the call.</summary>
    public static bool Stage(string packageDirectory, string projectDir, bool enabled, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var dest = Destination(projectDir);
        try
        {
            if (!enabled)
            {
                if (RemoveScripts(dest) > 0) log($"In-game chat is off: removed the chat scripts from {dest}");
                return false;
            }

            var source = Path.Combine(packageDirectory, PackageDir);
            var shared = Path.Combine(source, SharedScript);
            var host = Path.Combine(source, HostScript);
            var names = Path.Combine(source, NamesFile);
            if (!File.Exists(shared) || !File.Exists(host) || !File.Exists(names))
            {
                RemoveScripts(dest);
                log($"In-game chat not placed: the package has no complete {PackageDir} folder");
                return false;
            }

            var entries = ChatScriptNames.Parse(File.ReadAllText(names));
            var missing = ChatScriptNames.Missing(Path.Combine(projectDir, "Script"), entries, includeHost: true);
            if (missing.Count > 0)
            {
                RemoveScripts(dest);
                log("In-game chat not placed: this game build no longer has " +
                    string.Join("; ", missing.Take(6).Select(m => m.Name)) +
                    (missing.Count > 6 ? $" (and {missing.Count - 6} more)" : "") +
                    ". The server runs without chat until Cauldron is updated for this build.");
                return false;
            }

            Directory.CreateDirectory(dest);
            HostModStager.WriteIfChanged(Path.Combine(dest, SharedScript), File.ReadAllText(shared));
            HostModStager.WriteIfChanged(Path.Combine(dest, HostScript), File.ReadAllText(host));
            foreach (var stale in Directory.EnumerateFiles(dest, "*.as", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(stale);
                if (name.Equals(SharedScript, StringComparison.OrdinalIgnoreCase) || name.Equals(HostScript, StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(stale);
            }
            log($"In-game chat placed at {dest}");
            return true;
        }
        catch (Exception ex)
        {
            try { RemoveScripts(dest); } catch (Exception) { }
            log($"In-game chat not placed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static int RemoveScripts(string dest)
    {
        if (!Directory.Exists(dest)) return 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(dest, "*.as", SearchOption.TopDirectoryOnly))
        {
            File.Delete(f);
            n++;
        }
        return n;
    }
}
