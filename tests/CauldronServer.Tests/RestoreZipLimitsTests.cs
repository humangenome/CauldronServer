using System.IO.Compression;
using System.Text;
using CauldronServer.Configuration;
using CauldronServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CauldronServer.Tests;

/// <summary>
/// A world zip is capped on upload (MaxUploadBytes) but used to be unpacked with no limit, so a small
/// zip could unpack to far more and fill the disk every server on the machine shares. A zip with no
/// world in it also used to stop the game before it was refused. These zips are tiny and unpack big;
/// the caps here are small so the tests stay fast.
/// </summary>
public sealed class RestoreZipLimitsTests : IDisposable
{
    private const long KiB = 1024;
    private const string WorldEntry = "savegame_0/Actors.sav";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cauldron-zipcap-" + Guid.NewGuid().ToString("N"));
    private readonly CauldronServerOptions _options;
    private readonly CauldronRestartCoordinator _coordinator;
    private readonly SaveOrchestratorService _service;

    private string SavedDir => Path.Combine(_root, "userdir", "Saved");
    private string World => Path.Combine(SavedDir, "SaveGames", "savegame_0");

    public RestoreZipLimitsTests()
    {
        Directory.CreateDirectory(World);
        File.WriteAllText(Path.Combine(World, "Actors.sav"), "live world");
        File.WriteAllText(Path.Combine(World, "Slot.sav"), "slot");
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        _options = new CauldronServerOptions
        {
            GameUserDir = Path.Combine(_root, "userdir"),
            SaveDir = Path.Combine(_root, "saves"),
            HmacKeyPath = Path.Combine(_root, "data", "hmac.key"),
            SnapshotsEnabled = false,
            MaxRestoreUnpackedBytes = 1024 * KiB,
            MaxRestoreEntries = 20,
            MaxRestoreEntryBytes = 512 * KiB,
            RestoreFreeSpaceFloorBytes = 0,
        };
        var opts = Options.Create(_options);
        _coordinator = new CauldronRestartCoordinator(NullLogger<CauldronRestartCoordinator>.Instance, opts);
        _service = new SaveOrchestratorService(
            NullLogger<SaveOrchestratorService>.Instance,
            opts,
            new PipeServerState(NullLogger<PipeServerState>.Instance),
            _coordinator);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Zip(string name, bool withWorld, params (string Entry, long ZeroBytes)[] extra)
    {
        var zip = Path.Combine(_root, name);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        if (withWorld)
        {
            using var w = new StreamWriter(archive.CreateEntry(WorldEntry).Open());
            w.Write("restored world");
        }
        var zeros = new byte[64 * KiB];
        foreach (var (entry, bytes) in extra)
        {
            using var s = archive.CreateEntry(entry, CompressionLevel.SmallestSize).Open();
            for (long left = bytes; left > 0; left -= zeros.Length) s.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
        }
        return zip;
    }

    private void LiveWorldUntouched()
    {
        Assert.Equal("live world", File.ReadAllText(Path.Combine(World, "Actors.sav")));
        Assert.Empty(Directory.GetDirectories(SavedDir, "SaveGames.incoming-*"));
        Assert.Empty(Directory.GetDirectories(SavedDir, "SaveGames.old-*"));
        Assert.Empty(_service.ListSnapshots());
    }

    [Fact]
    public async Task A_tiny_zip_that_unpacks_huge_is_refused_before_the_game_is_stopped()
    {
        var zip = Zip("bomb.zip", withWorld: true, ("savegame_0/a.bin", 400 * KiB), ("savegame_0/b.bin", 400 * KiB),
            ("savegame_0/c.bin", 400 * KiB));
        Assert.True(new FileInfo(zip).Length < 16 * KiB, "the zip itself is tiny");

        Assert.False(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(0, _coordinator.KillRequests);
        LiveWorldUntouched();
    }

    [Fact]
    public async Task Too_many_entries_are_refused_before_the_game_is_stopped()
    {
        var many = Enumerable.Range(0, 25).Select(i => ($"savegame_0/f{i}.bin", 1L)).ToArray();
        var zip = Zip("many.zip", withWorld: true, many);

        Assert.False(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(0, _coordinator.KillRequests);
        LiveWorldUntouched();
    }

    [Fact]
    public async Task One_oversized_entry_is_refused_before_the_game_is_stopped()
    {
        var zip = Zip("one-big.zip", withWorld: true, ("savegame_0/big.bin", 600 * KiB));

        Assert.False(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(0, _coordinator.KillRequests);
        LiveWorldUntouched();
    }

    [Fact]
    public async Task An_unpack_that_would_leave_too_little_free_space_is_refused_before_the_game_is_stopped()
    {
        _options.RestoreFreeSpaceFloorBytes = 512 * KiB;
        _service.FreeSpaceProbe = _ => 800 * KiB;
        var zip = Zip("fits-caps.zip", withWorld: true, ("savegame_0/x.bin", 400 * KiB));

        Assert.False(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(0, _coordinator.KillRequests);
        LiveWorldUntouched();
    }

    [Fact]
    public async Task A_zip_with_no_world_is_refused_before_the_game_is_stopped()
    {
        // The customer zipped the wrong folder: before 2026-11-15 this stopped the world, then refused.
        var zip = Zip("no-world.zip", withWorld: false, ("Config/Engine.ini", 2 * KiB));

        Assert.False(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(0, _coordinator.KillRequests);
        LiveWorldUntouched();
    }

    [Fact]
    public async Task A_zip_that_cannot_be_opened_is_refused_before_the_game_is_stopped()
    {
        var zip = Path.Combine(_root, "not-a-zip.zip");
        File.WriteAllText(zip, "this is not a zip");

        Assert.False(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(0, _coordinator.KillRequests);
        LiveWorldUntouched();
    }

    [Fact]
    public async Task A_world_inside_the_caps_still_restores()
    {
        _options.RestoreFreeSpaceFloorBytes = 512 * KiB;
        _service.FreeSpaceProbe = _ => 100 * 1024 * KiB;
        var zip = Zip("ok.zip", withWorld: true, ("savegame_0/x.bin", 400 * KiB),
            ("savegame_0/Backups/2026-11-14_10.33.00/Actors.sav", 2 * KiB));

        Assert.True(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(1, _coordinator.KillRequests);
        Assert.Equal("restored world", File.ReadAllText(Path.Combine(World, "Actors.sav")));
        Assert.Equal(400 * KiB, new FileInfo(Path.Combine(World, "x.bin")).Length);
        Assert.Equal(2 * KiB, new FileInfo(Path.Combine(World, "Backups", "2026-11-14_10.33.00", "Actors.sav")).Length);
    }

    [Theory]
    [InlineData(CompressionLevel.SmallestSize)]
    [InlineData(CompressionLevel.NoCompression)]
    public async Task A_zip_that_states_small_sizes_never_writes_past_what_it_stated(CompressionLevel level)
    {
        var zip = Path.Combine(_root, "liar.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry(WorldEntry).Open())) w.Write("restored world");
            using var s = archive.CreateEntry("savegame_0/liar.bin", level).Open();
            s.Write(new byte[3 * 1024 * KiB]);
        }
        StateSize(zip, "savegame_0/liar.bin", 1000);
        using (var probe = ZipFile.OpenRead(zip))
            Assert.Equal(1000, probe.GetEntry("savegame_0/liar.bin")!.Length);

        // Refused, or unpacked with that entry cut at what it stated: never 3 MB on disk.
        var ok = await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore");

        var liar = Path.Combine(World, "liar.bin");
        if (ok) Assert.True(new FileInfo(liar).Length <= 1000);
        else Assert.Equal("live world", File.ReadAllText(Path.Combine(World, "Actors.sav")));
        var written = Directory.GetFiles(SavedDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.True(written < 64 * KiB, $"nothing near the 3 MB the zip holds was written ({written} bytes)");
    }

    [Fact]
    public void The_unpack_writes_nothing_when_the_entries_state_more_than_the_cap()
    {
        // The unpack runs the same check first, so a call that skipped the service's own check
        // (or a cap lowered between the two) still writes nothing.
        var zip = Zip("two-mid.zip", withWorld: true, ("savegame_0/a.bin", 300 * KiB), ("savegame_0/b.bin", 300 * KiB));
        var dest = Path.Combine(_root, "unpack");
        Directory.CreateDirectory(dest);
        var caps = new RestoreZipLimits.Caps(400 * KiB, 20, 512 * KiB, 0);

        Assert.Throws<InvalidDataException>(() => RestoreZipLimits.ExtractWithinCaps(zip, dest, caps, null));

        var written = Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.Equal(0, written);
    }

    [Theory]
    [InlineData("savegame_0/Actors.sav:hidden")]
    [InlineData("savegame_0/what?.sav")]
    [InlineData("savegame_0/a|b.sav")]
    public async Task An_entry_name_Windows_cannot_store_is_refused_before_the_game_is_stopped(string name)
    {
        var zip = Zip("odd-name.zip", withWorld: true, (name, 1 * KiB));

        Assert.False(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(0, _coordinator.KillRequests);
        LiveWorldUntouched();
    }

    [Theory]
    [InlineData("savegame_0/Backups/2026-11-14_10.33.00/Actors.sav", false)]
    [InlineData("savegame_0/329455617.sav", false)]
    [InlineData("savegame_0/Actors.sav:hidden", true)]
    [InlineData("savegame_0/x\u0001.sav", true)]
    public void Knows_the_names_Windows_cannot_store(string name, bool expected)
    {
        Assert.Equal(expected, RestoreZipLimits.HasNameWindowsCannotStore(System.Text.RegularExpressions.Regex.Unescape(name)));
    }

    [Fact]
    public void An_entry_that_leaves_the_folder_is_refused()
    {
        var zip = Path.Combine(_root, "slip.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(archive.CreateEntry("../outside.txt").Open())) w.Write("x");
        var dest = Path.Combine(_root, "unpack-slip");
        Directory.CreateDirectory(dest);
        var caps = new RestoreZipLimits.Caps(1024 * KiB, 20, 512 * KiB, 0);

        // Since 2026-11-17 the unpack's own first check (the same one the restore runs before the stop) refuses
        // it; the resolved-path check inside the unpack stays as a second line.
        var ex = Assert.Throws<InvalidDataException>(() => RestoreZipLimits.ExtractWithinCaps(zip, dest, caps, null));
        Assert.Contains("leaves the folder", ex.Message);

        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    // ---- 2026-11-17 (Security read of 0.1.27): paths, Windows names and false sizes are refused BEFORE the stop ----

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("savegame_0/../../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("\\outside.txt")]
    public async Task An_entry_whose_path_leaves_the_folder_is_refused_before_the_game_is_stopped(string name)
    {
        var zip = Zip("slip-early.zip", withWorld: true, (name, 1 * KiB));

        Assert.False(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(0, _coordinator.KillRequests);
        LiveWorldUntouched();
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(SavedDir, "outside.txt")));
    }

    [Theory]
    [InlineData("savegame_0/Actors.sav", false)]
    [InlineData("./savegame_0/Actors.sav", false)]
    [InlineData("savegame_0/../savegame_1/Actors.sav", false)]
    [InlineData("savegame_0/", false)]
    [InlineData("savegame_0/../../x.sav", true)]
    [InlineData("..", true)]
    [InlineData("C:x.sav", true)]
    [InlineData("\\\\server\\share\\x.sav", true)]
    public void Knows_the_paths_that_leave_the_folder(string name, bool expected)
    {
        Assert.Equal(expected, RestoreZipLimits.LeavesTheFolder(name));
    }

    [Theory]
    [InlineData(CompressionLevel.SmallestSize, 1000)]
    [InlineData(CompressionLevel.NoCompression, 1000)]
    [InlineData(CompressionLevel.SmallestSize, 400 * 1024)]
    [InlineData(CompressionLevel.NoCompression, 400 * 1024)]
    public async Task A_zip_whose_data_is_not_the_size_it_states_is_refused_before_the_game_is_stopped(CompressionLevel level, int stated)
    {
        // The entry holds 300 KiB. Stating 1000 bytes is the bomb shape (it used to stop the game, then fail in
        // the unpack); stating 400 KiB is a short entry (it used to restore a cut file).
        var zip = Path.Combine(_root, "false-size.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry(WorldEntry).Open())) w.Write("restored world");
            using var s = archive.CreateEntry("savegame_0/held.bin", level).Open();
            s.Write(new byte[300 * KiB]);
        }
        StateSize(zip, "savegame_0/held.bin", (uint)stated);

        Assert.False(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(0, _coordinator.KillRequests);
        LiveWorldUntouched();
    }

    [Fact]
    public async Task A_damaged_entry_is_refused_before_the_game_is_stopped()
    {
        // One byte of a stored entry's data changed after the zip was made: right size, wrong checksum.
        var zip = Path.Combine(_root, "damaged.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry(WorldEntry).Open())) w.Write("restored world");
            using var s = archive.CreateEntry("savegame_0/data.bin", CompressionLevel.NoCompression).Open();
            s.Write(Enumerable.Repeat((byte)'A', 64 * (int)KiB).ToArray());
        }
        var bytes = File.ReadAllBytes(zip);
        var at = bytes.AsSpan().IndexOf(Enumerable.Repeat((byte)'A', 1024).ToArray());
        Assert.True(at > 0, "the stored data is in the zip");
        bytes[at + 500] = (byte)'B';
        File.WriteAllBytes(zip, bytes);

        Assert.False(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(0, _coordinator.KillRequests);
        LiveWorldUntouched();
    }

    [Theory]
    [InlineData("savegame_0/CON")]
    [InlineData("savegame_0/nul.sav")]
    [InlineData("savegame_0/Com1.txt")]
    [InlineData("savegame_0/LPT9")]
    [InlineData("savegame_0/con .sav")]
    [InlineData("AUX/x.sav")]
    [InlineData("savegame_0/Actors.sav.")]
    [InlineData("savegame_0/Actors.sav ")]
    [InlineData("savegame_0/dir./x.sav")]
    [InlineData("savegame_0/dir /x.sav")]
    public async Task An_entry_name_Windows_treats_specially_is_refused_before_the_game_is_stopped(string name)
    {
        var zip = Zip("device-name.zip", withWorld: true, (name, 1 * KiB));

        Assert.False(await _service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore"));

        Assert.Equal(0, _coordinator.KillRequests);
        LiveWorldUntouched();
    }

    [Theory]
    [InlineData("savegame_0/CONSOLE.sav", false)]
    [InlineData("savegame_0/COM10", false)]
    [InlineData("savegame_0/con_x.sav", false)]
    [InlineData("savegame_0/.hidden", false)]
    [InlineData("savegame_0/Backups/2026-11-14_10.33.00/Actors.sav", false)]
    [InlineData("savegame_0/../savegame_0/Actors.sav", false)]
    [InlineData("savegame_0/PRN.sav", true)]
    [InlineData("savegame_0/conout$", true)]
    [InlineData("savegame_0/COM\u00B9.txt", true)]
    [InlineData("savegame_0\\LPT1\\x.sav", true)]
    [InlineData("savegame_0/x.sav.", true)]
    public void Knows_the_names_Windows_treats_specially(string name, bool expected)
    {
        Assert.Equal(expected, RestoreZipLimits.HasNameWindowsTreatsSpecially(name));
    }

    // ---- 2026-11-17: the free-space floor measures the disk a linked server folder really points to ----

    [Fact]
    public void The_free_space_check_measures_the_disk_a_linked_folder_points_to()
    {
        // A server folder kept on a second drive is a link (C: to D:, 228723 on 11/15). The floor must measure the
        // drive the files really land on, not the drive the path is written on. Here "second-drive" stands in for
        // the second drive's mount and answers 111; anything else answers 999.
        var real = Path.Combine(_root, "second-drive");
        var link = Path.Combine(_root, "server-folder");
        Directory.CreateDirectory(Path.Combine(real, "userdir", "Saved"));
        MakeDirectoryLink(link, real);
        var realMount = RestoreZipLimits.RealPath(real);
        var mounts = new[] { Path.GetPathRoot(Path.GetFullPath(_root))!, realMount };
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        long? Measure(string m) => string.Equals(m, realMount, cmp) ? 111 : 999;

        Assert.Equal(111, RestoreZipLimits.FreeBytesOf(Path.Combine(link, "userdir", "Saved"), mounts, Measure));
        Assert.Equal(111, RestoreZipLimits.FreeBytesOf(Path.Combine(real, "userdir", "Saved"), mounts, Measure));

        // A link to that link, and a folder that does not exist yet under it.
        var link2 = Path.Combine(_root, "link-to-link");
        MakeDirectoryLink(link2, link);
        Assert.Equal(111, RestoreZipLimits.FreeBytesOf(Path.Combine(link2, "userdir", "Saved", "not-yet"), mounts, Measure));

        // A folder that is not linked anywhere stays on the first drive.
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        Assert.Equal(999, RestoreZipLimits.FreeBytesOf(Path.Combine(_root, "plain"), mounts, Measure));
    }

    [Fact]
    public void Picks_the_longest_mount_that_holds_the_path()
    {
        var sep = Path.DirectorySeparatorChar;
        var top = OperatingSystem.IsWindows() ? "C:" + sep : sep.ToString();
        var data = top + "data";
        var mounts = new[] { top, data, data + "2" };
        Assert.Equal(data, RestoreZipLimits.LongestMountOf(data + sep + "x" + sep + "Saved", mounts));
        Assert.Equal(top, RestoreZipLimits.LongestMountOf(data + "x" + sep + "Saved", mounts));
    }

    // A directory link: a symbolic link, or on Windows without that right a junction (mklink /J needs no right).
    private static void MakeDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
        }
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(10_000);
        Assert.True(Directory.Exists(link), "could not make a directory link for the test");
    }

    [Fact]
    public void The_defaults_fit_a_real_Witchspire_save_folder_with_its_Backups_history()
    {
        // A world that has run for about six weeks: 3,841 game copies, 125,708 files, 1.9 GB (measured 2026-11-14).
        var d = new CauldronServerOptions();
        Assert.True(d.MaxRestoreEntries > 125_708);
        Assert.True(d.MaxRestoreUnpackedBytes > 1_900L * 1024 * 1024);
        Assert.True(d.MaxRestoreUnpackedBytes >= d.MaxUploadBytes);
    }

    // Rewrites the uncompressed size the zip states for one entry, in its local header and its
    // central directory record, leaving the compressed data as it is.
    private static void StateSize(string zip, string entry, uint size)
    {
        var bytes = File.ReadAllBytes(zip);
        var name = Encoding.UTF8.GetBytes(entry);
        var patched = 0;
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            var sig = BitConverter.ToUInt32(bytes, i);
            int nameAt, sizeAt;
            if (sig == 0x04034b50) { nameAt = i + 30; sizeAt = i + 22; }
            else if (sig == 0x02014b50) { nameAt = i + 46; sizeAt = i + 24; }
            else continue;
            if (nameAt + name.Length > bytes.Length) continue;
            if (!bytes.AsSpan(nameAt, name.Length).SequenceEqual(name)) continue;
            BitConverter.GetBytes(size).CopyTo(bytes, sizeAt);
            patched++;
        }
        Assert.Equal(2, patched);
        File.WriteAllBytes(zip, bytes);
    }
}
