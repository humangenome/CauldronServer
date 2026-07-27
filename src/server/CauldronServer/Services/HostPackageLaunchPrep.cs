using Cauldron.Abstractions;

namespace CauldronServer.Services;

/// <summary>
/// Built-in <see cref="IHostLaunchPrep"/>: applies the host package's Engine.ini
/// template to the instance user dir before every launch.
///
/// The engine rewrites its user-scope Engine.ini on every run, so the template has
/// to be re-applied each start rather than written once at install time. Drop the
/// template at <c>engine-ini/Engine.host.ini</c> beside the supervisor; if it is
/// absent this prep leaves the engine's own config alone.
/// </summary>
public sealed class HostPackageLaunchPrep : IHostLaunchPrep
{
    public const string TemplateRelativePath = "engine-ini/Engine.host.ini";

    public string Name => "host-package template";

    public void Prepare(HostLaunchContext context, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(log);

        // Refuse to write into a vanilla Witchspire install (misconfigured GameUserDir).
        if (VanillaInstallGuard.LooksLikeVanillaInstallPath(context.UserDir))
        {
            log($"Engine.ini write refused: user dir {context.UserDir} looks like a vanilla Witchspire install path. " +
                "Cauldron's user dir must be a separate folder (e.g. C:\\Cauldron\\UserDir).");
            return;
        }

        var configDir = Path.Combine(context.UserDir, "Saved", "Config", "Windows");
        Directory.CreateDirectory(configDir);
        var enginePath = Path.Combine(configDir, "Engine.ini");

        var template = Path.Combine(
            context.PackageDirectory,
            TemplateRelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(template))
        {
            File.Copy(template, enginePath, overwrite: true);
            log($"Applied host Engine.ini template at {enginePath}");
        }
        else
        {
            log($"Host Engine.ini template not found at {template} — skipping (the host package may supply it elsewhere)");
        }

        // The online subsystem persists its credential under the user dir; pre-create
        // the directory so the first launch does not race the engine creating it.
        Directory.CreateDirectory(Path.Combine(context.UserDir, "Saved", "PersistentDownloadDir", "EOSCache"));
    }
}
