using CauldronServer.Services;
using FluentAssertions;
using Xunit;

namespace CauldronServer.Tests;

public class WitchspireScriptPatchServiceTests
{
    private static readonly string[] RegisterPlayerBlock =
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
    };

    private static string TempFile(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "SaveSubsystem-" + Guid.NewGuid().ToString("N") + ".as");
        File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void PatchSaveSubsystem_RewritesRegisterPlayerIdentityOnce(string nl)
    {
        var path = TempFile(string.Join(nl, RegisterPlayerBlock));
        try
        {
            WitchspireScriptPatchService.PatchSaveSubsystem(path)
                .Should().Be(WitchspireScriptPatchService.PatchResult.Patched);

            var patched = File.ReadAllText(path);
            patched.Should().Contain("CAULDRON_DIRECT_IP_SAVE_IDENTITY_V2:");
            patched.Should().Contain("const FString CauldronPlayerName = Player.PlayerState.GetPlayerName();");
            patched.Should().Contain("Identity.Epic = f\"cp_{CauldronPlayerName.Left(20).GetHash()}\";");
            patched.Should().Contain("Identity.Steam = Identity.Epic;");
            patched.Should().NotContain("cp_{CauldronPlayerName.GetHash()}");

            WitchspireScriptPatchService.PatchSaveSubsystem(path)
                .Should().Be(WitchspireScriptPatchService.PatchResult.AlreadyPatched);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void PatchSaveSubsystem_UpgradesTheFirstVersionOfThePatch()
    {
        var v1 = string.Join("\r\n", new[]
        {
            "        Identity.Epic = f\"{Player.PlayerState.GetUniqueId().ToString().GetHash()}\";",
            "        // CAULDRON_DIRECT_IP_SAVE_IDENTITY: direct-IP joins run under Null OSS, so every remote",
            "        const FString CauldronPlayerName = Player.PlayerState.GetPlayerName();",
            "        if (!Player.IsLocalController() && !CauldronPlayerName.IsEmpty())",
            "        {",
            "            Identity.Epic = f\"cp_{CauldronPlayerName.GetHash()}\";",
            "            Identity.Steam = Identity.Epic;",
            "            return true;",
            "        }",
            "        if (Player.IsLocalController())",
            "",
        });
        var path = TempFile(v1);
        try
        {
            WitchspireScriptPatchService.PatchSaveSubsystem(path)
                .Should().Be(WitchspireScriptPatchService.PatchResult.Upgraded);

            var upgraded = File.ReadAllText(path);
            upgraded.Should().Contain("CAULDRON_DIRECT_IP_SAVE_IDENTITY_V2:");
            upgraded.Should().Contain("cp_{CauldronPlayerName.Left(20).GetHash()}");
            upgraded.Should().NotContain("cp_{CauldronPlayerName.GetHash()}");

            WitchspireScriptPatchService.PatchSaveSubsystem(path)
                .Should().Be(WitchspireScriptPatchService.PatchResult.AlreadyPatched);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void PatchSaveSubsystem_LeavesAnUnrecognisedFileAlone()
    {
        var original = "class USaveSubsystem { }\n";
        var path = TempFile(original);
        try
        {
            WitchspireScriptPatchService.PatchSaveSubsystem(path)
                .Should().Be(WitchspireScriptPatchService.PatchResult.NotFound);
            File.ReadAllText(path).Should().Be(original);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
