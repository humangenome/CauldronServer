using System.Text;
using CauldronServer.Configuration;
using CauldronServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CauldronServer.Tests;

public sealed class HostChatLogServiceTests
{
    private static string Hex(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(((int)c).ToString("x4"));
        return sb.ToString();
    }

    private sealed class Monitor : IOptionsMonitor<CauldronServerOptions>
    {
        public CauldronServerOptions CurrentValue { get; } = new() { GameUserDir = "" };
        public CauldronServerOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CauldronServerOptions, string?> listener) => null;
    }

    private static (HostChatLogService Svc, ChatService Chat) Make()
    {
        var chat = new ChatService(NullLogger<ChatService>.Instance, new Monitor());
        var svc = new HostChatLogService(NullLogger<HostChatLogService>.Instance, Options.Create(new CauldronServerOptions { GameUserDir = "" }), chat);
        return (svc, chat);
    }

    private static string Line(string from, string text) =>
        $"[2026.10.22-20.31.02:118][601]Cauldron: CAULDRON_CHAT: line kind=player from={Hex(from)} text={Hex(text)}";

    [Fact]
    public void A_player_line_decodes_with_awkward_characters()
    {
        var line = HostChatLogService.TryParseLine(Line("Mirabel", "hi é 漢 \"q\" 100%"));
        Assert.NotNull(line);
        Assert.Equal("Mirabel", line!.Sender);
        Assert.Equal("hi é 漢 \"q\" 100%", line.Text);
    }

    [Fact]
    public void A_line_goes_into_the_chat_history_as_a_player_message()
    {
        var (svc, chat) = Make();
        svc.ProcessLine(Line("Corvina", "anyone near the spire?"));
        var recent = chat.GetRecent(0, 10);
        Assert.Single(recent);
        Assert.Equal("Corvina", recent[0].Sender);
        Assert.Equal("player", recent[0].Channel);
        Assert.Equal("anyone near the spire?", recent[0].Msg);
        Assert.Equal(1, svc.Snapshot().Lines);
        Assert.Equal(new[] { "anyone near the spire?" }, svc.RecentTexts());
    }

    [Theory]
    [InlineData("CAULDRON_CHAT: line kind=player from=004 text=0068")]          // bad length
    [InlineData("CAULDRON_CHAT: line kind=player from=0041 text=00zz")]         // bad digit
    [InlineData("CAULDRON_CHAT: line kind=player from=0041 text=")]             // empty text
    [InlineData("CAULDRON_CHAT: line kind=notice from=0041 text=0068")]         // not a player line
    [InlineData("CAULDRON_HOST: roster count=1 players=Mirabel")]
    public void Anything_else_is_ignored(string line)
    {
        Assert.Null(HostChatLogService.TryParseLine(line));
    }

    [Fact]
    public void A_line_longer_than_the_host_allows_is_ignored()
    {
        Assert.Null(HostChatLogService.TryParseLine(Line("A", new string('x', 161))));
        Assert.NotNull(HostChatLogService.TryParseLine(Line("A", new string('x', 160))));
    }

    [Fact]
    public void Text_is_cleaned_again_here()
    {
        var line = HostChatLogService.TryParseLine(Line("Bad‮Name", "  <b>x</b>\u0007\tend​  "));
        Assert.NotNull(line);
        Assert.Equal("BadName", line!.Sender);
        Assert.Equal("‹b›x‹/b› end", line.Text);
    }

    [Fact]
    public void A_lone_surrogate_is_dropped_and_a_pair_kept()
    {
        Assert.Equal("ab", HostChatLogService.CleanText("a\uD83Db", 160));
        Assert.Equal("a😀b", HostChatLogService.CleanText("a😀b", 160));
    }

    [Fact]
    public void The_audit_line_is_read()
    {
        var at = DateTimeOffset.UtcNow;
        var a = HostChatLogService.TryParseAudit("Cauldron: CAULDRON_CHAT: audit players=3 relays=2 ready=2 saveMarker=false", at);
        Assert.NotNull(a);
        Assert.Equal(3, a!.Players);
        Assert.Equal(2, a.Relays);
        Assert.Equal(2, a.Ready);
        Assert.False(a.SaveMarker);
        Assert.True(HostChatLogService.TryParseAudit("CAULDRON_CHAT: audit players=1 relays=1 ready=0 saveMarker=True", at)!.SaveMarker);
    }

    [Fact]
    public void The_host_on_line_turns_the_status_on()
    {
        var (svc, _) = Make();
        Assert.Equal(0, svc.Snapshot().HostProto);
        svc.ProcessLine("Cauldron: CAULDRON_CHAT: host on proto=1");
        Assert.Equal(1, svc.Snapshot().HostProto);
    }

    [Fact]
    public void Chataudit_reply_reads_plainly()
    {
        var now = DateTimeOffset.UtcNow;
        var audit = new HostChatLogService.ChatAudit(2, 2, 1, false, now.AddSeconds(-30));
        var clean = new ChatSaveLeakCheck.Result(4, 3, Array.Empty<string>(), true);
        var text = RconHostedService.FormatChatAudit(audit, 12, 1, clean, now);
        Assert.Equal(
            "chat: on (protocol 1), 12 player lines read\n" +
            "relays: 2 for 2 players, 1 with chat up, save marker none (30s ago)\n" +
            "save check: clean (3 recent lines searched in 4 save files)", text);

        var off = RconHostedService.FormatChatAudit(null, 0, 0, new ChatSaveLeakCheck.Result(0, 0, Array.Empty<string>(), false), now);
        Assert.StartsWith("chat: off", off);
        Assert.Contains("save check: no world save yet", off);

        var leak = RconHostedService.FormatChatAudit(audit, 1, 1, new ChatSaveLeakCheck.Result(2, 1, new[] { "savegame_0/Actors.sav" }, true), now);
        Assert.Contains("save check: FOUND chat text in savegame_0/Actors.sav", leak);
    }

    [Fact]
    public void Chat_reply_lists_recent_lines()
    {
        var msgs = new[]
        {
            new ChatService.ChatMessage { Ts = 1000, Sender = "Mirabel", Channel = "player", Msg = "hello" },
            new ChatService.ChatMessage { Ts = 1050, Sender = "Server", Channel = "system", Msg = "restart soon" },
        };
        Assert.Equal("1m ago  Mirabel: hello\n20s ago  [system] restart soon", RconHostedService.FormatChat(msgs, 1070));
        Assert.Equal("no chat yet", RconHostedService.FormatChat(Array.Empty<ChatService.ChatMessage>(), 1070));
    }
}
