using System.Text;
using System.Text.RegularExpressions;

namespace Cauldron.Protocol;

/// <summary>
/// The check that runs before Cauldron's chat scripts are placed into a Witchspire game folder.
///
/// Witchspire compiles every loose AngelScript file when it starts, and a compile error in any of
/// them freezes the game (on a host: before the world opens; on a player's PC: at the menu). The chat
/// scripts use names from the game's own scripts, so a game update that renames or moves one of them
/// would freeze whoever has the chat files. Before placing them, the app (player side) and
/// CauldronServer (self-hosted host side) read <c>game-names.txt</c>, shipped beside the scripts, and
/// check every entry against the game's own files. Anything missing means chat is not placed, and the
/// caller logs which names were missing. The hosting panel and scripts/check-chat-names.py apply the
/// same rules; the format is described at the top of game-names.txt.
/// </summary>
public static class ChatScriptNames
{
    public enum Scope { Both, Host }

    public enum Kind { Class, Enum, Text, Bind }

    public sealed record Entry(Scope Scope, Kind Kind, string File, string Name)
    {
        public override string ToString() => $"{Kind.ToString().ToLowerInvariant()} {File} {Name}";
    }

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Parse a manifest. Throws <see cref="FormatException"/> on any line it does not understand.</summary>
    public static IReadOnlyList<Entry> Parse(string manifest)
    {
        var list = new List<Entry>();
        foreach (var raw in manifest.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var parts = line.Split('\t');
            if (parts.Length != 4) throw new FormatException($"bad manifest line: {line}");
            var scope = parts[0] switch
            {
                "both" => Scope.Both,
                "host" => Scope.Host,
                _ => throw new FormatException($"bad scope: {line}"),
            };
            var kind = parts[1] switch
            {
                "class" => Kind.Class,
                "enum" => Kind.Enum,
                "text" => Kind.Text,
                "bind" => Kind.Bind,
                _ => throw new FormatException($"bad kind: {line}"),
            };
            if (parts[2].Length == 0 || parts[3].Length == 0 || parts[2].Contains("..", StringComparison.Ordinal))
                throw new FormatException($"bad file or name: {line}");
            if (kind == Kind.Enum && parts[3].Split(' ').Length != 2)
                throw new FormatException($"enum entry needs '<Enum> <Member>': {line}");
            list.Add(new Entry(scope, kind, parts[2], parts[3]));
        }
        if (list.Count == 0) throw new FormatException("manifest has no entries");
        return list;
    }

    /// <summary>
    /// The entries that do not hold for the game whose <c>Hercules\Script</c> folder is
    /// <paramref name="scriptRoot"/>. <paramref name="includeHost"/> adds the host-only entries.
    /// Empty means every name is there and the chat files may be placed.
    /// </summary>
    public static IReadOnlyList<Entry> Missing(string scriptRoot, IEnumerable<Entry> entries, bool includeHost)
    {
        var missing = new List<Entry>();
        var texts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        byte[]? binds = null;
        var bindsRead = false;
        foreach (var e in entries)
        {
            if (e.Scope == Scope.Host && !includeHost) continue;
            bool ok;
            if (e.Kind == Kind.Bind)
            {
                if (!bindsRead)
                {
                    bindsRead = true;
                    try { binds = File.ReadAllBytes(Path.Combine(scriptRoot, "Binds.Cache")); }
                    catch (Exception) { binds = null; }
                }
                ok = binds is not null && IndexOf(binds, Encoding.ASCII.GetBytes(e.Name)) >= 0;
            }
            else
            {
                if (!texts.TryGetValue(e.File, out var src))
                {
                    try
                    {
                        var path = Path.Combine(new[] { scriptRoot }.Concat(e.File.Split('/')).ToArray());
                        src = File.ReadAllText(path, Encoding.UTF8);
                    }
                    catch (Exception) { src = null; }
                    texts[e.File] = src;
                }
                ok = src is not null && Holds(e, src);
            }
            if (!ok) missing.Add(e);
        }
        return missing;
    }

    internal static bool Holds(Entry e, string src)
    {
        switch (e.Kind)
        {
            case Kind.Class:
                foreach (var line in src.Split('\n'))
                {
                    var s = line.TrimStart(' ', '\t');
                    var head = "class " + e.Name;
                    if (!s.StartsWith(head, StringComparison.Ordinal)) continue;
                    var rest = s[head.Length..].TrimEnd('\r');
                    if (rest.Length == 0 || rest[0] is ' ' or '\t' or ':' or '{') return true;
                }
                return false;
            case Kind.Enum:
            {
                var two = e.Name.Split(' ');
                var m = Regex.Match(src, @"\benum\s+" + Regex.Escape(two[0]) + @"\b[^{]*\{([^}]*)\}");
                return m.Success && Regex.IsMatch(m.Groups[1].Value, @"(?<![A-Za-z0-9_])" + Regex.Escape(two[1]) + @"(?![A-Za-z0-9_])");
            }
            case Kind.Text:
                return Spaces.Replace(src, " ").Contains(Spaces.Replace(e.Name, " "), StringComparison.Ordinal);
            default:
                return false;
        }
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        if (needle.Length == 0) return 0;
        for (var i = 0; i <= hay.Length - needle.Length; i++)
        {
            if (hay[i] != needle[0]) continue;
            var j = 1;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
