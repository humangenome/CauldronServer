using System.IO.Compression;
using CauldronServer.Configuration;
using CauldronServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CauldronServer.Tests;

/// <summary>
/// Snapshots leave out the game's own savegame_N\Backups history, a restore keeps
/// that history in place, and a restore never turns auto-snapshots on when the
/// host has them off.
/// </summary>
public sealed class SnapshotBackupsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cauldron-snapbk-" + Guid.NewGuid().ToString("N"));
    private string SaveGames => Path.Combine(_root, "userdir", "Saved", "SaveGames");
    private string World => Path.Combine(SaveGames, "savegame_0");

    public SnapshotBackupsTests()
    {
        Directory.CreateDirectory(Path.Combine(World, "Backups", "2026-06-22_10.46.19"));
        Directory.CreateDirectory(Path.Combine(World, "Backups", "2026-10-15_20.00.00"));
        File.WriteAllText(Path.Combine(World, "Actors.sav"), "world-v1");
        File.WriteAllText(Path.Combine(World, "Slot.sav"), "slot");
        File.WriteAllText(Path.Combine(World, "329455617.sav"), "player");
        File.WriteAllText(Path.Combine(World, "Backups", "2026-06-22_10.46.19", "Actors.sav"), "june");
        File.WriteAllText(Path.Combine(World, "Backups", "2026-10-15_20.00.00", "Actors.sav"), "october");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private SaveOrchestratorService NewService(bool snapshotsEnabled)
    {
        var opts = Options.Create(new CauldronServerOptions
        {
            GameUserDir = Path.Combine(_root, "userdir"),
            SaveDir = Path.Combine(_root, "saves"),
            HmacKeyPath = Path.Combine(_root, "data", "hmac.key"),
            SnapshotsEnabled = snapshotsEnabled,
        });
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        return new SaveOrchestratorService(
            NullLogger<SaveOrchestratorService>.Instance,
            opts,
            new PipeServerState(NullLogger<PipeServerState>.Instance),
            new CauldronRestartCoordinator(NullLogger<CauldronRestartCoordinator>.Instance, opts));
    }

    [Theory]
    [InlineData("savegame_0/Backups", true)]
    [InlineData("savegame_0/Backups/2026-06-22_10.46.19/Actors.sav", true)]
    [InlineData(@"savegame_12\backups\x.sav", true)]
    [InlineData("savegame_0/Actors.sav", false)]
    [InlineData("savegame_0/BackupsOld/x.sav", false)]
    [InlineData("Backups/x.sav", false)]
    [InlineData("savegame_0/sub/Backups/x.sav", false)]
    public void KnowsTheGamesOwnBackupFolder(string path, bool expected)
    {
        Assert.Equal(expected, SaveOrchestratorService.IsGameBackupPath(path));
    }

    [Fact]
    public void SnapshotFilesLeaveOutBackups()
    {
        var names = SaveOrchestratorService.EnumerateSnapshotFiles(SaveGames)
            .Select(f => Path.GetRelativePath(SaveGames, f).Replace('\\', '/'))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "savegame_0/329455617.sav", "savegame_0/Actors.sav", "savegame_0/Slot.sav" }, names);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreKeepsBackupsAndOnlyArmsAutoSnapshotsWhenOn(bool snapshotsEnabled)
    {
        using var svc = NewService(snapshotsEnabled);
        await svc.StartAsync(CancellationToken.None);
        Assert.Equal(snapshotsEnabled, svc.AutoSnapshotsOn);

        var snap = await svc.SnapshotAsync("test");
        Assert.NotNull(snap);
        using (var zip = ZipFile.OpenRead(snap!.FilePath))
        {
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("Backups", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(zip.Entries, e => e.FullName == "savegame_0/Actors.sav");
        }

        File.WriteAllText(Path.Combine(World, "Actors.sav"), "world-v2");
        Assert.True(await svc.RestoreSnapshotAsync(snap.SnapshotId, "test"));

        Assert.Equal("world-v1", File.ReadAllText(Path.Combine(World, "Actors.sav")));
        Assert.Equal("june", File.ReadAllText(Path.Combine(World, "Backups", "2026-06-22_10.46.19", "Actors.sav")));
        Assert.Equal("october", File.ReadAllText(Path.Combine(World, "Backups", "2026-10-15_20.00.00", "Actors.sav")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "userdir", "Saved"), "SaveGames.old-*"));
        Assert.Equal(snapshotsEnabled, svc.AutoSnapshotsOn);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ImportedWorldGetsTheLiveBackupsAndItsOwnWin()
    {
        using var svc = NewService(snapshotsEnabled: false);
        await svc.StartAsync(CancellationToken.None);
        var incoming = Path.Combine(_root, "incoming.zip");
        using (var zip = ZipFile.Open(incoming, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
            Add("savegame_0/Actors.sav", "imported");
            Add("savegame_0/Slot.sav", "slot");
            Add("savegame_0/Backups/2026-10-15_20.00.00/Actors.sav", "incoming-october");
        }

        Assert.True(await svc.RestoreFromZipPathAsync(incoming, "test", "import"));

        Assert.Equal("imported", File.ReadAllText(Path.Combine(World, "Actors.sav")));
        Assert.Equal("june", File.ReadAllText(Path.Combine(World, "Backups", "2026-06-22_10.46.19", "Actors.sav")));
        Assert.Equal("incoming-october", File.ReadAllText(Path.Combine(World, "Backups", "2026-10-15_20.00.00", "Actors.sav")));
        Assert.False(svc.AutoSnapshotsOn);
    }

    [Fact]
    public void BackupsOfAWorldTheRestoreDoesNotHaveAreKeptBesideSaveGames()
    {
        var newTree = Path.Combine(_root, "userdir", "Saved", "SaveGames.new");
        Directory.CreateDirectory(newTree);
        File.WriteAllText(Path.Combine(newTree, "savegame_0.sav"), "old-layout");

        Assert.True(SaveOrchestratorService.KeepGameBackups(SaveGames, newTree, NullLogger.Instance));

        var kept = Directory.GetDirectories(Path.Combine(_root, "userdir", "Saved", "world-backups-kept"));
        Assert.Single(kept);
        Assert.StartsWith("savegame_0-", Path.GetFileName(kept[0]));
        Assert.Equal("june", File.ReadAllText(Path.Combine(kept[0], "2026-06-22_10.46.19", "Actors.sav")));
        Assert.False(Directory.Exists(Path.Combine(newTree, "savegame_0")));
    }
}
