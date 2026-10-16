using CauldronServer.Services;

namespace CauldronServer.Tests;

public sealed class SaveWorldLayoutTests
{
    [Theory]
    [InlineData("savegame_0/0.sav")]
    [InlineData("savegame_0/1601500059.sav")]
    [InlineData("savegame_3/Backups/0.sav")]
    [InlineData("savegame_0.sav")]
    [InlineData("SaveGame_0/0.SAV")]
    public void AcceptsAWorldAtTheZipRoot(string entry)
    {
        Assert.True(SaveOrchestratorService.ContainsWorldSave([(entry, 5489L)]));
    }

    [Theory]
    [InlineData("SaveGames/savegame_0/0.sav")]
    [InlineData("savegame_0/notes.txt")]
    [InlineData("other.sav")]
    [InlineData("savegame_x/0.sav")]
    public void RejectsZipsWithoutAWorldAtTheRoot(string entry)
    {
        Assert.False(SaveOrchestratorService.ContainsWorldSave([(entry, 5489L)]));
    }

    [Fact]
    public void EmptySaveFilesDoNotCount()
    {
        Assert.False(SaveOrchestratorService.ContainsWorldSave([("savegame_0/0.sav", 0L)]));
    }
}
