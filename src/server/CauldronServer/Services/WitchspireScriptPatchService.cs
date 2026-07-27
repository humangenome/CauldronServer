using CauldronServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CauldronServer.Services;

public sealed class WitchspireScriptPatchService : IHostedService
{
    private const string Marker = "CAULDRON_DIRECT_IP_SAVE_IDENTITY";
    private readonly ILogger<WitchspireScriptPatchService> _log;
    private readonly CauldronServerOptions _opts;

    public WitchspireScriptPatchService(ILogger<WitchspireScriptPatchService> log, IOptions<CauldronServerOptions> opts)
    {
        _log = log;
        _opts = opts.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var path = ResolveSaveSubsystemPath(_opts);
        if (path is null)
        {
            _log.LogWarning("Witchspire save identity patch skipped: SaveSubsystem.as not found");
            return Task.CompletedTask;
        }

        try
        {
            var result = PatchSaveSubsystem(path);
            if (result == PatchResult.Patched)
                _log.LogInformation("Witchspire save identity patch applied at {Path}", path);
            else if (result == PatchResult.AlreadyPatched)
                _log.LogInformation("Witchspire save identity patch already present at {Path}", path);
            else
                _log.LogWarning("Witchspire save identity patch skipped: expected RegisterPlayer block not found at {Path}", path);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Witchspire save identity patch failed");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static string? ResolveSaveSubsystemPath(CauldronServerOptions opts)
    {
        foreach (var path in CandidateSaveSubsystemPaths(opts))
        {
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    internal static PatchResult PatchSaveSubsystem(string path)
    {
        var original = File.ReadAllText(path);
        if (original.Contains(Marker, StringComparison.Ordinal))
            return PatchResult.AlreadyPatched;

        var patched = TryPatchWithNewline(original, "\r\n") ?? TryPatchWithNewline(original, "\n");
        if (patched is null)
            return PatchResult.NotFound;

        File.WriteAllText(path, patched);
        return PatchResult.Patched;
    }

    private static string? TryPatchWithNewline(string original, string nl)
    {
        var needle =
            $"        Identity.Epic = f\"{{Player.PlayerState.GetUniqueId().ToString().GetHash()}}\";{nl}" +
            $"        if (Player.IsLocalController())";

        if (!original.Contains(needle, StringComparison.Ordinal))
            return null;

        var replacement =
            $"        Identity.Epic = f\"{{Player.PlayerState.GetUniqueId().ToString().GetHash()}}\";{nl}" +
            $"        // {Marker}: direct-IP joins run under Null OSS, so every remote{nl}" +
            $"        // player has the same invalid UniqueId. At RegisterPlayer time the{nl}" +
            $"        // launcher-provided PlayerName is still unique; later save-load can{nl}" +
            $"        // replace it with the character display name.{nl}" +
            $"        const FString CauldronPlayerName = Player.PlayerState.GetPlayerName();{nl}" +
            $"        if (!Player.IsLocalController() && !CauldronPlayerName.IsEmpty()){nl}" +
            $"        {{{nl}" +
            $"            Identity.Epic = f\"cp_{{CauldronPlayerName.GetHash()}}\";{nl}" +
            $"            Identity.Steam = Identity.Epic;{nl}" +
            $"            Log(n\"SaveSystem\", f\"Cauldron direct-IP identity {{CauldronPlayerName}} -> {{Identity.Epic}}\");{nl}" +
            $"            return true;{nl}" +
            $"        }}{nl}" +
            $"        if (Player.IsLocalController())";

        return original.Replace(needle, replacement, StringComparison.Ordinal);
    }

    private static IEnumerable<string> CandidateSaveSubsystemPaths(CauldronServerOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.GameInstallRoot))
        {
            yield return Path.Combine(opts.GameInstallRoot, "Hercules", "Script", "Core", "SaveSystem", "SaveSubsystem.as");
            yield return Path.Combine(opts.GameInstallRoot, "Script", "Core", "SaveSystem", "SaveSubsystem.as");
        }

        if (!string.IsNullOrWhiteSpace(opts.GameExecutablePath))
        {
            var projectDir = TryProjectDirFromExe(opts.GameExecutablePath);
            if (projectDir is not null)
                yield return Path.Combine(projectDir, "Script", "Core", "SaveSystem", "SaveSubsystem.as");
        }

        if (!string.IsNullOrWhiteSpace(opts.GameUserDir))
        {
            var customerRoot = Directory.GetParent(Path.GetFullPath(opts.GameUserDir))?.FullName;
            if (!string.IsNullOrWhiteSpace(customerRoot))
            {
                yield return Path.Combine(customerRoot, "Hercules", "Script", "Core", "SaveSystem", "SaveSubsystem.as");
                yield return Path.Combine(customerRoot, "Script", "Core", "SaveSystem", "SaveSubsystem.as");
            }
        }
    }

    private static string? TryProjectDirFromExe(string exe)
    {
        try
        {
            var win64 = Path.GetDirectoryName(Path.GetFullPath(exe));
            var binaries = win64 is null ? null : Path.GetDirectoryName(win64);
            return binaries is null ? null : Path.GetDirectoryName(binaries);
        }
        catch
        {
            return null;
        }
    }

    internal enum PatchResult
    {
        Patched,
        AlreadyPatched,
        NotFound,
    }
}
