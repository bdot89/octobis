using System.Text.RegularExpressions;

namespace OctoBis.Scraper;

/// <summary>
/// Seeds the crawl from the pfQuest-octo dataset.
///
/// The database has no way to list the NPCs in a zone over GET - its filter form is POST-only and
/// its list pages cap at 500 rows - so we take the zone-to-unit mapping from pfQuest-octo instead,
/// which ships the server's spawn data as Lua tables. That gives us a starting set of NPCs per
/// instance without having to hand-curate boss lists, which matters most for Turtle's custom
/// dungeons where published boss rosters are thin or absent.
///
/// The mapping is a seed, not the whole truth: scripted encounters that have no static spawn entry
/// (Ragnaros, Nefarian) are missing from it. The crawler's backfill round picks those up from the
/// "dropped by" lists on the item pages themselves.
/// </summary>
public static partial class PfQuestSeed
{
    private const string RepoRaw = "https://raw.githubusercontent.com/paokkerkir/pfQuest-octo/master/db/";

    public static async Task<string> DownloadAsync(HttpClient http, string cacheDir, string file)
    {
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, file.Replace('/', '_'));
        if (File.Exists(path)) return await File.ReadAllTextAsync(path);

        var body = await http.GetStringAsync(RepoRaw + file);
        await File.WriteAllTextAsync(path, body);
        return body;
    }

    /// <summary>Builds zone id -> unit ids from the spawn coordinates in units-turtle.lua.</summary>
    public static Dictionary<int, HashSet<int>> ParseZoneUnits(string lua)
    {
        var result = new Dictionary<int, HashSet<int>>();
        var currentUnit = 0;

        foreach (var line in lua.Split('\n'))
        {
            if (UnitHeaderRegex().Match(line) is { Success: true } header)
            {
                currentUnit = int.Parse(header.Groups[1].Value);
                continue;
            }

            if (currentUnit == 0) continue;

            // Coordinates are { x, y, zoneId, respawnSeconds }.
            if (CoordRegex().Match(line) is { Success: true } coord)
            {
                var zone = int.Parse(coord.Groups[1].Value);
                if (zone == 0) continue;
                if (!result.TryGetValue(zone, out var units))
                    result[zone] = units = new HashSet<int>();
                units.Add(currentUnit);
            }
        }

        return result;
    }

    /// <summary>Parses a pfQuest locale table, e.g. [5077] = "Crescent Grove".</summary>
    public static Dictionary<int, string> ParseNameTable(string lua)
    {
        var result = new Dictionary<int, string>();
        foreach (Match m in NameEntryRegex().Matches(lua))
            result[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value.Replace("\\'", "'").Replace("\\\"", "\"");
        return result;
    }

    [GeneratedRegex(@"^\s{1,4}\[(\d+)\]\s*=\s*\{\s*$")] private static partial Regex UnitHeaderRegex();
    [GeneratedRegex(@"^\s*\[\d+\]\s*=\s*\{\s*-?[\d.]+,\s*-?[\d.]+,\s*(\d+),")] private static partial Regex CoordRegex();
    [GeneratedRegex(@"\[(\d+)\]\s*=\s*""((?:[^""\\]|\\.)*)""")] private static partial Regex NameEntryRegex();
}
