using System.IO.Compression;
using System.Security.Cryptography;
using Cauldron.Persistence;
using Cauldron.Protocol;
using CauldronServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CauldronServer.Services;

/// <summary>
/// Save snapshot orchestrator. On a triggered snapshot, sends
/// <see cref="FrameType.SaveQuiesce"/> to the plugin, waits for the
/// game-side quiesce to complete, copies save files to the snapshot
/// directory under an atomic-rename pattern, hashes them, records a
/// row in the database, and rotates old snapshots past retention.
/// </summary>
public sealed class SaveOrchestratorService : IHostedService, IDisposable
{
    private readonly ILogger<SaveOrchestratorService> _log;
    private readonly CauldronServerOptions _opts;
    private readonly PipeServerState _state;
    private readonly CauldronRestartCoordinator _coordinator;
    private readonly CauldronDb _db;
    private readonly string _dbPath;
    private FileSystemWatcher? _watcher;
    private readonly System.Threading.SemaphoreSlim _autoLock = new(1, 1);
    private DateTime _lastAutoSnapshotUtc = DateTime.MinValue;
    // Witchspire saves every few minutes; one automatic snapshot per window keeps a
    // useful history without filling the disk (snapshots are kept 30 days).
    private static readonly TimeSpan AutoSnapshotDebounce = TimeSpan.FromMinutes(15);

    public SaveOrchestratorService(
        ILogger<SaveOrchestratorService> log,
        IOptions<CauldronServerOptions> opts,
        PipeServerState state,
        CauldronRestartCoordinator coordinator)
    {
        _log = log;
        _opts = opts.Value;
        _state = state;
        _coordinator = coordinator;
        _dbPath = Path.Combine(Path.GetDirectoryName(_opts.HmacKeyPath) ?? "data", "cauldron.db");
        _db = new CauldronDb(_dbPath);
    }

    public CauldronDb Database => _db;

    /// <summary>True while the auto-snapshot watcher is on (tests and diagnostics).</summary>
    internal bool AutoSnapshotsOn => _watcher is not null;

    public Task StartAsync(CancellationToken _)
    {
        _log.LogInformation("Save orchestrator ready; db={Path}, save_dir={Dir}, snapshots_enabled={Enabled}",
            _dbPath, _opts.SaveDir, _opts.SnapshotsEnabled);
        TryNormalizeSaveSlots();
        if (_opts.SnapshotsEnabled)
        {
            TryStartFileWatcher();
        }
        else
        {
            _log.LogInformation("Auto-snapshot FileSystemWatcher disabled by config (SnapshotsEnabled=false)");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken _)
    {
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        try { _autoLock.Dispose(); } catch { }
    }

    /// <summary>
    /// Watch the game's SaveGames dir for auto-save writes. Witchspire keeps a world
    /// in a folder (SaveGames\savegame_0\*.sav, one file per player plus the world),
    /// so the watch covers subfolders. When a save lands, debounce and trigger a
    /// Cauldron snapshot. This is the "Cauldron snapshots, Witchspire owns the
    /// trigger" model — no save RPC needed.
    /// </summary>
    private void TryStartFileWatcher()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_opts.GameUserDir))
            {
                _log.LogInformation("FileSystemWatcher skipped: GameUserDir not configured");
                return;
            }
            var sourceDir = Path.Combine(_opts.GameUserDir, "Saved", "SaveGames");
            Directory.CreateDirectory(sourceDir);
            _watcher = new FileSystemWatcher(sourceDir, "*.sav")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            // The game also copies saves into savegame_N\Backups; those copies are not a
            // new world state and are left out of snapshots, so they never trigger one.
            void OnEvent(string fullPath)
            {
                if (IsGameBackupPath(Path.GetRelativePath(sourceDir, fullPath))) return;
                _ = OnSaveFileChangedAsync(fullPath);
            }
            _watcher.Changed += (_, e) => OnEvent(e.FullPath);
            _watcher.Created += (_, e) => OnEvent(e.FullPath);
            _watcher.Renamed += (_, e) => OnEvent(e.FullPath);
            _log.LogInformation("FileSystemWatcher armed on {Dir} (*.sav, subfolders included)", sourceDir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FileSystemWatcher failed to start; auto-snapshots disabled");
        }
    }

    /// <summary>
    /// Witchspire listen-host startup creates a fresh savegame_N.sav when multiple
    /// slots are present and no explicit "continue slot" is supplied. Cauldron
    /// hosts one world per instance, so keep the newest slot as savegame_0.sav
    /// and archive the rest before the game process starts.
    /// </summary>
    private void TryNormalizeSaveSlots()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_opts.GameUserDir))
            {
                _log.LogInformation("Save slot normalization skipped: GameUserDir not configured");
                return;
            }

            var sourceDir = Path.Combine(_opts.GameUserDir, "Saved", "SaveGames");
            if (_coordinator.IsOwnedGameRunning())
            {
                _log.LogInformation("Save slot normalization skipped: owned Witchspire process is already running");
                return;
            }

            SaveSlotNormalizer.Normalize(sourceDir, _log);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Save slot normalization failed; Witchspire may create a new save slot on this launch");
        }
    }

    private async Task OnSaveFileChangedAsync(string path)
    {
        // Debounce: Witchspire may emit multiple write events per save (size, mtime,
        // last access). Only one snapshot per AutoSnapshotDebounce window.
        if (!await _autoLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastAutoSnapshotUtc < AutoSnapshotDebounce) return;
            // Wait briefly for Witchspire to finish its write
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var rec = await SnapshotAsync(requestedBy: "auto:filewatcher", CancellationToken.None).ConfigureAwait(false);
            if (rec is not null)
            {
                _lastAutoSnapshotUtc = now;
                _log.LogInformation("Auto-snapshot {Id} fired from {Path}", rec.SnapshotId, path);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Auto-snapshot from FileSystemWatcher failed for {Path}", path);
        }
        finally
        {
            _autoLock.Release();
        }
    }

    /// <summary>
    /// Trigger a snapshot. Returns the snapshot record on success, null on failure.
    /// Invoked from RCON ("save snapshot"), the FileSystemWatcher when Witchspire auto-saves,
    /// or a scheduled timer.
    /// </summary>
    public async Task<SnapshotRecord?> SnapshotAsync(string requestedBy, CancellationToken ct = default)
    {
        var snapshotId = $"snap-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}".Substring(0, 36);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            // If plugin is connected, request a SaveQuiesce so the game flushes
            // in-flight state. If not connected, fall through — Witchspire writes its
            // own savegame_0.sav atomically (temp + rename), so a snapshot taken
            // without quiesce is still self-consistent. This unblocks the
            // FileSystemWatcher auto-snapshot path which fires when Witchspire has just
            // finished writing the file.
            var conn = _state.Connection;
            if (conn is not null)
            {
                await conn.SendAsync(FrameType.SaveQuiesce,
                    new SaveQuiesceMessage(snapshotId, TimeoutSeconds: 30),
                    ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(_opts.SaveDir);
            var sourceDir = Path.Combine(_opts.GameUserDir, "Saved", "SaveGames");
            if (!Directory.Exists(sourceDir))
            {
                _log.LogWarning("Save source dir not found: {Dir}", sourceDir);
                return null;
            }

            var snapshotPath = Path.Combine(_opts.SaveDir, $"{snapshotId}.zip");
            var tmpPath = snapshotPath + ".tmp";
            CreateSaveGamesZip(sourceDir, tmpPath);
            File.Move(tmpPath, snapshotPath);

            var size = new FileInfo(snapshotPath).Length;
            var hash = await Sha256OfAsync(snapshotPath, ct).ConfigureAwait(false);

            var record = new SnapshotRecord(
                SnapshotId: snapshotId,
                TakenUnix: now,
                FilePath: snapshotPath,
                SizeBytes: size,
                Sha256Hex: hash,
                RetentionDays: 30);
            _db.RecordSnapshot(record);
            _db.Audit(requestedBy, "save.snapshot", target: snapshotId,
                detailJson: $"{{\"path\":\"{snapshotPath}\",\"bytes\":{size}}}", unixSeconds: now);

            _log.LogInformation("Snapshot {Id} written: {Bytes} bytes sha={Hash}", snapshotId, size, hash[..16]);
            RotateOldSnapshots();
            return record;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Snapshot {Id} failed", snapshotId);
            return null;
        }
    }

    private void RotateOldSnapshots()
    {
        try
        {
            var snapshots = _db.ListSnapshots(int.MaxValue);
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var s in snapshots)
            {
                var ageSeconds = nowUnix - s.TakenUnix;
                var maxSeconds = s.RetentionDays * 86_400;
                if (ageSeconds <= maxSeconds) continue;
                if (File.Exists(s.FilePath))
                {
                    try { File.Delete(s.FilePath); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Snapshot rotation pass failed");
        }
    }

    private static void CreateSaveGamesZip(string sourceDir, string destinationPath)
    {
        using var archive = System.IO.Compression.ZipFile.Open(
            destinationPath,
            System.IO.Compression.ZipArchiveMode.Create);

        foreach (var file in EnumerateSnapshotFiles(sourceDir))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            var info = new FileInfo(file);
            if (info.Length == 0 && IsRootSaveSlotPath(relativePath))
            {
                continue;
            }

            archive.CreateEntryFromFile(file, relativePath, System.IO.Compression.CompressionLevel.Optimal);
        }
    }

    /// <summary>
    /// Every file a snapshot holds: the whole SaveGames tree except each world's
    /// <c>Backups</c> folder. Witchspire keeps adding its own copies of the world
    /// there and never prunes them (one long-lived server had 99,313 files,
    /// 503 MB), so zipping it made each snapshot minutes long and hundreds of MB.
    /// The folder is skipped without being walked.
    /// </summary>
    internal static IEnumerable<string> EnumerateSnapshotFiles(string sourceDir)
    {
        var pending = new Stack<string>();
        pending.Push(sourceDir);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(dir))
                yield return file;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (IsGameBackupPath(Path.GetRelativePath(sourceDir, sub))) continue;
                pending.Push(sub);
            }
        }
    }

    /// <summary>
    /// Is this path (relative to SaveGames) the game's own backup folder of a world,
    /// <c>savegame_N/Backups</c>, or anything inside it?
    /// </summary>
    internal static bool IsGameBackupPath(string relativePath) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            relativePath.Replace('\\', '/'),
            @"^savegame_\d+/Backups(/|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// After a restore swapped SaveGames, move each world's <c>Backups</c> history
    /// from the old tree into the restored one, so a restore never deletes it
    /// (snapshots no longer carry it). A backup the incoming zip already has wins.
    /// When the restored tree has no folder for that world, the history goes to
    /// <c>Saved\world-backups-kept\savegame_N-&lt;time&gt;</c> instead. Returns false
    /// if anything could not be moved; the caller then keeps the old tree.
    /// </summary>
    internal static bool KeepGameBackups(string oldSaveGamesDir, string newSaveGamesDir, ILogger log)
    {
        var ok = true;
        if (!Directory.Exists(oldSaveGamesDir)) return true;
        foreach (var slot in Directory.EnumerateDirectories(oldSaveGamesDir))
        {
            var slotName = Path.GetFileName(slot);
            if (!System.Text.RegularExpressions.Regex.IsMatch(slotName, @"^savegame_\d+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant)) continue;
            var oldBackups = Directory.EnumerateDirectories(slot)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Backups", StringComparison.OrdinalIgnoreCase));
            if (oldBackups is null) continue;
            try
            {
                var newSlot = Path.Combine(newSaveGamesDir, slotName);
                if (!Directory.Exists(newSlot))
                {
                    var keptRoot = Path.Combine(Path.GetDirectoryName(newSaveGamesDir)!, "world-backups-kept");
                    Directory.CreateDirectory(keptRoot);
                    var kept = Path.Combine(keptRoot, $"{slotName}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}");
                    Directory.Move(oldBackups, kept);
                    log.LogInformation("Kept the game's backups of {Slot} at {Path}: the restored saves have no folder for that world", slotName, kept);
                    continue;
                }
                var newBackups = Directory.EnumerateDirectories(newSlot)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Backups", StringComparison.OrdinalIgnoreCase));
                if (newBackups is null)
                {
                    Directory.Move(oldBackups, Path.Combine(newSlot, "Backups"));
                    continue;
                }
                foreach (var entry in Directory.EnumerateFileSystemEntries(oldBackups).ToList())
                {
                    var target = Path.Combine(newBackups, Path.GetFileName(entry));
                    if (File.Exists(target) || Directory.Exists(target)) continue;
                    if (Directory.Exists(entry)) Directory.Move(entry, target);
                    else File.Move(entry, target);
                }
            }
            catch (Exception ex)
            {
                ok = false;
                log.LogWarning(ex, "Could not move the game's backups of {Slot} into the restored saves; the old saves stay at {Old}", slotName, oldSaveGamesDir);
            }
        }
        return ok;
    }

    /// <summary>
    /// Does a SaveGames zip carry a world at its root? Either a world folder
    /// (<c>savegame_0/…/*.sav</c>, the current layout) or an older single-file slot
    /// (<c>savegame_0.sav</c>). Empty files do not count.
    /// </summary>
    internal static bool ContainsWorldSave(IEnumerable<(string FullName, long Length)> entries) =>
        entries.Any(e =>
            e.Length > 0 &&
            System.Text.RegularExpressions.Regex.IsMatch(
                e.FullName.Replace('\\', '/'),
                @"^savegame_\d+(\.sav|/.+\.sav)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant));

    private static bool IsRootSaveSlotPath(string relativePath) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            relativePath,
            @"^savegame_\d+\.sav$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static async Task<string> Sha256OfAsync(string path, CancellationToken ct)
    {
        await using var f = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(f, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public IReadOnlyList<SnapshotRecord> ListSnapshots(int limit = 50) =>
        _db.ListSnapshots(limit);

    /// <summary>
    /// Restore a previously-taken snapshot by ID. Takes a pre-restore
    /// snapshot first (so the operation is reversible), kills the game, wipes
    /// SaveGames, extracts the target snapshot zip, then releases the gate
    /// so the supervisor relaunches Witchspire with the restored world.
    /// </summary>
    public async Task<bool> RestoreSnapshotAsync(string snapshotId, string requestedBy, CancellationToken ct = default)
    {
        var snap = _db.ListSnapshots(int.MaxValue).FirstOrDefault(s => s.SnapshotId == snapshotId);
        if (snap is null)
        {
            _log.LogWarning("Restore failed: snapshot {Id} not found", snapshotId);
            return false;
        }
        if (!File.Exists(snap.FilePath))
        {
            _log.LogWarning("Restore failed: snapshot file missing {Path}", snap.FilePath);
            return false;
        }
        return await RestoreFromZipPathAsync(snap.FilePath, requestedBy, $"restore:{snapshotId}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Restore from a freshly-uploaded zip on disk (e.g. an imported vanilla
    /// save or a cross-server transfer payload). The zip must contain the
    /// contents of a SaveGames directory at its root: a world folder
    /// (savegame_0/*.sav) or an older single-file slot (savegame_0.sav).
    /// Takes a pre-restore snapshot, kills the game, swaps in the new files, then
    /// releases the supervisor gate.
    /// </summary>
    public async Task<bool> RestoreFromZipPathAsync(
        string zipPath, string requestedBy, string auditAction, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath))
        {
            _log.LogWarning("Restore failed: source zip missing {Path}", zipPath);
            return false;
        }

        using var _ = await _coordinator.BeginRestoreAsync(ct).ConfigureAwait(false);

        // 1. Stop Witchspire first. Taking a snapshot BEFORE the kill would
        // capture whatever state Witchspire was mid-writing — which on a save
        // tick is half a savegame_0.sav. Kill, wait for exit, then snap.
        _coordinator.KillGame(TimeSpan.FromSeconds(20));

        // 2. Snapshot the now-stable state so a bad restore is reversible.
        var pre = await SnapshotAsync($"pre-restore:{requestedBy}", ct).ConfigureAwait(false);
        if (pre is null)
        {
            _log.LogWarning("Restore aborted: pre-restore snapshot failed");
            return false;
        }

        // 3. Atomic SaveGames swap. Extract the incoming zip into a temp
        // directory first; on success, rename the old SaveGames out of the
        // way and rename the temp dir into place. If anything fails before
        // the rename, the live SaveGames is untouched and the operation is
        // a no-op rather than a corruption.
        try
        {
            var saveGamesDir = Path.Combine(_opts.GameUserDir, "Saved", "SaveGames");
            Directory.CreateDirectory(Path.GetDirectoryName(saveGamesDir)!);

            var stagingDir = saveGamesDir + ".incoming-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var prevDir = saveGamesDir + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Validate the zip BEFORE touching live SaveGames. A zip with no
            // world at the root is almost certainly a customer zipping the
            // wrong folder — refuse rather than wipe their world.
            try
            {
                using var probe = System.IO.Compression.ZipFile.OpenRead(zipPath);
                if (!ContainsWorldSave(probe.Entries.Select(e => (e.FullName, e.Length))))
                {
                    _log.LogWarning("Restore rejected: zip {Path} has no world at its root (savegame_N/*.sav or savegame_N.sav)", zipPath);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Restore rejected: zip {Path} could not be opened", zipPath);
                return false;
            }

            bool savesMovedAside = false;
            try
            {
                Directory.CreateDirectory(stagingDir);
                // .NET 5+ ZipFile.ExtractToDirectory validates entries don't
                // escape the destination (zip-slip is rejected with an
                // IOException), so the staging dir is the canonical sandbox.
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

                if (Directory.Exists(saveGamesDir))
                {
                    Directory.Move(saveGamesDir, prevDir);
                    savesMovedAside = true;
                }
                Directory.Move(stagingDir, saveGamesDir);
                if (Directory.Exists(prevDir))
                {
                    if (KeepGameBackups(prevDir, saveGamesDir, _log))
                    {
                        try { Directory.Delete(prevDir, recursive: true); } catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                // Rollback. If we already renamed the live SaveGames out to
                // prevDir but couldn't get staging into place, restore the
                // original so the customer doesn't end up with NO save dir.
                if (savesMovedAside && Directory.Exists(prevDir) && !Directory.Exists(saveGamesDir))
                {
                    try { Directory.Move(prevDir, saveGamesDir); }
                    catch (Exception rollbackEx)
                    {
                        _log.LogError(rollbackEx,
                            "CRITICAL: rollback of SaveGames from {Prev} to {Live} failed after restore error; " +
                            "manual recovery may be required",
                            prevDir, saveGamesDir);
                    }
                }
                if (Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, recursive: true); } catch { }
                }
                _log.LogError(ex, "Restore failed; SaveGames rolled back from {Prev}", prevDir);
                throw;
            }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _db.Audit(requestedBy, auditAction, target: pre.SnapshotId,
                detailJson: $"{{\"pre_snapshot\":\"{pre.SnapshotId}\",\"source\":\"{Path.GetFileName(zipPath)}\"}}",
                unixSeconds: nowUnix);

            // The FileSystemWatcher's directory handle was opened against
            // the ORIGINAL SaveGames dir; Directory.Move above replaced
            // that inode. Tear it down and re-arm against the new dir or
            // future auto-snapshots stop firing until CauldronServer restart.
            // Only when auto-snapshots are on: a host that turned them off
            // (SnapshotsEnabled=false) must not get them back from a restore.
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
            if (_opts.SnapshotsEnabled)
            {
                TryStartFileWatcher();
            }
            else
            {
                _log.LogInformation("Auto-snapshots stay off after the restore (SnapshotsEnabled=false)");
            }

            _log.LogInformation("Restore complete from {Zip}; pre-snapshot {Id}", zipPath, pre.SnapshotId);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore failed while swapping SaveGames");
            return false;
        }
        // gate released by `using` — supervisor will relaunch Witchspire on its
        // next loop iteration.
    }
}
