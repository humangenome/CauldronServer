using System.Reflection;
using Cauldron.Abstractions;

namespace CauldronServer.Services;

/// <summary>
/// Resolves the <see cref="IHostLaunchPrep"/> the supervisor runs before each
/// launch.
///
/// A host package may ship its own prep plugin — an assembly named
/// <c>Cauldron.HostPrep*.dll</c> beside the supervisor (or under
/// <c>hostprep/</c>) exposing a public parameterless <see cref="IHostLaunchPrep"/>.
/// That is how a package supplies the Steam/EOS auth prerequisites its own
/// headless host needs. When no plugin is present the loader returns
/// <see cref="HostPackageLaunchPrep"/>, which applies the package's
/// <c>engine-ini/Engine.host.ini</c> template.
///
/// Loading is best-effort: a broken or unreadable plugin logs and falls back
/// rather than taking the supervisor down.
/// </summary>
public static class HostLaunchPrepLoader
{
    public const string PluginFilePattern = "Cauldron.HostPrep*.dll";
    public const string PluginSubdirectory = "hostprep";

    public static IHostLaunchPrep Load(string packageDirectory, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        foreach (var candidate in EnumeratePluginCandidates(packageDirectory))
        {
            try
            {
                var asm = Assembly.LoadFrom(candidate);
                var type = asm.GetTypes().FirstOrDefault(t =>
                    t is { IsClass: true, IsAbstract: false, IsPublic: true }
                    && typeof(IHostLaunchPrep).IsAssignableFrom(t)
                    && t.GetConstructor(Type.EmptyTypes) is not null);

                if (type is null) continue;
                if (Activator.CreateInstance(type) is not IHostLaunchPrep prep) continue;

                log($"Host launch prep: loaded '{prep.Name}' from {Path.GetFileName(candidate)}");
                return prep;
            }
            catch (Exception ex)
            {
                log($"Host launch prep: ignoring {Path.GetFileName(candidate)} — {ex.GetType().Name}: {ex.Message}");
            }
        }

        var builtin = new HostPackageLaunchPrep();
        log($"Host launch prep: using built-in '{builtin.Name}'");
        return builtin;
    }

    internal static IEnumerable<string> EnumeratePluginCandidates(string packageDirectory)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory)) yield break;

        foreach (var dir in new[] { packageDirectory, Path.Combine(packageDirectory, PluginSubdirectory) })
        {
            string[] files;
            try
            {
                files = Directory.Exists(dir)
                    ? Directory.GetFiles(dir, PluginFilePattern)
                    : [];
            }
            catch { continue; }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) yield return f;
        }
    }
}
