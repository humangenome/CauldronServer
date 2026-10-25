using CauldronServer.Services;

namespace CauldronServer.Tests;

public sealed class ChatModStagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cauldron-chatstage-" + Guid.NewGuid().ToString("N"));
    private string Package => Path.Combine(_root, "pkg");
    private string Project => Path.Combine(_root, "game", "Hercules");
    private string Dest => ChatModStager.Destination(Project);
    private readonly List<string> _log = new();

    public ChatModStagerTests()
    {
        var src = Path.Combine(Package, ChatModStager.PackageDir);
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, ChatModStager.SharedScript), "// shared\nclass ACauldronChatRelayV1 : AActor {}\n");
        File.WriteAllText(Path.Combine(src, ChatModStager.HostScript), "// host\n");
        File.WriteAllText(Path.Combine(src, ChatModStager.NamesFile),
            "# names\nboth\tclass\tUserInterface/HUD/HercHUD.as\tAHercHUD\nhost\tbind\t-\tUEMSActorSaveInterface\n");
        Directory.CreateDirectory(Path.Combine(Project, "Script", "UserInterface", "HUD"));
        File.WriteAllText(Path.Combine(Project, "Script", "UserInterface", "HUD", "HercHUD.as"), "class AHercHUD : AHUD\n{\n};\n");
        File.WriteAllText(Path.Combine(Project, "Script", "Binds.Cache"), "..UEMSActorSaveInterface..");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Places_both_scripts_when_on_and_the_game_has_every_name()
    {
        Assert.True(ChatModStager.Stage(Package, Project, enabled: true, _log.Add));
        Assert.True(File.Exists(Path.Combine(Dest, ChatModStager.SharedScript)));
        Assert.True(File.Exists(Path.Combine(Dest, ChatModStager.HostScript)));
        Assert.False(File.Exists(Path.Combine(Dest, ChatModStager.NamesFile)));
    }

    [Fact]
    public void Removes_them_when_switched_off()
    {
        ChatModStager.Stage(Package, Project, enabled: true, _log.Add);
        Assert.False(ChatModStager.Stage(Package, Project, enabled: false, _log.Add));
        Assert.Empty(Directory.GetFiles(Dest, "*.as"));
        Assert.Contains(_log, l => l.Contains("off", StringComparison.Ordinal));
    }

    [Fact]
    public void Removes_them_and_names_what_is_missing_after_a_game_update()
    {
        ChatModStager.Stage(Package, Project, enabled: true, _log.Add);
        File.WriteAllText(Path.Combine(Project, "Script", "UserInterface", "HUD", "HercHUD.as"), "class AHercHUD2 : AHUD\n{\n};\n");
        Assert.False(ChatModStager.Stage(Package, Project, enabled: true, _log.Add));
        Assert.Empty(Directory.GetFiles(Dest, "*.as"));
        Assert.Contains(_log, l => l.Contains("AHercHUD", StringComparison.Ordinal) && l.Contains("no longer has", StringComparison.Ordinal));
    }

    [Fact]
    public void An_incomplete_package_places_nothing()
    {
        File.Delete(Path.Combine(Package, ChatModStager.PackageDir, ChatModStager.NamesFile));
        Assert.False(ChatModStager.Stage(Package, Project, enabled: true, _log.Add));
        Assert.False(Directory.Exists(Dest) && Directory.GetFiles(Dest, "*.as").Length > 0);
    }

    [Fact]
    public void A_stale_script_in_the_chat_folder_is_removed()
    {
        Directory.CreateDirectory(Dest);
        File.WriteAllText(Path.Combine(Dest, "Old.as"), "broken");
        Assert.True(ChatModStager.Stage(Package, Project, enabled: true, _log.Add));
        Assert.False(File.Exists(Path.Combine(Dest, "Old.as")));
    }
}
