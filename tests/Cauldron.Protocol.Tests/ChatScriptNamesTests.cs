using System.Text;
using Cauldron.Protocol;
using FluentAssertions;
using Xunit;

namespace Cauldron.Protocol.Tests;

public class ChatScriptNamesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cauldron-chatnames-" + Guid.NewGuid().ToString("N"));

    public ChatScriptNamesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "UserInterface", "HUD"));
        File.WriteAllText(Path.Combine(_root, "UserInterface", "HUD", "HercHUD.as"),
            "UCLASS(Abstract)\r\nclass AHercHUD : AHUD\r\n{\r\n    UPrimaryGameLayout GameLayout;\r\n    void ClearFocus() const\r\n    {\r\n    }\r\n};\r\n" +
            "enum EUILayer\r\n{\r\n    Game,\r\n    GameMenu,\r\n    GameModal\r\n};\r\n");
        File.WriteAllBytes(Path.Combine(_root, "Binds.Cache"), Encoding.ASCII.GetBytes("\u0001\u0002GetCharacterAsNumber\u0000UHercActivatableWidget\u0000"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Manifest(params string[] lines) => "# comment\n\n" + string.Join("\n", lines) + "\n";

    [Fact]
    public void Every_kind_holds_when_the_game_has_the_name()
    {
        var entries = ChatScriptNames.Parse(Manifest(
            "both\tclass\tUserInterface/HUD/HercHUD.as\tAHercHUD",
            "both\ttext\tUserInterface/HUD/HercHUD.as\tUPrimaryGameLayout   GameLayout;",
            "both\ttext\tUserInterface/HUD/HercHUD.as\tvoid ClearFocus()",
            "both\tenum\tUserInterface/HUD/HercHUD.as\tEUILayer GameMenu",
            "both\tbind\t-\tGetCharacterAsNumber",
            "host\tbind\t-\tUHercActivatableWidget"));
        ChatScriptNames.Missing(_root, entries, includeHost: true).Should().BeEmpty();
    }

    [Fact]
    public void A_renamed_or_removed_name_is_reported()
    {
        var entries = ChatScriptNames.Parse(Manifest(
            "both\tclass\tUserInterface/HUD/HercHUD.as\tAHercHU",
            "both\tclass\tUserInterface/HUD/HercHUD.as\tAHercHUDX",
            "both\tenum\tUserInterface/HUD/HercHUD.as\tEUILayer Menu",
            "both\ttext\tUserInterface/HUD/HercHUD.as\tvoid ClearFocusNow()",
            "both\tbind\t-\tNoSuchBinding",
            "both\ttext\tUserInterface/Gone/Gone.as\tanything"));
        var missing = ChatScriptNames.Missing(_root, entries, includeHost: false);
        missing.Should().HaveCount(6);
    }

    [Fact]
    public void Host_entries_are_checked_only_for_a_host()
    {
        var entries = ChatScriptNames.Parse(Manifest(
            "both\tbind\t-\tGetCharacterAsNumber",
            "host\tbind\t-\tUEMSActorSaveInterface"));
        ChatScriptNames.Missing(_root, entries, includeHost: false).Should().BeEmpty();
        ChatScriptNames.Missing(_root, entries, includeHost: true).Should().ContainSingle()
            .Which.Name.Should().Be("UEMSActorSaveInterface");
    }

    [Fact]
    public void A_game_folder_without_scripts_misses_everything()
    {
        var entries = ChatScriptNames.Parse(Manifest(
            "both\tclass\tUserInterface/HUD/HercHUD.as\tAHercHUD",
            "both\tbind\t-\tGetCharacterAsNumber"));
        ChatScriptNames.Missing(Path.Combine(_root, "nope"), entries, includeHost: true).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("both\tclass\tA.as")]
    [InlineData("game\tclass\tA.as\tX")]
    [InlineData("both\tfunc\tA.as\tX")]
    [InlineData("both\tenum\tA.as\tOnlyEnum")]
    [InlineData("both\ttext\t../A.as\tX")]
    public void A_line_it_does_not_understand_is_refused(string line)
    {
        var act = () => ChatScriptNames.Parse(Manifest(line));
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void An_empty_manifest_is_refused()
    {
        var act = () => ChatScriptNames.Parse("# only comments\n");
        act.Should().Throw<FormatException>();
    }
}
