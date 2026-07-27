using CauldronServer.Services;
using FluentAssertions;
using Xunit;

namespace CauldronServer.Tests;

public class WitchspireScriptPatchServiceTests
{
    [Fact]
    public void PatchSaveSubsystem_RewritesRegisterPlayerIdentityOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), "SaveSubsystem-" + Guid.NewGuid().ToString("N") + ".as");
        try
        {
            File.WriteAllText(path, string.Join("\r\n", new[]
            {
                "    bool RegisterPlayer(const APlayerController Player)",
                "    {",
                "        FSaveSystemIdentity& Identity = OnlineIdentities.FindOrAdd(Player);",
                "        Identity.Epic = f\"{Player.PlayerState.GetUniqueId().ToString().GetHash()}\";",
                "        if (Player.IsLocalController())",
                "        {",
                "            Identity.Steam = SteamUtil::GetSteamId();",
                "        }",
                "        return true;",
                "    }",
                "",
            }));

            WitchspireScriptPatchService.PatchSaveSubsystem(path)
                .Should().Be(WitchspireScriptPatchService.PatchResult.Patched);

            var patched = File.ReadAllText(path);
            patched.Should().Contain("CAULDRON_DIRECT_IP_SAVE_IDENTITY");
            patched.Should().Contain("const FString CauldronPlayerName = Player.PlayerState.GetPlayerName();");
            patched.Should().Contain("Identity.Epic = f\"cp_{CauldronPlayerName.GetHash()}\";");
            patched.Should().Contain("Identity.Steam = Identity.Epic;");

            WitchspireScriptPatchService.PatchSaveSubsystem(path)
                .Should().Be(WitchspireScriptPatchService.PatchResult.AlreadyPatched);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
