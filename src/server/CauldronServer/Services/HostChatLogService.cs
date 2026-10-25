using System.Text;
using System.Text.RegularExpressions;
using CauldronServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CauldronServer.Services;

/// <summary>
/// Reads in-game chat out of the Witchspire host log (<c>{GameUserDir}\ws-ue.log</c>) and puts each
/// player line into the chat-v1 history (<see cref="ChatService"/>), so <c>/api/v1/chat/recent</c>
/// and RCON show what players said.
///
/// The chat host script (CauldronChatHost.as with CauldronChat.as) writes one line per message, with
/// the sender and the text hex-encoded (4 hex digits per UTF-16 code unit), so a chat message can
/// never pose as another log line:
///   CAULDRON_CHAT: line kind=player from=0041006c... text=0068006900...
/// and an audit line every minute:
///   CAULDRON_CHAT: audit players=2 relays=2 ready=2 saveMarker=false
/// The host already cleaned and rate-limited the line; it is cleaned again here (defence in depth).
/// </summary>
public sealed class HostChatLogService : BackgroundService
{
    private static readonly Regex LineRegex = new(
        @"CAULDRON_CHAT:\s+line\s+kind=(?<kind>player)\s+from=(?<from>[0-9a-f]*)\s+text=(?<text>[0-9a-f]+)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex AuditRegex = new(
        @"CAULDRON_CHAT:\s+audit\s+players=(?<players>\d+)\s+relays=(?<relays>\d+)\s+ready=(?<ready>\d+)\s+saveMarker=(?<marker>true|false)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HostOnRegex = new(@"CAULDRON_CHAT:\s+host on proto=(?<proto>\d+)", RegexOptions.Compiled);

    internal const int MaxTextChars = 160;
    internal const int MaxNameChars = 32;

    private readonly ILogger<HostChatLogService> _log;
    private readonly CauldronServerOptions _opts;
    private readonly ChatService _chat;
    private readonly object _lock = new();
    private ChatAudit? _audit;
    private int _hostProto;
    private long _linesSeen;
    private readonly LinkedList<string> _recentTexts = new();
    private long _position;

    public HostChatLogService(ILogger<HostChatLogService> log, IOptions<CauldronServerOptions> opts, ChatService chat)
    {
        _log = log;
        _opts = opts.Value;
        _chat = chat;
    }

    public sealed record ChatAudit(int Players, int Relays, int Ready, bool SaveMarker, DateTimeOffset At);

    public sealed record ChatLine(string Sender, string Text);

    /// <summary>The latest audit line, how many player lines were read since start, the host protocol (0 = chat host not seen).</summary>
    public (ChatAudit? Audit, long Lines, int HostProto) Snapshot()
    {
        lock (_lock) return (_audit, _linesSeen, _hostProto);
    }

    /// <summary>The last player texts read (newest last), for the save leak check.</summary>
    public IReadOnlyList<string> RecentTexts()
    {
        lock (_lock) return _recentTexts.ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.GameUserDir))
        {
            _log.LogInformation("[chat] host chat tail idle: GameUserDir not configured");
            return;
        }
        var logPath = Path.Combine(_opts.GameUserDir, "ws-ue.log");
        _log.LogInformation("[chat] host chat tail watching {Path}", logPath);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(logPath))
                {
                    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length < _position) _position = 0;
                    fs.Seek(_position, SeekOrigin.Begin);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    string? line;
                    while ((line = await sr.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                        ProcessLine(line);
                    _position = fs.Position;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { }
            catch (Exception ex) { _log.LogDebug(ex, "[chat] host chat tail loop error"); }

            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal void ProcessLine(string line)
    {
        if (!line.Contains("CAULDRON_CHAT:", StringComparison.Ordinal)) return;
        var chat = TryParseLine(line);
        if (chat is not null)
        {
            _chat.AppendFromGame(chat.Sender, chat.Text);
            lock (_lock)
            {
                _linesSeen++;
                _recentTexts.AddLast(chat.Text);
                while (_recentTexts.Count > 20) _recentTexts.RemoveFirst();
            }
            return;
        }
        var audit = TryParseAudit(line, DateTimeOffset.UtcNow);
        if (audit is not null)
        {
            lock (_lock) _audit = audit;
            if (audit.SaveMarker)
                _log.LogWarning("[chat] audit: a chat relay carries the save system's interface");
            return;
        }
        var on = HostOnRegex.Match(line);
        if (on.Success && int.TryParse(on.Groups["proto"].Value, out var proto))
        {
            lock (_lock) _hostProto = proto;
            _log.LogInformation("[chat] in-game chat is on (protocol {Proto})", proto);
        }
    }

    /// <summary>Parse one player chat log line; null when the line is not one or does not decode cleanly.</summary>
    internal static ChatLine? TryParseLine(string line)
    {
        var m = LineRegex.Match(line);
        if (!m.Success) return null;
        var from = DecodeHex(m.Groups["from"].Value, MaxNameChars);
        var text = DecodeHex(m.Groups["text"].Value, MaxTextChars);
        if (from is null || text is null) return null;
        from = CleanText(from, MaxNameChars);
        text = CleanText(text, MaxTextChars);
        if (text.Length == 0) return null;
        return new ChatLine(from.Length == 0 ? "Player" : from, text);
    }

    internal static ChatAudit? TryParseAudit(string line, DateTimeOffset at)
    {
        var m = AuditRegex.Match(line);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups["players"].Value, out var players)) return null;
        if (!int.TryParse(m.Groups["relays"].Value, out var relays)) return null;
        if (!int.TryParse(m.Groups["ready"].Value, out var ready)) return null;
        return new ChatAudit(players, relays, ready, string.Equals(m.Groups["marker"].Value, "true", StringComparison.OrdinalIgnoreCase), at);
    }

    /// <summary>4 hex digits per UTF-16 code unit. Null on a bad length, a bad digit or more than maxChars units.</summary>
    internal static string? DecodeHex(string hex, int maxChars)
    {
        if (hex.Length % 4 != 0 || hex.Length / 4 > maxChars) return null;
        var sb = new StringBuilder(hex.Length / 4);
        for (var i = 0; i < hex.Length; i += 4)
        {
            if (!ushort.TryParse(hex.AsSpan(i, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var unit))
                return null;
            sb.Append((char)unit);
        }
        return sb.ToString();
    }

    /// <summary>The same rules as the host script: control and direction characters out, &lt; &gt; harmless, no lone surrogates, trimmed.</summary>
    internal static string CleanText(string s, int max)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length && sb.Length < max; i++)
        {
            var c = s[i];
            if (c is '\t' or '\n' or '\r') { sb.Append(' '); continue; }
            if (c < 0x20 || c == 0x7F || (c >= 0x80 && c < 0xA0) || (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069)
                || c == 0x2028 || c == 0x2029 || c == 0x200B || c == 0xFEFF)
                continue;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) && sb.Length + 2 <= max)
                {
                    sb.Append(c).Append(s[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            if (c == '<') { sb.Append('‹'); continue; }
            if (c == '>') { sb.Append('›'); continue; }
            sb.Append(c);
        }
        return sb.ToString().Trim(' ');
    }
}
