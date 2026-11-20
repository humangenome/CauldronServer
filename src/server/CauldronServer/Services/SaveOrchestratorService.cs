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

    /// <summary>Free bytes on the disk under a path (tests replace it).</summary>
    internal Func<string, long?> FreeSpaceProbe { get; set; } = RestoreZipLimits.FreeBytesOf;

    private RestoreZipLimits.Caps RestoreCaps => new(
        _opts.MaxRestoreUnpackedBytes, _opts.MaxRestoreEntries, _opts.MaxRestoreEntryBytes, _opts.RestoreFreeSpaceFloorBytes);

    private long? RestoreFreeBytes()
    {
        if (_opts.RestoreFreeSpaceFloorBytes <= 0) return null;
        return FreeSpaceProbe(SavedDir);
    }

    private string SavedDir => Path.Combine(_opts.GameUserDir, "Saved");

    /// <summary>
    /// Tests only: runs right before each folder a restore makes, renames or deletes, with the step's name, so a
    /// test can put a link in place at exactly that moment. Null otherwise.
    /// </summary>
    internal Action<string>? TestBeforeMove { get; set; }

    /// <summary>
    /// The one link a save folder may sit under (<see cref="SecondDriveRule"/>), for a user folder. Tests give it
    /// stand-in drives; on a machine it is the server's own folder kept on a second drive.
    /// </summary>
    internal Func<string?, SecondDriveRule> FolderRule { get; set; } = SecondDriveRule.ForUserFolder;

    // The zip a restore is reading right now: snapshot rotation never removes it.
    private readonly object _restoreSourceGate = new();
    private string? _restoreSource;

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
            var linksLeftOut = CreateSaveGamesZip(sourceDir, tmpPath, FolderRule(_opts.GameUserDir));
            if (linksLeftOut.Count > 0)
                _log.LogWarning("Snapshot {Id}: {Count} link(s) in the save folder were left out, not followed: {Links}",
                    snapshotId, linksLeftOut.Count, string.Join(", ", linksLeftOut.Take(10)));
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

    /// <summary>
    /// Remove the snapshots past the room they may take, oldest first (<see cref="SnapshotBudget"/>: the newest
    /// <c>MaxSnapshotsKept</c> within <c>MaxSnapshotBytes</c>, and 30 days). Runs after every snapshot, the
    /// pre-restore copy included, and after every upload. The newest snapshot and one a restore is reading stay.
    /// A zip is removed only when it sits directly in <c>SaveDir</c>, and its row goes with it.
    /// </summary>
    internal void RotateOldSnapshots()
    {
        try
        {
            string? reading;
            lock (_restoreSourceGate) reading = _restoreSource;
            var items = _db.ListSnapshots(int.MaxValue)
                .Select(s => new SnapshotBudget.Item(s.SnapshotId, s.TakenUnix, s.SizeBytes, s.RetentionDays, s.FilePath));
            var keep = reading is null ? Array.Empty<string>() : new[] { reading };
            foreach (var s in SnapshotBudget.ToRemove(items, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                         _opts.MaxSnapshotsKept, _opts.MaxSnapshotBytes, keep))
            {
                // The row stays while its zip does; the next pass tries again.
                if (!TryRemoveSnapshotFile(s.FilePath)) continue;
                _db.DeleteSnapshot(s.Id);
                _log.LogInformation("Snapshot {Id} removed by rotation ({Bytes} bytes, taken {Taken:u})",
                    s.Id, s.SizeBytes, DateTimeOffset.FromUnixTimeSeconds(s.TakenUnix));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Snapshot rotation pass failed");
        }
    }

    // A snapshot zip is removed only when it sits directly in SaveDir, so a row can never send the removal anywhere
    // else. File.Delete removes a link itself, never what it points to. A zip that is already gone counts as removed.
    private bool TryRemoveSnapshotFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_opts.SaveDir));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(full), dir, comparison) || Directory.Exists(full))
            {
                _log.LogWarning("Snapshot rotation left {Path} alone: it is not a file in the snapshot folder {Dir}", full, dir);
                return false;
            }
            File.Delete(full);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Snapshot rotation could not remove {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Zips the save tree to <paramref name="destinationPath"/> and returns the links in it that were
    /// left out. The walk never follows a link (<see cref="SnapshotWalk"/>), and the file list is made
    /// before the zip exists, so a save folder under any link but the server's own folder kept on a
    /// second drive is refused with nothing written. Zip names are relative to the real save folder.
    /// </summary>
    internal static List<string> CreateSaveGamesZip(string sourceDir, string destinationPath, SecondDriveRule? rule = null)
    {
        var linksLeftOut = new List<string>();
        var files = EnumerateSnapshotFiles(sourceDir, linksLeftOut, out var walkedRoot, rule);
        using var archive = System.IO.Compression.ZipFile.Open(
            destinationPath,
            System.IO.Compression.ZipArchiveMode.Create);

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(walkedRoot, file).Replace('\\', '/');
            var info = new FileInfo(file);
            if (info.Length == 0 && IsRootSaveSlotPath(relativePath))
            {
                continue;
            }

            archive.CreateEntryFromFile(file, relativePath, System.IO.Compression.CompressionLevel.Optimal);
        }

        return linksLeftOut;
    }

    /// <summary>
    /// Every file a snapshot holds: the whole SaveGames tree except each world's
    /// <c>Backups</c> folder. Witchspire keeps adding its own copies of the world
    /// there and never prunes them (one long-lived server had 99,313 files,
    /// 503 MB), so zipping it made each snapshot minutes long and hundreds of MB.
    /// The folder is skipped without being walked. No link is followed (<see cref="SnapshotWalk"/>).
    /// </summary>
    internal static IEnumerable<string> EnumerateSnapshotFiles(string sourceDir) =>
        EnumerateSnapshotFiles(sourceDir, new List<string>(), out _);

    /// <summary>
    /// The same files; the links left out are added to <paramref name="linksLeftOut"/>, and
    /// <paramref name="walkedRoot"/> is the real save folder they sit under (<see cref="SnapshotWalk"/>).
    /// </summary>
    internal static List<string> EnumerateSnapshotFiles(string sourceDir, ICollection<string> linksLeftOut, out string walkedRoot,
        SecondDriveRule? rule = null) =>
        SnapshotWalk.PlainFiles(sourceDir, linksLeftOut, out walkedRoot, IsGameBackupPath, rule);

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
            // Nothing is moved out of a link (it could pull files from outside the save into it): a link stays in
            // the old saves, and the old saves are kept.
            if (RestoreFolders.IsLink(slot))
            {
                ok = false;
                log.LogWarning("Left the link {Slot} in the old saves at {Old}; nothing is moved through a link", slot, oldSaveGamesDir);
                continue;
            }
            var oldBackups = Directory.EnumerateDirectories(slot)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Backups", StringComparison.OrdinalIgnoreCase));
            if (oldBackups is null) continue;
            if (RestoreFolders.IsLink(oldBackups))
            {
                ok = false;
                log.LogWarning("Left the link {Backups} in the old saves at {Old}; nothing is moved through a link", oldBackups, oldSaveGamesDir);
                continue;
            }
            try
            {
                var newSlot = Path.Combine(newSaveGamesDir, slotName);
                if (!Directory.Exists(newSlot))
                {
                    var keptRoot = Path.Combine(Path.GetDirectoryName(newSaveGamesDir)!, "world-backups-kept");
                    // Never moved into a link (Security, 11/19): a link or a file at world-backups-kept leaves the
                    // backups in the old saves, which are then kept.
                    if (RestoreFolders.EntryProblem(keptRoot) is string keptWhy)
                    {
                        ok = false;
                        log.LogWarning("The game's backups of {Slot} stay in the old saves at {Old}: {Why}", slotName, oldSaveGamesDir, keptWhy);
                        continue;
                    }
                    Directory.CreateDirectory(keptRoot);
                    if (RestoreFolders.EntryProblem(keptRoot) is string keptAfter)
                    {
                        ok = false;
                        log.LogWarning("The game's backups of {Slot} stay in the old saves at {Old}: {Why}", slotName, oldSaveGamesDir, keptAfter);
                        continue;
                    }
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
                if (RestoreFolders.IsLink(newBackups))
                {
                    ok = false;
                    log.LogWarning("The restored saves have a link at {Backups}; the game's backups of {Slot} stay in the old saves at {Old}", newBackups, slotName, oldSaveGamesDir);
                    continue;
                }
                foreach (var entry in Directory.EnumerateFileSystemEntries(oldBackups).ToList())
                {
                    if (RestoreFolders.IsLink(entry))
                    {
                        ok = false;
                        continue;
                    }
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

        // 0. Check the zip BEFORE stopping anything: a zip with no world at the root (almost certainly
        // the wrong folder zipped), one that unpacks to more than the caps allow, one with an entry whose
        // path leaves the folder or whose name Windows would not store as a plain file, and one whose
        // data is not the size it states is refused with the game still running and the world untouched.
        // Until 2026-11-15 the world check ran after the kill, so a wrong zip stopped the world for
        // nothing, and nothing limited what a zip unpacked to: a small zip could unpack to far more and
        // fill the disk every server on the machine shares. Until 2026-11-17 a path that left the folder
        // and a size that lied were caught only during the unpack, after the stop.
        try
        {
            using var probe = System.IO.Compression.ZipFile.OpenRead(zipPath);
            if (!ContainsWorldSave(probe.Entries.Select(e => (e.FullName, e.Length))))
            {
                _log.LogWarning("Restore rejected before stopping the game: zip {Path} has no world at its root (savegame_N/*.sav or savegame_N.sav)", zipPath);
                return false;
            }
            // The directory first (cheap), then one read of the data, writing nothing; the read is bounded
            // by the caps the first check just held the stated sizes to.
            var why = RestoreZipLimits.CheckBeforeUnpack(probe, RestoreCaps, RestoreFreeBytes())
                ?? RestoreZipLimits.CheckStatedSizesHold(probe);
            if (why is not null)
            {
                _log.LogWarning("Restore refused before stopping the game: zip {Path} {Why}", zipPath, why);
                return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restore rejected before stopping the game: zip {Path} could not be opened or read", zipPath);
            return false;
        }

        // The folders the restore writes in, before stopping anything (2026-11-18): the Saved folder and every
        // folder above it must be real folders (except the server's own folder kept on a second drive), and so
        // must the live SaveGames folder. Checked again right before every folder is made, renamed or deleted.
        var rule = FolderRule(_opts.GameUserDir);
        var folderWhy = RestoreFolders.Problem(SavedDir, rule, out var realSaved);
        var saveGamesDir = folderWhy is null ? Path.Combine(realSaved, "SaveGames") : "";
        folderWhy ??= RestoreFolders.EntryProblem(saveGamesDir);
        if (folderWhy is not null)
        {
            _log.LogWarning("Restore refused before stopping the game: {Why}; a restore never writes through a link", folderWhy);
            return false;
        }

        lock (_restoreSourceGate) _restoreSource = Path.GetFullPath(zipPath);
        try
        {
            return await StopAndSwapAsync(zipPath, requestedBy, auditAction, rule, realSaved, saveGamesDir, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_restoreSourceGate) _restoreSource = null;
        }
        // gate released by `using` — supervisor will relaunch Witchspire on its
        // next loop iteration.
    }

    private async Task<bool> StopAndSwapAsync(string zipPath, string requestedBy, string auditAction,
        SecondDriveRule rule, string realSaved, string saveGamesDir, CancellationToken ct)
    {
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

        // 3. Atomic SaveGames swap, in the real Saved folder checked before the stop. Extract the incoming zip into
        // a temp directory first; on success, rename the old SaveGames out of the way and rename the temp dir into
        // place. If anything fails before the rename, the live SaveGames is untouched and the operation is a no-op
        // rather than a corruption. Right before each folder is made, renamed or deleted, the Saved folder and the
        // folders involved are checked again: a link that appeared stops the restore there.
        try
        {
            var savedDir = SavedDir;
            void CheckFolders(string step, params string[] folders)
            {
                TestBeforeMove?.Invoke(step);
                RestoreFolders.CheckBeforeMove(savedDir, rule, realSaved, folders);
            }

            var stagingDir = saveGamesDir + ".incoming-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var prevDir = saveGamesDir + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // The zip's world and sizes were checked in step 0, before the game was stopped.

            bool savesMovedAside = false;
            try
            {
                CheckFolders("make-staging", saveGamesDir, stagingDir, prevDir);
                Directory.CreateDirectory(realSaved);
                Directory.CreateDirectory(stagingDir);
                CheckFolders("unpack", stagingDir);
                // Entries may not escape the staging dir (zip-slip is an IOException), nothing is written through
                // a link, and the unpack stops the moment it writes more than the caps allow, whatever sizes the
                // zip states; the staging dir is the sandbox and is removed on any failure below.
                RestoreZipLimits.ExtractWithinCaps(zipPath, stagingDir, RestoreCaps, RestoreFreeBytes());

                if (Directory.Exists(saveGamesDir))
                {
                    CheckFolders("move-live-aside", saveGamesDir, prevDir);
                    Directory.Move(saveGamesDir, prevDir);
                    savesMovedAside = true;
                }
                CheckFolders("move-incoming-in", stagingDir, saveGamesDir);
                Directory.Move(stagingDir, saveGamesDir);
                if (Directory.Exists(prevDir))
                {
                    // The folder check again right before the game's backups move (Security, 11/19), with the
                    // folder they may move into. The new world is already in place, so a failed check keeps the
                    // replaced saves (and their backups) where they are instead of failing the restore.
                    string? keepWhy = null;
                    try { CheckFolders("keep-backups", prevDir, saveGamesDir, Path.Combine(realSaved, "world-backups-kept")); }
                    catch (IOException ex) { keepWhy = ex.Message; }
                    if (keepWhy is not null)
                        _log.LogWarning("The game's backups stay in the replaced saves at {Prev}: {Why}", prevDir, keepWhy);
                    else if (KeepGameBackups(prevDir, saveGamesDir, _log))
                    {
                        try
                        {
                            CheckFolders("delete-old", prevDir);
                            RestoreFolders.DeleteTree(prevDir);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "The replaced saves were left at {Prev}", prevDir);
                        }
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
                    try
                    {
                        CheckFolders("rollback", saveGamesDir, prevDir);
                        Directory.Move(prevDir, saveGamesDir);
                    }
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
                    try
                    {
                        CheckFolders("delete-staging", stagingDir);
                        RestoreFolders.DeleteTree(stagingDir);
                    }
                    catch { }
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
    }
}
