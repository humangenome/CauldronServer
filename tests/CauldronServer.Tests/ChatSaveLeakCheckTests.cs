using System.Text;
using CauldronServer.Services;

namespace CauldronServer.Tests;

public sealed class ChatSaveLeakCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cauldron-leak-" + Guid.NewGuid().ToString("N"));

    public ChatSaveLeakCheckTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "savegame_0"));
        File.WriteAllBytes(Path.Combine(_dir, "savegame_0", "Actors.sav"), Encoding.Unicode.GetBytes("L_StarterIsland chest Sign text"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Clean_when_no_recent_line_is_in_the_save()
    {
        var r = ChatSaveLeakCheck.Run(_dir, new[] { "meet at the spire tonight", "short" });
        Assert.True(r.SaveFolderFound);
        Assert.Equal(1, r.FilesScanned);
        Assert.Equal(1, r.TextsChecked);
        Assert.Empty(r.Hits);
    }

    [Fact]
    public void Finds_a_line_stored_as_utf16_or_utf8()
    {
        File.WriteAllBytes(Path.Combine(_dir, "savegame_0", "Player_cp_1.sav"), Encoding.UTF8.GetBytes("xx meet at the spire tonight xx"));
        var r = ChatSaveLeakCheck.Run(_dir, new[] { "meet at the spire tonight", "chest Sign text" });
        Assert.Equal(2, r.Hits.Count);
    }

    [Fact]
    public void No_save_folder_says_so()
    {
        var r = ChatSaveLeakCheck.Run(Path.Combine(_dir, "missing"), new[] { "meet at the spire tonight" });
        Assert.False(r.SaveFolderFound);
    }
}
