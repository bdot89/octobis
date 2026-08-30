using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OctoBis.Scraper;

/// <summary>Sources kept per item in the published data. Sorted most-accessible first.</summary>
const int MaxSourcesPerItem = 6;

// A full crawl runs for hours and is normally watched through a redirected log. .NET buffers stdout
// when it is not a console, which makes progress invisible until the process ends, so flush eagerly.
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

// ---- Arguments -------------------------------------------------------------------------------

var refresh = args.Contains("--refresh");
var downloadIcons = args.Contains("--download-icons");
var limit = ParseLimit(args);
var delayMs = ParseInt(args, "--delay") ?? 800;

var repoRoot = FindRepoRoot();

// The cache deliberately lives outside the repository. This repo sits in a OneDrive folder, and
// writing thousands of pages into a synced directory stalls file I/O badly enough to back up the
// thread pool - fetches that take 0.3s on the wire were measuring 15s end to end. Override with
// --cache if you want it somewhere else.
var cacheDir = ParseString(args, "--cache")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OctoBiS", "cache");
var configDir = Path.Combine(repoRoot, "config");
var dataDir = Path.Combine(repoRoot, "data");
Directory.CreateDirectory(dataDir);

if (refresh && Directory.Exists(cacheDir))
{
    Console.WriteLine("Clearing cache...");
    Directory.Delete(cacheDir, recursive: true);
}

Console.WriteLine($"OctoBiS scraper");
Console.WriteLine($"  repo   {repoRoot}");
Console.WriteLine($"  delay  {delayMs}ms between requests");
if (limit is { } l) Console.WriteLine($"  limit  {l} pages per round (smoke-test mode)");
Console.WriteLine();

// ---- Zone names and the spawn seed -------------------------------------------------------------

using var client = new AowowClient(cacheDir, delayMs);
using var plainHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
plainHttp.DefaultRequestHeaders.UserAgent.ParseAdd("OctoBiS/1.0 (+data pipeline)");

Console.WriteLine("Loading zone names...");
var zoneNames = new Dictionary<int, string>();

// Base map: the database's own locale file.
var localeJs = await client.GetAsync("templates/wowhead/js/locale_enus.js");
foreach (var (id, name) in ParseLocaleZones(localeJs)) zoneNames[id] = name;

// Turtle's custom zones, which the locale file does not carry.
var pfZoneNames = PfQuestSeed.ParseNameTable(
    await PfQuestSeed.DownloadAsync(plainHttp, Path.Combine(cacheDir, "pfquest"), "enUS/zones-turtle.lua"));
foreach (var (id, name) in pfZoneNames) zoneNames.TryAdd(id, name);

Console.WriteLine("Loading spawn data...");
var zoneUnits = PfQuestSeed.ParseZoneUnits(
    await PfQuestSeed.DownloadAsync(plainHttp, Path.Combine(cacheDir, "pfquest"), "units-turtle.lua"));

// --talents scrapes the talent calculator and writes data/talents.json. Kept separate from the
// item crawl: the trees change only when the server rebalances, not when loot moves around.
if (args.Contains("--talents"))
{
    Console.WriteLine("Scraping talent trees...");
    using var talentHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    talentHttp.DefaultRequestHeaders.UserAgent.ParseAdd("OctoBiS/1.0 (talent tree import)");

    var talentScraper = new TalentScraper(talentHttp, Path.Combine(cacheDir, "talents"));
    var classes = await talentScraper.LoadAllAsync(ParseInt(args, "--delay") ?? 800);

    await WriteJsonAsync(Path.Combine(dataDir, "talents.json"), new
    {
        generated = DateTime.UtcNow.ToString("O"),
        source = "https://octowow.st/talents/",
        iconBase = "https://octowow.st/db/images/icons/medium/",
        iconExtension = ".png",
        classes = classes.ToDictionary(c => c.ClassId, c => new
        {
            trees = c.Trees.Select(t => new
            {
                index = t.Index,
                name = t.Name,
                talents = t.Talents.Select(talent => new
                {
                    cell = talent.Cell,
                    row = talent.Row,
                    col = talent.Column,
                    name = talent.Name,
                    icon = talent.Icon,
                    ranks = talent.Ranks,
                    requires = talent.Requires,
                    reqRanks = talent.RequiredRanks,
                    description = talent.Description
                })
            })
        }),
        warnings = talentScraper.Warnings
    }, new JsonSerializerOptions { WriteIndented = false });

    foreach (var warning in talentScraper.Warnings) Console.WriteLine($"  WARNING {warning}");
    Console.WriteLine($"Done. {classes.Sum(c => c.Trees.Sum(t => t.Talents.Count))} talents across {classes.Count} classes.");
    return 0;
}

// --atlas-report reads the Atlas-CFM addon and prints what it found, without touching the network.
if (ParseString(args, "--atlas-report") is { } atlasRoot)
{
    var importer = new AtlasImporter(atlasRoot);
    var instances = importer.LoadInstances();

    Console.WriteLine($"{instances.Count} instances visible on the Turtle profile");
    Console.WriteLine($"{instances.Sum(i => i.Bosses.Count)} encounters, " +
                      $"{instances.Sum(i => i.LootCount)} loot entries, " +
                      $"{instances.SelectMany(i => i.Bosses).SelectMany(b => b.Loot).Select(l => l.ItemId).Distinct().Count()} distinct items");
    Console.WriteLine();

    foreach (var instance in instances.OrderByDescending(i => i.LootCount))
    {
        Console.WriteLine($"  {instance.Key,-28} {instance.Name,-32} {instance.Bosses.Count,3} bosses  {instance.LootCount,4} loot" +
                          (instance.MaxPlayers is { } n ? $"  {n}-man" : ""));
    }

    if (ParseString(args, "--detail") is { } detailKey)
    {
        var target = instances.FirstOrDefault(i => i.Key.Equals(detailKey, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine();
        if (target is null) Console.WriteLine($"No instance keyed '{detailKey}'");
        else
            foreach (var boss in target.Bosses)
            {
                Console.WriteLine($"  {boss.Order}. {boss.Name} ({boss.Loot.Count} items)");
                foreach (var loot in boss.Loot.Take(8))
                    Console.WriteLine($"       {loot.ItemId,7}  {loot.DropRate?.ToString("0.##") ?? "?",5}%  {loot.Name ?? "(unnamed)",-42}" +
                                      (loot.ContainerId is { } c ? $" token {c}" : "") +
                                      (loot.LooksLikeRecipe ? "  [recipe]" : ""));
            }
    }

    foreach (var warning in importer.Warnings) Console.WriteLine($"  WARNING {warning}");
    return 0;
}

// --attachments scrapes enchants, belt buckles and gems into data/enchants.json. Separate from the
// item crawl because these change only when the server adds recipes, not when loot moves.
if (args.Contains("--attachments"))
{
    Console.WriteLine("Scraping enchants, buckles and gems...");
    // Attachments the search endpoint cannot be relied on to surface are named by id in the config.
    var enchantConfigPath = Path.Combine(configDir, "enchants.json");
    var extraIds = new List<int>();
    if (File.Exists(enchantConfigPath))
    {
        using var enchantConfig = JsonDocument.Parse(await File.ReadAllTextAsync(enchantConfigPath));
        if (enchantConfig.RootElement.TryGetProperty("alsoInclude", out var alsoInclude)
            && alsoInclude.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in alsoInclude.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var idElement)
                    && idElement.TryGetInt32(out var id)) extraIds.Add(id);
                else if (entry.TryGetInt32(out var bare)) extraIds.Add(bare);
            }
        }
    }

    var attachmentScraper = new AttachmentScraper(client, extraIds);
    var attachments = await attachmentScraper.LoadAsync();

    var byKind = attachments.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
    foreach (var (kind, count) in byKind) Console.WriteLine($"  {kind,-8} {count}");

    await WriteJsonAsync(Path.Combine(dataDir, "enchants.json"), new
    {
        generated = DateTime.UtcNow.ToString("O"),
        source = "https://octowow.st/db/ — enchant spells, belt buckles and jewelcrafting gems",
        counts = byKind,
        enchants = attachments.Select(a => new
        {
            id = a.Id,
            kind = a.Kind,
            name = a.Name,
            slots = a.Slots,
            stats = a.Stats.Count > 0 ? a.Stats.OrderBy(s => s.Key).ToDictionary(s => s.Key, s => Math.Round(s.Value, 2)) : null,
            effect = a.Effect,
            proc = a.IsProc ? true : (bool?)null,
            twoHandOnly = a.TwoHandOnly ? true : (bool?)null,
            shieldOnly = a.ShieldOnly ? true : (bool?)null
        }),
        unparsedEffects = attachmentScraper.Unparsed.Distinct().Take(60).ToList(),
        warnings = attachmentScraper.Warnings
    }, new JsonSerializerOptions { WriteIndented = false });

    Console.WriteLine($"  {attachmentScraper.Unparsed.Distinct().Count()} effects with no stats parsed");
    foreach (var warning in attachmentScraper.Warnings) Console.WriteLine($"  WARNING {warning}");
    return 0;
}

// --search dumps what the database returns for a term, parsed rather than raw. Handy when working
// out what exists before deciding how to import it.
if (ParseString(args, "--search") is { } term)
{
    var html = await client.GetSearchPageAsync(term);
    var wanted = ParseString(args, "--type");
    var take = ParseInt(args, "--show") ?? 40;

    Console.WriteLine($"Search \"{term}\":");
    foreach (var view in ListviewParser.ParseAll(html))
    {
        if (wanted is not null && !view.Template.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine($"  {view.Template}/{view.Id}: {view.Rows.Count} rows");

        foreach (var row in view.Rows.Take(take))
        {
            var name = ListviewParser.StripNamePrefix(JsLiteral.Str(row, "name") ?? "");
            Console.WriteLine($"      {JsLiteral.Int(row, "id"),7}  {name}" +
                              (JsLiteral.Str(row, "rank") is { Length: > 0 } rank ? $"  [{rank}]" : ""));
        }
    }
    return 0;
}

// --inspect-zone prints what the spawn seed produced for one zone and stops. Use it whenever a
// zone comes back with a surprising NPC count before spending a whole crawl on it.
if (ParseInt(args, "--inspect-zone") is { } inspectId)
{
    var name = zoneNames.GetValueOrDefault(inspectId, "(unnamed)");
    var units = zoneUnits.GetValueOrDefault(inspectId) ?? new HashSet<int>();
    Console.WriteLine($"zone {inspectId} \"{name}\": {units.Count} seeded units");
    Console.WriteLine(string.Join(' ', units.OrderBy(u => u)));
    Console.WriteLine();
    Console.WriteLine($"ids also named \"{name}\": {string.Join(", ", zoneNames.Where(kv => kv.Value == name).Select(kv => kv.Key).OrderBy(i => i))}");
    return 0;
}

// --inspect-npc / --inspect-item dump what the parsers see on one page, which is the quickest way
// to tell a fetch problem apart from a parse problem.
if (ParseInt(args, "--inspect-npc") is { } npcId)
{
    var html = await client.GetNpcPageAsync(npcId);
    Console.WriteLine($"npc {npcId}: {html.Length} bytes");
    foreach (var view in ListviewParser.ParseAll(html))
    {
        Console.WriteLine($"  listview id='{view.Id}' template='{view.Template}' rows={view.Rows.Count}");
        foreach (var row in view.Rows.Take(3))
            Console.WriteLine($"      {string.Join(", ", row.Select(kv => $"{kv.Key}={Describe(kv.Value)}"))}");
    }
    return 0;
}

if (ParseInt(args, "--inspect-item") is { } itemId)
{
    var html = await client.GetItemPageAsync(itemId);
    var unmatched = new List<string>();
    var parsed = ItemPageParser.Parse(html, itemId, unmatched);
    Console.WriteLine(parsed is null
        ? $"item {itemId}: tooltip not recognised"
        : $"item {itemId}: {parsed.Name} (q{parsed.Quality}) {parsed.SubClassName} slot={parsed.Slot} req={parsed.ReqLevel} randomSuffix={parsed.HasRandomSuffix}\n  stats: {string.Join(", ", parsed.Stats.Select(s => $"{s.Key}={s.Value}"))}\n  classes: {string.Join('/', parsed.Classes)}\n  set: {parsed.SetName}\n  unmatched: {string.Join(" | ", unmatched)}");
    if (parsed is not null)
    {
        Console.WriteLine($"  tooltip: bind={parsed.Binding ?? "-"} unique={parsed.Unique} durability={parsed.Durability?.ToString() ?? "-"} bonusDmg={parsed.BonusDamage ?? "-"}");
        foreach (var effect in parsed.Effects) Console.WriteLine($"    [{effect.Kind}] {effect.Text}");
    }
    if (ItemPageParser.ParseSet(html) is { } setInfo)
    {
        Console.WriteLine($"  set {setInfo.Id} '{setInfo.Name}' {setInfo.Pieces.Count}/{setInfo.Total} pieces: {string.Join(", ", setInfo.Pieces)}");
        foreach (var bonus in setInfo.Bonuses) Console.WriteLine($"    ({bonus.Pieces}) {bonus.Text}");
    }
    foreach (var view in ListviewParser.ParseAll(html))
        Console.WriteLine($"  listview id='{view.Id}' template='{view.Template}' rows={view.Rows.Count}");
    return 0;
}

var config = Config.Load(await File.ReadAllTextAsync(Path.Combine(configDir, "phases.json")), zoneUnits);

// The Atlas-CFM addon is the primary loot source when its folder is available. Without it the
// crawl still works, falling back on the website alone.
var atlasPath = ParseString(args, "--atlas") ?? DefaultAtlasPath();
if (atlasPath is not null && Directory.Exists(Path.Combine(atlasPath, "CFMLoot", "Data")))
{
    config.Atlas = new AtlasImporter(atlasPath);
    Console.WriteLine($"  Atlas-CFM loot data: {atlasPath}");
}
else
{
    Console.WriteLine("  Atlas-CFM not found - falling back to website loot data only (pass --atlas <path>)");
}

// --zone narrows the crawl to a single instance, which keeps verification runs to a few minutes.
if (OnlyZone(args) is { } onlyZone)
{
    Console.WriteLine($"  restricted to zone \"{onlyZone}\" (crafted items skipped)");
    foreach (var phase in config.Phases)
    {
        phase.Zones = phase.Zones.Where(z => z.Equals(onlyZone, StringComparison.OrdinalIgnoreCase)).ToList();

        // Named bosses are kept for whichever phase still owns the zone. Dropping them would hide
        // exactly the encounters that named seeding exists to reach, which defeats the point of
        // using this flag to verify an instance.
        if (phase.Zones.Count == 0) phase.Npcs = new List<string>();
    }
    config.CraftedItems.Clear();
}

// Explicit overrides win over both name sources.
foreach (var (id, name) in config.ZoneIdOverrides) zoneNames[id] = name;

Console.WriteLine($"  {zoneNames.Count} zone names, {zoneUnits.Count} zones with spawns");
Console.WriteLine();

// ---- Crawl -------------------------------------------------------------------------------------

var crawler = new Crawler(client, config, zoneNames);
var (items, sources, report) = await crawler.RunAsync(limit);

// ---- Icons ---------------------------------------------------------------------------------------

if (downloadIcons)
{
    Console.WriteLine();
    Console.WriteLine("Downloading icons...");
    var iconDir = Path.Combine(repoRoot, "site", "assets", "icons");
    // The placeholder is a real icon as far as the site is concerned: items the database has no art
    // for fall back to it rather than showing an empty box, so a local bundle needs it too.
    var wanted = items.Select(i => i.Icon)
        .Where(i => !string.IsNullOrEmpty(i))
        .Append("inv_misc_questionmark")
        .Distinct()
        .ToList();
    var got = 0;
    foreach (var icon in wanted)
    {
        if (await client.DownloadAsync(
                $"https://octowow.st/db/images/icons/medium/{icon}.png",
                Path.Combine(iconDir, icon + ".png")))
        {
            got++;
        }
    }
    Console.WriteLine($"  {got}/{wanted.Count} icons available under site/assets/icons");
}

// ---- Write -----------------------------------------------------------------------------------

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

var ordered = items.OrderBy(i => i.Id).ToList();

// Effect sentences repeat heavily across the database - the same "Increases damage and healing
// done by magical spells and effects by up to 20" is on dozens of items - so they are written once
// into a shared table and referenced by index. That is ~210 KB off the payload for 4,900 lines.
var effectLine = new Regex(@"^(Equip|Use|Chance on hit):", RegexOptions.IgnoreCase);
var effectTable = new List<ItemEffect>();
var effectIndex = new Dictionary<(string Kind, string Text), int>();

int EffectId(ItemEffect effect)
{
    var key = (effect.Kind, effect.Text);
    if (effectIndex.TryGetValue(key, out var existing)) return existing;

    effectIndex[key] = effectTable.Count;
    effectTable.Add(effect);
    return effectTable.Count - 1;
}

var effectRefs = ordered.ToDictionary(i => i.Id, i => i.Effects.Select(EffectId).ToList());

await WriteJsonAsync(Path.Combine(dataDir, "items.json"), new
{
    generated = DateTime.UtcNow.ToString("O"),
    // Icons are PNGs under /db/images/icons/<size>/. The templates path looks plausible but is a
    // catch-all that answers every request with an HTML page and a 200, so it silently "works"
    // while serving no image at all.
    iconBase = "https://octowow.st/db/images/icons/medium/",
    iconExtension = ".png",
    itemUrlBase = "https://octowow.st/db/?item=",
    items = ordered.Select(i => new
    {
        id = i.Id,
        name = i.Name,
        quality = i.Quality,
        ilvl = i.ItemLevel,
        req = i.ReqLevel,
        slot = i.Slot,
        cls = i.ItemClass,
        sub = i.SubClass,
        subName = i.SubClassName,
        icon = i.Icon,
        classes = i.Classes.Count > 0 ? i.Classes : null,
        setId = i.SetId,
        setName = i.SetName,
        // null means "we could not establish when this becomes available". The site excludes those
        // from phase-gated views rather than assuming they are available now.
        minPhase = i.MinPhase == int.MaxValue ? (int?)null : i.MinPhase,
        stats = i.Stats.Count > 0 ? i.Stats.OrderBy(s => s.Key).ToDictionary(s => s.Key, s => Math.Round(s.Value, 2)) : null,
        // Effect lines are already carried verbatim in the shared table, so a note repeating one
        // would be the same sentence twice. Notes keep only what nothing else records.
        notes = i.Notes.Where(n => !effectLine.IsMatch(n)).ToList() is { Count: > 0 } kept ? kept : null,

        // Tooltip presentation. Every one of these is null or absent when the item does not have
        // it, so items with a bare stat line cost nothing extra in the payload.
        bind = i.Binding,
        unique = i.Unique ? true : (bool?)null,
        dur = i.Durability,
        bonusDmg = i.BonusDamage,
        fx = effectRefs[i.Id] is { Count: > 0 } refs ? refs : null
    }),
    effects = effectTable.Select(e => new { k = e.Kind, t = e.Text }),
    // Sets are written once and referenced by setId, rather than repeating the same piece list and
    // bonus text on all eight members.
    sets = crawler.Sets.Count > 0
        ? crawler.Sets.OrderBy(kv => kv.Key).ToDictionary(
            kv => kv.Key.ToString(),
            kv => new
            {
                name = kv.Value.Name,
                total = kv.Value.Total,
                pieces = kv.Value.Pieces,
                bonuses = kv.Value.Bonuses.OrderBy(b => b.Pieces).Select(b => new { n = b.Pieces, t = b.Text })
            })
        : null
}, jsonOptions);

await WriteJsonAsync(Path.Combine(dataDir, "sources.json"), new
{
    generated = DateTime.UtcNow.ToString("O"),
    sources = sources
        .Where(kv => ordered.Any(i => i.Id == kv.Key))
        .OrderBy(kv => kv.Key)
        .ToDictionary(
            kv => kv.Key.ToString(),
            kv => kv.Value
                .OrderBy(s => s.Phase)
                .ThenByDescending(s => s.Percent ?? 0)
                // A world-drop item can list hundreds of sources and the site only ever shows the
                // most accessible one. Keeping a handful cuts this file from roughly 6MB to under
                // 1MB, which matters because the whole dataset is downloaded on first load.
                .Take(MaxSourcesPerItem)
                .Select(s => new
                {
                    kind = s.Kind.ToString().ToLowerInvariant(),
                    id = s.SourceId == 0 ? (int?)null : s.SourceId,
                    name = s.Name,
                    zoneId = s.ZoneId == 0 ? (int?)null : s.ZoneId,
                    zone = s.ZoneName,
                    instance = s.Instance,
                    order = s.Order,
                    percent = s.Percent,
                    cost = s.Cost,
                    token = s.ContainerId,
                    boss = s.Classification >= 3,
                    phase = s.Phase == int.MaxValue ? (int?)null : s.Phase
                })
                .ToList())
}, jsonOptions);

await WriteJsonAsync(Path.Combine(dataDir, "zones.json"), new
{
    generated = DateTime.UtcNow.ToString("O"),
    zones = zoneNames.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
}, jsonOptions);

var unmatchedGrouped = report.UnmatchedLines
    .GroupBy(l => l)
    .OrderByDescending(g => g.Count())
    .Take(80)
    .ToDictionary(g => g.Key, g => g.Count());

await WriteJsonAsync(Path.Combine(dataDir, "meta.json"), new
{
    generated = DateTime.UtcNow.ToString("O"),
    databaseUrl = "https://octowow.st/db/",
    counts = new
    {
        items = ordered.Count,
        itemsWithStats = ordered.Count(i => i.Stats.Count > 0),
        itemsWithSources = ordered.Count(i => sources.ContainsKey(i.Id)),
        npcsCrawled = report.NpcsCrawled,
        itemsDetailed = report.ItemsDetailed,
        prunedStubs = report.PrunedItems,
        skippedAsWorldDrop = report.SkippedAsWorldDrop,
        abandonedSlowPages = client.AbandonedPages,
        retries = client.RetryCount,
        cacheHits = client.CacheHits,
        networkFetches = client.NetworkFetches
    },
    itemsPerPhase = ordered
        .GroupBy(i => i.MinPhase == int.MaxValue ? "unknown" : i.MinPhase.ToString())
        .OrderBy(g => g.Key)
        .ToDictionary(g => g.Key, g => g.Count()),
    itemsWithUnknownPhase = report.ItemsWithUnknownPhase,
    craftSourcesRephased = report.CraftSourcesRephased,
    atlas = new
    {
        itemsFromAtlas = report.AtlasItems,
        recipesSkipped = report.AtlasRecipesSkipped,
        catalogues = report.AtlasCatalogues,
        setItems = report.AtlasSetItems,
        containerItems = report.AtlasContainerItems,
        sets = report.AtlasSets.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
        // A high count here means Atlas knows content the server has not released yet.
        itemsMissingFromDatabase = report.AtlasItemsMissingFromDatabase
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value),
        instances = report.AtlasInstances.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
        warnings = report.AtlasWarnings
    },
    seededZones = report.SeededZones.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
    unresolved = new
    {
        zoneNames = report.UnresolvedZoneNames,
        npcNames = report.UnresolvedNpcNames,
        itemNames = report.UnresolvedItemNames,
        unnamedZoneIds = report.UnnamedZoneIds.OrderBy(id => id).ToList()
    },
    unmatchedTooltipLines = new
    {
        total = report.UnmatchedLines.Count,
        distinct = report.UnmatchedLines.Distinct().Count(),
        top = unmatchedGrouped
    }
}, new JsonSerializerOptions { WriteIndented = true });

Console.WriteLine();
Console.WriteLine($"Done. {ordered.Count} items, {sources.Count} with sources.");
Console.WriteLine($"  {client.CacheHits} cache hits, {client.NetworkFetches} network fetches");
Console.WriteLine($"  {report.UnmatchedLines.Distinct().Count()} distinct unmatched tooltip lines (see data/meta.json)");
if (report.UnresolvedZoneNames.Count > 0)
    Console.WriteLine($"  WARNING: unresolved zone names: {string.Join(", ", report.UnresolvedZoneNames)}");

return 0;

// ---- Local helpers ------------------------------------------------------------------------------

static async Task WriteJsonAsync(string path, object value, JsonSerializerOptions options)
{
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, options));
    var size = new FileInfo(path).Length;
    Console.WriteLine($"  wrote {Path.GetFileName(path)} ({size / 1024.0:F0} KB)");
}

/// <summary>Reads the `zones = { 1: "Dun Morogh", ... }` table out of the database's locale script.</summary>
static Dictionary<int, string> ParseLocaleZones(string js)
{
    var result = new Dictionary<int, string>();
    var at = js.IndexOf("zones = {", StringComparison.Ordinal);
    if (at < 0) return result;

    var cursor = at + "zones = ".Length;
    var table = JsLiteral.ParseObject(js, ref cursor);

    foreach (var (key, value) in table)
        if (int.TryParse(key, out var id) && value is string name)
            result[id] = name;

    return result;
}

static int? ParseLimit(string[] args) => ParseInt(args, "--limit");

static string Describe(object? value) => value switch
{
    null => "null",
    List<object?> list => "[" + string.Join(',', list.Select(Describe)) + "]",
    Dictionary<string, object?> => "{...}",
    JsLiteral.Expression e => $"<{e.Text}>",
    JsLiteral.Hole => "_",
    _ => value.ToString() ?? ""
};

static string? OnlyZone(string[] args) => ParseString(args, "--zone");

/// <summary>Looks for the addon beside the repository, where a WoW install usually sits.</summary>
static string? DefaultAtlasPath()
{
    var candidates = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "OneDrive", "Desktop", "OctoWoW", "Interface", "AddOns", "Atlas-CFM"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     "OctoWoW", "Interface", "AddOns", "Atlas-CFM")
    };
    return candidates.FirstOrDefault(Directory.Exists);
}

static string? ParseString(string[] args, string flag)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int? ParseInt(string[] args, string flag)
{
    var index = Array.IndexOf(args, flag);
    if (index < 0 || index + 1 >= args.Length) return null;
    return int.TryParse(args[index + 1], out var value) ? value : null;
}

/// <summary>Walks up from the executable until it finds the directory holding config/phases.json.</summary>
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "config", "phases.json"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Could not locate the repository root (no config/phases.json found above the executable).");
}
