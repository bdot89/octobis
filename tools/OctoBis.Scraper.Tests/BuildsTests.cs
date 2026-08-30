using System.Text.Json;
using Xunit;

namespace OctoBis.Scraper.Tests;

/// <summary>
/// Checks every suggested talent build in config/builds.json against the real trees in
/// data/talents.json.
///
/// The builds are hand-authored - adapted from vanilla and Turtle consensus onto OctoWoW's own
/// trees - and hand-authoring 29 builds of 51 points each across trees that differ from vanilla is
/// exactly the kind of work that silently produces a build no player could actually train. A rank
/// above the maximum, a talent whose prerequisite is not taken, or a fifth-tier pick in a tree with
/// only 18 points in it all look perfectly reasonable in a JSON file and are impossible in game.
///
/// The tier rule mirrors talents.js: a talent in row N needs N*5 points spent anywhere in that
/// tree. The simulation places points one at a time in any order that works, because a build is
/// legal if *some* training order reaches it.
/// </summary>
public class BuildsTests
{
    private static readonly Lazy<(JsonElement Builds, JsonElement Talents)> Data = new(Load);

    private static (JsonElement, JsonElement) Load()
    {
        var root = FindRepoRoot();
        var builds = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "builds.json"))).RootElement;
        var talents = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "talents.json"))).RootElement;
        return (builds, talents);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config", "builds.json"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    public static TheoryData<string, string> EverySpec()
    {
        var data = new TheoryData<string, string>();
        foreach (var cls in Data.Value.Builds.GetProperty("builds").EnumerateObject())
            foreach (var spec in cls.Value.EnumerateObject())
                data.Add(cls.Name, spec.Name);
        return data;
    }

    private sealed record Talent(int Cell, int Row, int Ranks, int? Requires, int? ReqRanks, string Name);

    private static Dictionary<string, List<Talent>> TreesFor(string classId)
    {
        var trees = new Dictionary<string, List<Talent>>();
        foreach (var tree in Data.Value.Talents.GetProperty("classes").GetProperty(classId).GetProperty("trees").EnumerateArray())
        {
            var list = new List<Talent>();
            foreach (var t in tree.GetProperty("talents").EnumerateArray())
            {
                list.Add(new Talent(
                    t.GetProperty("cell").GetInt32(),
                    t.GetProperty("row").GetInt32(),
                    t.GetProperty("ranks").GetInt32(),
                    t.TryGetProperty("requires", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : null,
                    t.TryGetProperty("reqRanks", out var rr) && rr.ValueKind == JsonValueKind.Number ? rr.GetInt32() : null,
                    t.GetProperty("name").GetString()!));
            }
            trees[tree.GetProperty("name").GetString()!] = list;
        }
        return trees;
    }

    [Theory]
    [MemberData(nameof(EverySpec))]
    public void EveryBuildIsOneAPlayerCouldActuallyTrain(string classId, string specId)
    {
        var entry = Data.Value.Builds.GetProperty("builds").GetProperty(classId).GetProperty(specId);
        var trees = TreesFor(classId);

        // Resolve names, and check ranks while we are here.
        var wanted = new Dictionary<string, Dictionary<int, int>>();
        var total = 0;

        foreach (var treeEntry in entry.GetProperty("points").EnumerateObject())
        {
            Assert.True(trees.ContainsKey(treeEntry.Name),
                $"{classId}/{specId}: no tree named '{treeEntry.Name}'.");

            var tree = trees[treeEntry.Name];
            var want = wanted[treeEntry.Name] = new Dictionary<int, int>();

            foreach (var point in treeEntry.Value.EnumerateObject())
            {
                var talent = tree.FirstOrDefault(t => t.Name == point.Name);
                Assert.True(talent is not null,
                    $"{classId}/{specId}: '{point.Name}' is not a talent in {treeEntry.Name}.");

                var ranks = point.Value.GetInt32();
                Assert.True(ranks <= talent!.Ranks,
                    $"{classId}/{specId}: {point.Name} asks for {ranks} of a maximum {talent.Ranks}.");

                want[talent.Cell] = ranks;
                total += ranks;
            }
        }

        Assert.True(total <= 51, $"{classId}/{specId}: spends {total} points; a level 60 character has 51.");

        // Place points one at a time, in whatever order works.
        var have = wanted.ToDictionary(pair => pair.Key, _ => new Dictionary<int, int>());
        var placed = 0;
        bool progress;

        do
        {
            progress = false;
            foreach (var (treeName, want) in wanted)
            {
                var tree = trees[treeName];
                var inTree = have[treeName].Values.Sum();

                foreach (var (cell, target) in want)
                {
                    have[treeName].TryGetValue(cell, out var current);
                    if (current >= target) continue;

                    var talent = tree.First(t => t.Cell == cell);
                    if (inTree < talent.Row * 5) continue;

                    if (talent.Requires is { } requiredCell)
                    {
                        var needed = talent.ReqRanks ?? tree.First(t => t.Cell == requiredCell).Ranks;
                        have[treeName].TryGetValue(requiredCell, out var held);
                        if (held < needed) continue;
                    }

                    have[treeName][cell] = current + 1;
                    inTree++;
                    placed++;
                    progress = true;
                }
            }
        } while (progress);

        if (placed == total) return;

        // Say exactly which point could not be trained, and why - a bare count is useless to whoever
        // has to fix the build.
        var stuck = new List<string>();
        foreach (var (treeName, want) in wanted)
        {
            var tree = trees[treeName];
            var inTree = have[treeName].Values.Sum();

            foreach (var (cell, target) in want)
            {
                have[treeName].TryGetValue(cell, out var current);
                if (current >= target) continue;

                var talent = tree.First(t => t.Cell == cell);
                stuck.Add(inTree < talent.Row * 5
                    ? $"{talent.Name} sits in row {talent.Row}, needing {talent.Row * 5} points in {treeName}, but the build spends {inTree}"
                    : $"{talent.Name} needs its prerequisite trained first");
            }
        }

        Assert.Fail($"{classId}/{specId} cannot be trained: {string.Join("; ", stuck)}.");
    }

    [Theory]
    [MemberData(nameof(EverySpec))]
    public void EveryBuildSpendsAllFiftyOnePoints(string classId, string specId)
    {
        var entry = Data.Value.Builds.GetProperty("builds").GetProperty(classId).GetProperty(specId);

        var total = entry.GetProperty("points").EnumerateObject()
            .SelectMany(tree => tree.Value.EnumerateObject())
            .Sum(point => point.Value.GetInt32());

        // Not a rule of the game - a build may legally spend fewer - but every build shipped here
        // is meant to be complete, and a short one is far more likely to be a dropped talent than
        // a deliberate choice.
        Assert.Equal(51, total);
    }

    [Fact]
    public void EverySpecInTheConfigHasASuggestedBuild()
    {
        var root = FindRepoRoot();
        var specs = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "specs.json"))).RootElement;
        var builds = Data.Value.Builds.GetProperty("builds");

        var missing = new List<string>();
        foreach (var cls in specs.GetProperty("classes").EnumerateArray())
        {
            var classId = cls.GetProperty("id").GetString()!;
            foreach (var spec in cls.GetProperty("specs").EnumerateArray())
            {
                var specId = spec.GetProperty("id").GetString()!;
                if (!builds.TryGetProperty(classId, out var forClass) ||
                    !forClass.TryGetProperty(specId, out _))
                {
                    missing.Add($"{classId}/{specId}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            $"No suggested build for: {string.Join(", ", missing)}. Every spec the planner offers needs one.");
    }
}
