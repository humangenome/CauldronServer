namespace Cauldron.Abstractions;

/// <summary>
/// Per-launch preparation of a Witchspire install, run by the process supervisor
/// immediately before every start.
///
/// Witchspire is an online title: a headless host still has to satisfy the game's
/// Steam/EOS platform prerequisites before it will reach a listen world. That
/// prep is host-package specific, so it lives behind this interface rather than
/// in the supervisor. A package may ship its own implementation; when none is
/// present the supervisor falls back to <c>HostPackageLaunchPrep</c>, which
/// applies the package's own <c>engine-ini/Engine.host.ini</c> template.
///
/// Implementations must be idempotent — the supervisor calls <see cref="Prepare"/>
/// on every launch, including every crash restart.
/// </summary>
public interface IHostLaunchPrep
{
    /// <summary>Short name for logs (e.g. "host-package template").</summary>
    string Name { get; }

    /// <summary>
    /// Prepare the install for one launch. Must not throw for recoverable
    /// conditions — log and return instead, so a missing optional asset does not
    /// take the supervisor's restart loop down.
    /// </summary>
    void Prepare(HostLaunchContext context, Action<string> log);
}

/// <summary>
/// Everything a <see cref="IHostLaunchPrep"/> needs, with no dependency on the
/// server's options type (so a prep implementation can live in its own assembly).
/// </summary>
/// <param name="InstallRoot">Root of the Witchspire install, or null if unresolved.</param>
/// <param name="ExecutablePath">Full path to the shipping executable.</param>
/// <param name="UserDir">Cauldron's per-instance user dir (never the vanilla install).</param>
/// <param name="ServerName">Operator-visible server name.</param>
/// <param name="InstanceId">Cauldron instance id.</param>
/// <param name="PackageDirectory">Directory the supervisor was published into — where package assets live.</param>
public sealed record HostLaunchContext(
    string? InstallRoot,
    string ExecutablePath,
    string UserDir,
    string ServerName,
    string InstanceId,
    string PackageDirectory);
