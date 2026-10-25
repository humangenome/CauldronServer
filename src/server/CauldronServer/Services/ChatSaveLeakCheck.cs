using System.Text;

namespace CauldronServer.Services;

/// <summary>
/// Does any file of the world save hold a recent chat line? Chat must never reach the save: the relay
/// has no saved fields and no game message carries chat text. This is the run-time proof RCON
/// <c>chataudit</c> reports: it searches every file under <c>Saved\SaveGames</c> for each recent line
/// of 8 or more characters, as UTF-8 and as UTF-16.
/// </summary>
public static class ChatSaveLeakCheck
{
    public const int MinTextChars = 8;
    private const long MaxFileBytes = 256L * 1024 * 1024;

    public sealed record Result(int FilesScanned, int TextsChecked, IReadOnlyList<string> Hits, bool SaveFolderFound);

    public static Result Run(string saveGamesDir, IReadOnlyList<string> recentTexts)
    {
        var texts = recentTexts.Where(t => t.Length >= MinTextChars).Distinct(StringComparer.Ordinal).ToList();
        if (!Directory.Exists(saveGamesDir))
            return new Result(0, texts.Count, Array.Empty<string>(), false);

        var needles = texts.SelectMany(t => new[] { Encoding.UTF8.GetBytes(t), Encoding.Unicode.GetBytes(t) }).ToList();
        var hits = new List<string>();
        var files = 0;
        foreach (var path in Directory.EnumerateFiles(saveGamesDir, "*", SearchOption.AllDirectories))
        {
            byte[] data;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes) continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                data = new byte[fs.Length];
                var read = 0;
                while (read < data.Length)
                {
                    var n = fs.Read(data, read, data.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            files++;
            if (needles.Count == 0) continue;
            if (needles.Any(n => data.AsSpan().IndexOf(n) >= 0))
                hits.Add(Path.GetRelativePath(saveGamesDir, path));
        }
        return new Result(files, texts.Count, hits, true);
    }
}
