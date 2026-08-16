using System.Text;
using System.Text.RegularExpressions;

namespace OctoBis.Scraper;

/// <summary>
/// Reads the talent trees out of OctoWoW's talent calculator.
///
/// The calculator is a Next.js app that streams its data as React Server Component payloads inside
/// <c>self.__next_f.push([1, "..."])</c> calls. There is no JSON endpoint behind it - the probable
/// paths all 404 - so the payload is unescaped and read directly. It is regular enough to pick
/// apart with patterns: every talent node is a flat object carrying its grid cell, tree, rank
/// count, icon and prerequisite, followed by a tooltip whose first heading is the talent's name.
/// </summary>
public sealed partial class TalentScraper
{
    private const string BaseUrl = "https://octowow.st/talents/";

    /// <summary>Trees are laid out four columns wide, and a node's `i` is its cell in that grid.</summary>
    private const int TreeColumns = 4;

    private static readonly string[] ClassSlugs =
        { "warrior", "paladin", "hunter", "rogue", "priest", "shaman", "mage", "warlock", "druid" };

    private readonly HttpClient _http;
    private readonly string _cacheDir;

    public List<string> Warnings { get; } = new();

    public TalentScraper(HttpClient http, string cacheDir)
    {
        _http = http;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public sealed class Talent
    {
        public int Cell { get; init; }
        public int Row => Cell / TreeColumns;
        public int Column => Cell % TreeColumns;
        public string Name { get; set; } = "";
        public string? Icon { get; init; }
        public int Ranks { get; init; }
        /// <summary>Grid cell of the talent that must be trained first, if any.</summary>
        public int? Requires { get; init; }
        public int? RequiredRanks { get; init; }
        public string? Description { get; set; }
    }

    public sealed class Tree
    {
        public int Index { get; init; }
        public string Name { get; set; } = "";
        public List<Talent> Talents { get; } = new();
    }

    public sealed class ClassTalents
    {
        public string ClassId { get; init; } = "";
        public List<Tree> Trees { get; } = new();
    }

    public async Task<List<ClassTalents>> LoadAllAsync(int delayMs)
    {
        var result = new List<ClassTalents>();

        foreach (var slug in ClassSlugs)
        {
            var html = await FetchAsync(slug, delayMs);
            if (string.IsNullOrEmpty(html))
            {
                Warnings.Add($"{slug}: talent page returned nothing");
                continue;
            }

            var parsed = Parse(slug, html);
            if (parsed.Trees.Count == 0) Warnings.Add($"{slug}: no talent trees found");
            result.Add(parsed);

            Console.WriteLine($"  {slug,-9} {parsed.Trees.Count} trees, " +
                              $"{parsed.Trees.Sum(t => t.Talents.Count)} talents " +
                              $"({string.Join(", ", parsed.Trees.Select(t => t.Name))})");
        }

        return result;
    }

    private async Task<string> FetchAsync(string slug, int delayMs)
    {
        var path = Path.Combine(_cacheDir, $"talents_{slug}.html");
        if (File.Exists(path)) return await File.ReadAllTextAsync(path);

        await Task.Delay(delayMs);
        try
        {
            var body = await _http.GetStringAsync($"{BaseUrl}{slug}/");
            await File.WriteAllTextAsync(path, body);
            return body;
        }
        catch (HttpRequestException ex)
        {
            Warnings.Add($"{slug}: {ex.Message}");
            return "";
        }
    }

    internal ClassTalents Parse(string classId, string html)
    {
        var payload = ExtractPayload(html);
        var result = new ClassTalents { ClassId = classId };

        // Tree headings carry their own index, so the three specs can be named without guessing.
        foreach (Match match in TreeNameRegex().Matches(payload))
        {
            var index = int.Parse(match.Groups[2].Value);
            if (result.Trees.Any(t => t.Index == index)) continue;
            result.Trees.Add(new Tree { Index = index, Name = match.Groups[1].Value });
        }

        foreach (Match match in TalentNodeRegex().Matches(payload))
        {
            var treeIndex = int.Parse(match.Groups["tree"].Value);
            var tree = result.Trees.FirstOrDefault(t => t.Index == treeIndex);
            if (tree is null)
            {
                tree = new Tree { Index = treeIndex, Name = $"Tree {treeIndex + 1}" };
                result.Trees.Add(tree);
            }

            var talent = new Talent
            {
                Cell = int.Parse(match.Groups["cell"].Value),
                Icon = match.Groups["icon"].Value,
                Ranks = int.Parse(match.Groups["ranks"].Value),
                Requires = match.Groups["requires"].Success ? int.Parse(match.Groups["requires"].Value) : null,
                RequiredRanks = match.Groups["reqRanks"].Success ? int.Parse(match.Groups["reqRanks"].Value) : null
            };

            // The name and description live in the tooltip that follows the node.
            var tail = payload[match.Index..Math.Min(payload.Length, match.Index + 2500)];
            talent.Name = TalentNameRegex().Match(tail) is { Success: true } name
                ? name.Groups[1].Value
                : $"Talent {talent.Cell}";
            talent.Description = ExtractDescription(tail);

            // A cell can only hold one talent; duplicates mean the pattern matched a repeat render.
            if (tree.Talents.All(t => t.Cell != talent.Cell)) tree.Talents.Add(talent);
        }

        foreach (var tree in result.Trees) tree.Talents.Sort((a, b) => a.Cell.CompareTo(b.Cell));
        result.Trees.Sort((a, b) => a.Index.CompareTo(b.Index));
        return result;
    }

    /// <summary>
    /// Joins the streamed payload chunks and undoes the JavaScript string escaping, so the result
    /// can be pattern-matched as ordinary text.
    /// </summary>
    private static string ExtractPayload(string html)
    {
        var sb = new StringBuilder();

        foreach (Match match in PayloadRegex().Matches(html))
        {
            var raw = match.Groups[1].Value;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] != '\\' || i + 1 >= raw.Length) { sb.Append(raw[i]); continue; }

                i++;
                switch (raw[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': break;
                    case 'u' when i + 4 < raw.Length:
                        sb.Append((char)Convert.ToInt32(raw.Substring(i + 1, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(raw[i]); break;
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The description is an array mixing literal text with nested components, e.g.
    /// <c>["A vicious strike that deals ","115","% weapon damage..."]</c>. Where a talent's value
    /// changes per rank the payload embeds a component holding a <c>values</c> array instead of a
    /// literal, which renders as "1/2/3" on the site - so that is what we substitute.
    ///
    /// Only top-level elements are read. Descending into the nested components is what previously
    /// spilled their object keys into the text ("...by $$L1e1treetalentvalues123").
    /// </summary>
    private static string? ExtractDescription(string tail)
    {
        var marker = tail.IndexOf(@"""className"":""whitespace-pre-wrap"",""children"":", StringComparison.Ordinal);
        if (marker < 0) return null;

        var start = tail.IndexOf('[', marker);
        if (start < 0) return null;

        var sb = new StringBuilder();
        foreach (var element in TopLevelElements(tail, start))
        {
            if (element.Length > 0 && element[0] == '"')
            {
                sb.Append(Unquote(element));
                continue;
            }

            // A nested component: use its per-rank values if it has any.
            var values = ValuesRegex().Match(element);
            if (!values.Success) continue;

            var numbers = NumberRegex().Matches(values.Groups[1].Value).Select(m => m.Value);
            sb.Append(string.Join('/', numbers));
        }

        // A handful of talents interpolate a value through a deferred reference ("$L47") that is
        // resolved from a separate payload chunk. Rather than follow the reference table for ~1% of
        // descriptions, mark the gap so the sentence still reads as a sentence.
        var text = DeferredRefRegex().Replace(sb.ToString(), "?").Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>Splits a bracketed array into its immediate elements, respecting nesting and strings.</summary>
    private static IEnumerable<string> TopLevelElements(string source, int openBracket)
    {
        var depth = 0;
        var elementStart = openBracket + 1;

        for (var i = openBracket; i < source.Length; i++)
        {
            var c = source[i];

            if (c == '"')
            {
                i = SkipString(source, i);
                continue;
            }

            if (c is '[' or '{')
            {
                depth++;
                continue;
            }

            if (c is ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    if (i > elementStart) yield return source[elementStart..i].Trim();
                    yield break;
                }
                continue;
            }

            if (c == ',' && depth == 1)
            {
                yield return source[elementStart..i].Trim();
                elementStart = i + 1;
            }
        }
    }

    private static int SkipString(string source, int quoteIndex)
    {
        for (var i = quoteIndex + 1; i < source.Length; i++)
        {
            if (source[i] == '\\') { i++; continue; }
            if (source[i] == '"') return i;
        }
        return source.Length - 1;
    }

    private static string Unquote(string literal)
    {
        var trimmed = literal.Trim();
        if (trimmed.Length < 2) return trimmed;
        return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    [GeneratedRegex(@"self\.__next_f\.push\(\[1,""((?:[^""\\]|\\.)*)""\]\)")]
    private static partial Regex PayloadRegex();

    [GeneratedRegex(@"""className"":""h4 grow truncate"",""title"":""([^""]+)"",""children"":""[^""]*""}\],\[""\$"",""\$L\w+"",null,\{""index"":(\d+)")]
    private static partial Regex TreeNameRegex();

    [GeneratedRegex(@"""i"":(?<cell>\d+),""index"":(?<tree>\d+),""icon"":""(?<icon>[^""]*)"",""ranks"":(?<ranks>\d+)(?:,""requires"":(?<requires>\d+))?(?:,""reqRanks"":(?<reqRanks>\d+))?")]
    private static partial Regex TalentNodeRegex();

    [GeneratedRegex(@"""className"":""tw-color"",""children"":""([^""]+)""")]
    private static partial Regex TalentNameRegex();

    [GeneratedRegex(@"""values"":\[([^\]]*)\]")]
    private static partial Regex ValuesRegex();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\$L?[0-9a-f]+")]
    private static partial Regex DeferredRefRegex();
}
