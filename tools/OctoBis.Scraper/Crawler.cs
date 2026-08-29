using System.Text.Json;

namespace OctoBis.Scraper;

/// <summary>
/// Walks the OctoWoW database and produces the site's data files.
///
/// The crawl runs in rounds rather than a single pass, because neither direction of the data is
/// complete on its own:
///
///   1. Seed      zone -> NPCs from pfQuest, plus explicitly named NPCs and crafted items.
///   2. Loot      each seeded NPC's page yields its drop table and vendor stock.
///   3. Detail    each candidate item's own page yields its stats and its full source list, which
///                reveals bosses that had no spawn entry to seed from (Ragnaros, Nefarian).
///   4. Backfill  newly revealed NPCs inside tracked zones are crawled, and any items they add are
///                detailed in turn.
///
/// Steps 3 and 4 are what let the phase config get away with naming only zones - no boss roster has
/// to be maintained by hand.
/// </summary>
public sealed class Crawler
{
    // Candidate thresholds. Beyond trimming the crawl, these dodge a real performance cliff: a
    // world-drop green is listed as dropped by hundreds of NPCs, and the database takes ~16 seconds
    // to render that item's page cold, against ~0.4s for an ordinary one. Those items are also
    // never best in slot at 60, so excluding them costs nothing and saves hours.
    // This is a level-60 site: pre-raid blues sit around item level 60-66 and raid epics run to 92,
    // so anything below 55 is levelling gear that would never be picked anyway.
    private const int MinItemLevel = 55;
    private const int MinQuality = 3;

    /// <summary>
    /// Legendary. Above it is quality 6, "artifact", which vanilla never uses for anything a player
    /// can obtain - the Warglaives of Azzinoth and the Twin Blades sit there. Not loot.
    /// </summary>
    private const int MaxQuality = 5;

    /// <summary>Drop rates at or above this are treated as real loot rather than a world drop.</summary>
    private const double WorldDropCeiling = 2.0;

    /// <summary>At or above this item level, an item is fetched regardless of its drop rate.</summary>
    private const int RaidItemLevel = 65;

    /// <summary>
    /// How long any single item page gets before it is abandoned. Ordinary pages answer in well
    /// under two seconds; anything that does not is a world drop we do not want. See
    /// <see cref="AowowClient.TryGetItemPageAsync"/>.
    /// </summary>
    private static readonly TimeSpan ItemPageBudget = TimeSpan.FromSeconds(5);

    private static readonly int[] IgnoredSlots = { 0, 4, 18, 19, 24 }; // none, shirt, bag, tabard, ammo

    private readonly AowowClient _client;
    private readonly Config _config;
    private readonly Report _report = new();

    private readonly Dictionary<int, Item> _items = new();
    private readonly Dictionary<int, List<ItemSource>> _sources = new();

    /// <summary>Set blocks, read once from the first piece of each set that is detailed.</summary>
    private readonly Dictionary<int, ItemSetInfo> _sets = new();
    private readonly Dictionary<int, Npc> _npcs = new();
    private readonly Dictionary<int, int> _zoneToPhase = new();

    /// <summary>Item id -> phase implied by the tier set it belongs to. Fallback only.</summary>
    private readonly Dictionary<int, int> _setPhases = new();
    private readonly Dictionary<int, string> _zoneNames;

    public Crawler(AowowClient client, Config config, Dictionary<int, string> zoneNames)
    {
        _client = client;
        _config = config;
        _zoneNames = zoneNames;
    }

    /// <summary>Item sets encountered during the crawl, keyed by set id.</summary>
    public IReadOnlyDictionary<int, ItemSetInfo> Sets => _sets;

    public async Task<(IReadOnlyCollection<Item> Items, Dictionary<int, List<ItemSource>> Sources, Report Report)> RunAsync(int? limit)
    {
        BuildZonePhaseMap();

        ImportAtlas();
        await SeedAsync();
        await CrawlNpcsAsync(limit);
        await DetailItemsAsync(limit);
        await BackfillAsync(limit);

        AssignPhases();
        PruneUndetailed();

        return (_items.Values, _sources, _report);
    }

    // ---- 0. Zone to phase --------------------------------------------------------------------

    private void BuildZonePhaseMap()
    {
        foreach (var phase in _config.Phases)
        {
            foreach (var zoneName in phase.Zones)
            {
                var matches = _zoneNames.Where(kv => kv.Value.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                                        .Select(kv => kv.Key)
                                        .ToList();

                if (matches.Count == 0)
                {
                    _report.UnresolvedZoneNames.Add($"{phase.Key}: {zoneName}");
                    continue;
                }

                // A zone can appear under several ids (instance wings share a name); the earliest
                // phase claiming it wins, so a zone is never gated later than its first appearance.
                foreach (var id in matches)
                    if (!_zoneToPhase.TryGetValue(id, out var existing) || phase.Id < existing)
                        _zoneToPhase[id] = phase.Id;
            }
        }
    }

    // ---- 0b. Atlas ---------------------------------------------------------------------------

    /// <summary>
    /// Brings in the Atlas-CFM loot tables. This runs before the website crawl so that Atlas's
    /// curated boss names, drop rates and instance grouping are already in place when database
    /// sources are merged on top of them.
    /// </summary>
    private void ImportAtlas()
    {
        if (_config.Atlas is null) return;

        var instances = _config.Atlas.LoadInstances();
        foreach (var warning in _config.Atlas.Warnings) _report.AtlasWarnings.Add(warning);

        foreach (var instance in instances)
        {
            var phase = _config.AtlasInstancePhases.GetValueOrDefault(instance.Key, 0);

            foreach (var boss in instance.Bosses)
            foreach (var loot in boss.Loot)
            {
                // Recipes and plans are never gear; skipping them here avoids fetching a page for
                // each one only to discard it.
                if (loot.LooksLikeRecipe) { _report.AtlasRecipesSkipped++; continue; }

                _items.TryAdd(loot.ItemId, new Item { Id = loot.ItemId, Name = loot.Name ?? "" });

                // Container ids are the other half of a chain: the drop that turns into something
                // else. That is how quest rewards like Benediction and Anathema are reachable at
                // all, since nothing drops them directly.
                foreach (var container in loot.ContainerIds)
                    if (_items.TryAdd(container, new Item { Id = container })) _report.AtlasContainerItems++;

                AddSource(loot.ItemId, new ItemSource
                {
                    Kind = SourceKind.Drop,
                    SourceId = 0,
                    Name = boss.Name,
                    ZoneName = instance.Name,
                    Instance = instance.Name,
                    Order = boss.Order,
                    Percent = loot.DropRate,
                    ContainerId = loot.ContainerId,
                    // Atlas lists the encounters an instance actually has, so anything on a boss
                    // table is a boss for our purposes.
                    Classification = 3,
                    Phase = phase
                });
            }

            _report.AtlasInstances[instance.Name] = instance.LootCount;
        }

        // Atlas records a four-number skill range per recipe whose elements are not documented and
        // do not read consistently as "skill required", so only the profession is surfaced. The
        // numbers stay in the importer if someone works out what they mean.
        // Tier set variants, artifacts, legendaries and world blues. Imported without a source so
        // that each item's own page decides where it comes from and which phase it belongs to.
        //
        // The set name is remembered as a fallback, because a tier piece nothing drops still plainly
        // belongs to its raid - a Naxxramas set piece is Naxxramas gear whether or not the crawl
        // found the boss that drops it.
        foreach (var (setName, ids) in _config.Atlas.LoadSets())
        {
            var phase = _config.SetPhaseFor(setName);

            foreach (var id in ids)
            {
                if (_items.TryAdd(id, new Item { Id = id })) _report.AtlasSetItems++;
                if (phase is { } p && !_setPhases.ContainsKey(id)) _setPhases[id] = p;
            }

            _report.AtlasSets[setName] = ids.Count;
        }

        var recipeProducts = _config.Atlas.LoadSpellProducts();
        _report.RecipeProducts = recipeProducts.Count;
        ImportCatalogue("Crafting.lua", SourceKind.Craft,
            entry => (AtlasImporter.ProfessionOf(entry.Category), AtlasImporter.ProfessionOf(entry.Category)),
            AtlasImporter.CraftsGear,
            resolveId: spellId => recipeProducts.TryGetValue(spellId, out var made) ? made : null);

        ImportCatalogue("Factions.lua", SourceKind.Reputation, entry =>
        {
            var faction = AtlasImporter.Prettify(entry.Category);
            // The section header is the standing at which the reward unlocks.
            return (faction, entry.Section is { Length: > 0 } standing ? $"{standing} — {faction}" : faction);
        }, phaseOf: entry => _config.FactionPhaseFor(entry.Category));

        _report.AtlasItems = _items.Count;
        Console.WriteLine($"Atlas: {_report.RecipeProducts} recipes resolved, {_report.UnresolvedRecipes} unresolved");
        Console.WriteLine($"Atlas: {instances.Count} instances, {instances.Sum(i => i.Bosses.Count)} encounters, " +
                          $"{_items.Count} items ({_report.AtlasRecipesSkipped} recipes skipped)");
    }

    /// <summary>
    /// Brings in one of the Tables/ catalogues as a source kind. These have no phase of their own -
    /// crafting and reputation are available from launch - and no drop rate.
    /// </summary>
    /// <param name="resolveId">
    /// Maps a catalogue id onto an item id. Crafting.lua lists recipe <em>spell</em> ids, and the
    /// two namespaces collide - spell 23067 is the Blue Firework recipe, item 23067 is Ring of the
    /// Cryptstalker - so those entries must be resolved through Spells.lua before use.
    /// </param>
    private void ImportCatalogue(
        string fileName,
        SourceKind kind,
        Func<AtlasImporter.CatalogueEntry, (string Group, string Label)> describe,
        Func<string, bool>? categoryFilter = null,
        Func<AtlasImporter.CatalogueEntry, int>? phaseOf = null,
        Func<int, int?>? resolveId = null)
    {
        if (_config.Atlas is null) return;

        var added = 0;
        foreach (var entry in _config.Atlas.LoadCatalogue(fileName))
        {
            if (categoryFilter is not null && !categoryFilter(entry.Category)) continue;
            if (entry.Name is not null &&
                new AtlasImporter.AtlasLoot { Name = entry.Name }.LooksLikeRecipe) continue;

            // Crafting entries are recipe spells; everything else is already an item id.
            var itemId = resolveId is null ? entry.ItemId : resolveId(entry.ItemId);
            if (itemId is null)
            {
                _report.UnresolvedRecipes++;
                continue;
            }

            var (group, label) = describe(entry);

            _items.TryAdd(itemId.Value, new Item { Id = itemId.Value, Name = entry.Name ?? "" });
            AddSource(itemId.Value, new ItemSource
            {
                Kind = kind,
                SourceId = 0,
                Name = label,
                ZoneName = group,
                Instance = group,
                Phase = phaseOf?.Invoke(entry) ?? 0
            });
            added++;
        }

        _report.AtlasCatalogues[fileName] = added;
    }

    // ---- 1. Seed -----------------------------------------------------------------------------

    private async Task SeedAsync()
    {
        foreach (var (zoneId, unitIds) in _config.ZoneUnits)
        {
            if (!_zoneToPhase.TryGetValue(zoneId, out var zonePhase)) continue;
            foreach (var unitId in unitIds)
                Remember(new Npc { Id = unitId, ZoneId = zoneId, Phase = zonePhase });
            _report.SeededZones[ZoneName(zoneId)] = unitIds.Count;
        }

        foreach (var phase in _config.Phases)
        foreach (var npcName in phase.Npcs)
        {
            var resolved = await ResolveNpcByNameAsync(npcName);
            if (resolved is null)
            {
                _report.UnresolvedNpcNames.Add(npcName);
                continue;
            }

            // A named boss belongs to the phase that named it, whatever zone it claims - this is
            // the only phase signal available for encounters that report no location.
            resolved.Phase = phase.Id;
            Remember(resolved);
        }

        // Class quest rewards, which nothing else can lead us to. Their phase comes from their own
        // page like any other item.
        foreach (var itemName in _config.QuestItems)
        {
            var id = await ResolveItemByNameAsync(itemName);
            if (id is null)
            {
                _report.UnresolvedItemNames.Add(itemName);
                continue;
            }

            if (_items.TryAdd(id.Value, new Item { Id = id.Value, Name = itemName })) _report.QuestItems++;
        }

        foreach (var (phaseKey, itemNames) in _config.CraftedItems)
        foreach (var itemName in itemNames)
        {
            var id = await ResolveItemByNameAsync(itemName);
            if (id is null)
            {
                _report.UnresolvedItemNames.Add(itemName);
                continue;
            }

            var phase = _config.Phases.FirstOrDefault(p => p.Key == phaseKey)?.Id ?? 0;
            AddSource(id.Value, new ItemSource
            {
                Kind = SourceKind.Craft,
                SourceId = 0,
                Name = "Crafted",
                Phase = phase
            });
            _items.TryAdd(id.Value, new Item { Id = id.Value, Name = itemName });
        }
    }

    private async Task<Npc?> ResolveNpcByNameAsync(string name)
    {
        var html = await _client.GetSearchPageAsync(name);

        // A search with exactly one hit 302s straight to that entity's own page, so there is no
        // results listview to read - the answer is the page we landed on. Every uniquely named
        // boss takes this path, which is most of them.
        if (PageInfo(html) is { Type: 1 } page)
        {
            return new Npc { Id = page.TypeId, Name = page.Name ?? name };
        }

        var listview = ListviewParser.Find(html, "npcs");
        if (listview is null || listview.Rows.Count == 0) return null;

        // Prefer an exact name match, then the highest classification (3 = boss).
        var row = listview.Rows
            .OrderByDescending(r => string.Equals(JsLiteral.Str(r, "name"), name, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(r => JsLiteral.Int(r, "classification") ?? 0)
            .First();

        return new Npc
        {
            Id = JsLiteral.Int(row, "id") ?? 0,
            Name = JsLiteral.Str(row, "name") ?? name,
            ZoneId = JsLiteral.FirstInt(row, "location") ?? 0,
            Classification = JsLiteral.Int(row, "classification") ?? 0
        };
    }

    /// <summary>Reads the `g_pageInfo` block a database page carries (type 1 = NPC, 3 = item).</summary>
    private static (int Type, int TypeId, string? Name)? PageInfo(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, @"g_pageInfo\s*=\s*\{\s*type:\s*(\d+),\s*typeId:\s*(\d+)(?:,\s*name:\s*'((?:[^'\\]|\\.)*)')?");

        return match.Success
            ? (int.Parse(match.Groups[1].Value),
               int.Parse(match.Groups[2].Value),
               match.Groups[3].Success ? match.Groups[3].Value.Replace("\\'", "'") : null)
            : null;
    }

    private async Task<int?> ResolveItemByNameAsync(string name)
    {
        var html = await _client.GetSearchPageAsync(name);

        // Same single-hit redirect as for NPCs; type 3 is an item.
        if (PageInfo(html) is { Type: 3 } page) return page.TypeId;

        var listview = ListviewParser.Find(html, "items");
        if (listview is null) return null;

        foreach (var row in listview.Rows)
        {
            var rowName = ListviewParser.StripNamePrefix(JsLiteral.Str(row, "name") ?? "");
            if (rowName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return JsLiteral.Int(row, "id");
        }
        return null;
    }

    // ---- 2. Loot -----------------------------------------------------------------------------

    private async Task CrawlNpcsAsync(int? limit)
    {
        var pending = _npcs.Values.Where(n => !n.Crawled).ToList();
        if (limit is { } cap) pending = pending.Take(cap).ToList();

        var done = 0;
        foreach (var npc in pending)
        {
            await CrawlOneNpcAsync(npc);
            if (++done % 50 == 0)
                Console.WriteLine($"  ... {done}/{pending.Count} NPCs ({_client.CacheHits} cached, {_client.NetworkFetches} fetched)");
        }
    }

    private async Task CrawlOneNpcAsync(Npc npc)
    {
        npc.Crawled = true;
        var html = await _client.GetNpcPageAsync(npc.Id);
        if (string.IsNullOrEmpty(html)) return;

        if (string.IsNullOrEmpty(npc.Name))
            npc.Name = ExtractHeading(html) ?? $"NPC {npc.Id}";

        var views = ListviewParser.ParseAll(html);
        _report.NpcsCrawled++;

        foreach (var view in views)
        {
            if (view.Template != "item") continue;

            var kind = view.Id.Contains("sell", StringComparison.OrdinalIgnoreCase) ? SourceKind.Vendor : SourceKind.Drop;

            foreach (var row in view.Rows)
            {
                var itemId = JsLiteral.Int(row, "id");
                if (itemId is null) continue;
                if (!IsCandidate(row)) continue;

                RememberItemStub(itemId.Value, row);
                AddSource(itemId.Value, new ItemSource
                {
                    Kind = kind,
                    SourceId = npc.Id,
                    Name = npc.Name,
                    ZoneId = npc.ZoneId,
                    Classification = npc.Classification,
                    Phase = npc.Phase,
                    Percent = kind == SourceKind.Drop ? Positive(JsLiteral.Num(row, "percent")) : null,
                    Cost = kind == SourceKind.Vendor ? (long?)JsLiteral.FirstInt(row, "cost") : null
                });
            }
        }
    }

    // ---- 3. Detail ---------------------------------------------------------------------------

    /// <summary>
    /// Decides whether an item is worth spending a page fetch on.
    ///
    /// The expensive pages are world drops: an item on hundreds of NPCs' loot tables takes the
    /// database ~15 seconds to render, against ~0.4s for a normal item. They are also the items
    /// least likely to matter, and they are recognisable before fetching by their drop rate - a
    /// world drop lands at around 1.3% off any given mob, while genuine boss loot is 6% or better.
    /// Epics are always fetched regardless, since there are few of them and they are the point.
    /// </summary>
    private bool WorthDetailing(int itemId)
    {
        // Raid-tier item levels are always worth a look, whatever the drop rate. Quality cannot be
        // used here - it is not known until the page is fetched.
        if (_items.TryGetValue(itemId, out var item) && item.ItemLevel >= RaidItemLevel) return true;

        var sources = _sources.GetValueOrDefault(itemId);
        if (sources is null || sources.Count == 0) return true; // seeded by name, no drop data yet

        // Anything Atlas lists against an encounter is curated boss loot, so the world-drop
        // heuristic below must not touch it - plenty of real boss drops sit under 2%.
        if (sources.Any(s => s.Instance is not null)) return true;

        return sources.Any(s => s.Kind != SourceKind.Drop || s.Percent is >= WorldDropCeiling);
    }

    private async Task DetailItemsAsync(int? limit)
    {
        var pending = _items.Values
            .Where(i => !i.DetailFetched)
            .Select(i => i.Id)
            .Where(WorthDetailing)
            .ToList();

        _report.SkippedAsWorldDrop += _items.Values.Count(i => !i.DetailFetched) - pending.Count;
        if (limit is { } cap) pending = pending.Take(cap).ToList();

        Console.WriteLine($"Detailing {pending.Count} of {_items.Count} candidates " +
                          $"(ilvl>={MinItemLevel}; quality>={MinQuality} applied after fetch)");

        var done = 0;
        foreach (var id in pending)
        {
            await DetailOneItemAsync(id);
            if (++done % 50 == 0)
                Console.WriteLine($"  ... {done}/{pending.Count} items ({_client.CacheHits} cached, {_client.NetworkFetches} fetched)");
        }
    }

    private async Task DetailOneItemAsync(int id)
    {
        var html = await _client.TryGetItemPageAsync(id, ItemPageBudget);
        if (string.IsNullOrEmpty(html)) return;

        var parsed = ItemPageParser.Parse(html, id, _report.UnmatchedLines);
        if (parsed is null) return;

        // Merge in the listview-sourced fields the tooltip does not carry, without letting an empty
        // stub value overwrite something the tooltip did resolve.
        if (_items.TryGetValue(id, out var stub))
        {
            parsed.ItemLevel = stub.ItemLevel;
            parsed.ItemClass = stub.ItemClass;
            parsed.SubClass = stub.SubClass;
            if (parsed.Slot == 0) parsed.Slot = stub.Slot;
            if (parsed.ReqLevel == 0) parsed.ReqLevel = stub.ReqLevel;
        }

        // Now that the tooltip has given us the real quality, apply the quality floor, along with
        // the checks that need a resolved slot. Random-suffix gear has no fixed stat line to rank.
        if (parsed.Slot == 0 || IgnoredSlots.Contains(parsed.Slot)
            || parsed.HasRandomSuffix || parsed.Quality < MinQuality || parsed.Quality > MaxQuality)
        {
            _items.Remove(id);
            _sources.Remove(id);
            _report.PrunedItems++;
            return;
        }

        _items[id] = parsed;
        _report.ItemsDetailed++;

        // All members of a set carry the same block, so the first one to arrive settles it.
        if (parsed.SetId is int setId && !_sets.ContainsKey(setId)
            && ItemPageParser.ParseSet(html) is { } setInfo)
        {
            _sets[setId] = setInfo;
        }

        foreach (var view in ListviewParser.ParseAll(html))
        {
            var kind = Classify(view);
            if (kind is null) continue;

            foreach (var row in view.Rows)
            {
                var sourceId = JsLiteral.Int(row, "id");
                if (sourceId is null) continue;

                var zoneId = JsLiteral.FirstInt(row, "location") ?? 0;
                var name = JsLiteral.Str(row, "name") ?? "";
                if (view.Template == "item") name = ListviewParser.StripNamePrefix(name);

                // An item page reports the dropping NPC but often no zone. Fall back to whatever we
                // already know about that NPC from seeding.
                // Only for NPC-backed sources. Quest and object ids live in their own id spaces, so
                // a quest id that happens to equal an NPC id would inherit that NPC's zone and phase
                // - which is how the Cryptstalker quest was placed in Blackrock Depths, and with it
                // the whole of hunter tier 3 into the launch planner.
                var known = kind is SourceKind.Drop or SourceKind.Vendor
                    ? _npcs.GetValueOrDefault(sourceId.Value)
                    : null;
                if (zoneId == 0 && known is not null) zoneId = known.ZoneId;

                AddSource(id, new ItemSource
                {
                    Kind = kind.Value,
                    SourceId = sourceId.Value,
                    Name = name,
                    ZoneId = zoneId,
                    Classification = JsLiteral.Int(row, "classification") ?? 0,
                    Phase = known?.Phase ?? int.MaxValue,
                    Percent = kind == SourceKind.Drop ? Positive(JsLiteral.Num(row, "percent")) : null
                });

                // A source we have not seen becomes a crawl candidate for the backfill round.
                if (kind is SourceKind.Drop or SourceKind.Vendor && !_npcs.ContainsKey(sourceId.Value))
                {
                    Remember(new Npc
                    {
                        Id = sourceId.Value,
                        Name = name,
                        ZoneId = zoneId,
                        Classification = JsLiteral.Int(row, "classification") ?? 0
                    });
                }
            }
        }
    }

    private static SourceKind? Classify(ListviewParser.Listview view) => view.Template switch
    {
        "npc" when view.Id.Contains("sold", StringComparison.OrdinalIgnoreCase) => SourceKind.Vendor,
        "npc" when view.Id.Contains("dropped", StringComparison.OrdinalIgnoreCase) => SourceKind.Drop,
        "quest" when view.Id.Contains("reward", StringComparison.OrdinalIgnoreCase) => SourceKind.Quest,
        "object" => SourceKind.Object,
        "spell" when view.Id.Contains("created", StringComparison.OrdinalIgnoreCase) => SourceKind.Craft,
        _ => null
    };

    // ---- 4. Backfill -------------------------------------------------------------------------

    private async Task BackfillAsync(int? limit)
    {
        // Only chase NPCs that sit inside content we actually track; the rest is world noise.
        var pending = _npcs.Values
            .Where(n => !n.Crawled && _zoneToPhase.ContainsKey(n.ZoneId))
            .ToList();

        if (limit is { } cap) pending = pending.Take(cap).ToList();
        if (pending.Count == 0) return;

        Console.WriteLine($"Backfill: {pending.Count} newly revealed NPCs inside tracked zones");

        var done = 0;
        foreach (var npc in pending)
        {
            await CrawlOneNpcAsync(npc);
            if (++done % 50 == 0)
                Console.WriteLine($"  ... {done}/{pending.Count} NPCs ({_client.CacheHits} cached, {_client.NetworkFetches} fetched)");
        }

        await DetailItemsAsync(limit);
    }

    // ---- 5. Phases and pruning ---------------------------------------------------------------

    private void AssignPhases()
    {
        foreach (var (itemId, sources) in _sources)
        {
            foreach (var source in sources)
            {
                if (source.Instance is not null)
                {
                    // Atlas assigns the source to a named instance, and the phase config maps that
                    // instance directly. That beats anything inferred from a zone id: zone labels
                    // are guesswork for the custom instances, and a wrong one silently mis-phases
                    // a whole raid's loot.
                    if (source.ZoneName is null && source.ZoneId != 0) source.ZoneName = ZoneName(source.ZoneId);
                    continue;
                }

                // Website-only sources: the zone is all we have. If it tells us nothing, the phase
                // stays unknown - it is emphatically not "available at launch".
                var fromZone = _zoneToPhase.TryGetValue(source.ZoneId, out var zonePhase) ? zonePhase : int.MaxValue;
                source.Phase = Math.Min(source.Phase, fromZone);

                source.ZoneName = source.ZoneId == 0 ? null : ZoneName(source.ZoneId);
                if (source.ZoneId != 0 && !_zoneNames.ContainsKey(source.ZoneId))
                    _report.UnnamedZoneIds.Add(source.ZoneId);
            }

            // A crafted item is only available when its recipe is, and recipe availability is not
            // in the data. Where an item is both craftable and dropped, the drop tells us the era
            // it belongs to: Yshgo'lar is listed as an Engineering craft but its only other source
            // is C'Thun, so the pattern is plainly Ahn'Qiraj-gated rather than launch.
            var gated = sources.Where(s => s.Kind is not (SourceKind.Craft or SourceKind.Reputation)
                                           && s.Phase != int.MaxValue)
                               .Select(s => s.Phase)
                               .ToList();

            if (gated.Count > 0)
            {
                foreach (var source in sources.Where(s => s.Kind == SourceKind.Craft && s.Phase == 0))
                {
                    source.Phase = gated.Min();
                    _report.CraftSourcesRephased++;
                }
            }

            if (_items.TryGetValue(itemId, out var item))
            {
                var known = sources.Where(s => s.Phase != int.MaxValue).Select(s => s.Phase).ToList();
                if (known.Count > 0) item.MinPhase = known.Min();
            }
        }

        // Anything still unknown gets one last chance from the tier set it belongs to, then stays
        // unknown. It must never fall back to launch: doing that put Naxxramas and Ahn'Qiraj gear
        // into the launch planner, which is worse than the item being absent.
        foreach (var item in _items.Values)
        {
            if (item.MinPhase != int.MaxValue) continue;

            if (_setPhases.TryGetValue(item.Id, out var setPhase))
            {
                item.MinPhase = setPhase;
                continue;
            }

            // A quest reward that neither a zone nor a set can place is taken as available now:
            // quests ship with the build, and the phases here are raid releases, not quest releases.
            //
            // Below epic quality only. Epic and legendary quest rewards are endgame almost by
            // definition and their quests are the ones this cannot place - Atiesh, Thunderfury, the
            // Blessed Qiraji weapons, the Shifting Sands jewellery. Letting the rule reach them put
            // a Naxxramas legendary in the launch planner and had 101 class/spec/phase combinations
            // equipping it. They stay unknown until something can phase them properly.
            if (item.Quality < 4
                && _sources.TryGetValue(item.Id, out var itemSources)
                && itemSources.Any(s => s.Kind == SourceKind.Quest))
            {
                item.MinPhase = 0;
                _report.QuestRewardsPhasedAtLaunch++;
                continue;
            }

            _report.ItemsWithUnknownPhase++;
        }
    }

    /// <summary>
    /// Drops anything we never managed to detail - a stub with no stats is not usable.
    ///
    /// When Atlas lists an item the database has never heard of, that is a signal worth keeping:
    /// it means the addon knows about content OctoWoW has not shipped yet. Those are counted per
    /// instance so an empty phase can be explained rather than just looking broken.
    /// </summary>
    private void PruneUndetailed()
    {
        foreach (var id in _items.Where(kv => !kv.Value.DetailFetched).Select(kv => kv.Key).ToList())
        {
            foreach (var instance in _sources.GetValueOrDefault(id)?
                         .Select(s => s.Instance)
                         .Where(name => name is not null)
                         .Distinct() ?? Enumerable.Empty<string?>())
            {
                _report.AtlasItemsMissingFromDatabase[instance!] =
                    _report.AtlasItemsMissingFromDatabase.GetValueOrDefault(instance!) + 1;
            }

            _items.Remove(id);
            _sources.Remove(id);
            _report.PrunedItems++;
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static bool IsCandidate(Dictionary<string, object?> row)
    {
        var itemClass = JsLiteral.Int(row, "classs") ?? -1;
        if (itemClass is not (2 or 4)) return false;

        // Drop and vendor listviews omit `slot` entirely - only the item browse pages carry it - so
        // an absent slot must not count against the item. The real slot arrives with the tooltip.
        if (JsLiteral.Int(row, "slot") is { } slot && IgnoredSlots.Contains(slot)) return false;

        // Quality is deliberately not tested here: listviews do not carry it (see StripNamePrefix).
        // Item level is the only quality-ish signal available before fetching the page, and the
        // real quality check happens after the tooltip is parsed.
        return (JsLiteral.Int(row, "level") ?? 0) >= MinItemLevel;
    }

    private void RememberItemStub(int id, Dictionary<string, object?> row)
    {
        if (_items.ContainsKey(id)) return;

        // Quality is left unset: it is not available from a listview, and the tooltip fills it in.
        _items[id] = new Item
        {
            Id = id,
            Name = ListviewParser.StripNamePrefix(JsLiteral.Str(row, "name") ?? ""),
            ItemLevel = JsLiteral.Int(row, "level") ?? 0,
            ReqLevel = JsLiteral.Int(row, "reqlevel") ?? 0,
            Slot = JsLiteral.Int(row, "slot") ?? 0,
            ItemClass = JsLiteral.Int(row, "classs") ?? 0,
            SubClass = JsLiteral.Int(row, "subclass") ?? 0
        };
    }

    private void Remember(Npc npc)
    {
        if (npc.Id == 0) return;
        if (_npcs.TryGetValue(npc.Id, out var existing))
        {
            if (existing.ZoneId == 0) existing.ZoneId = npc.ZoneId;
            if (string.IsNullOrEmpty(existing.Name)) existing.Name = npc.Name;
            existing.Classification = Math.Max(existing.Classification, npc.Classification);
            existing.Phase = Math.Min(existing.Phase, npc.Phase);
            return;
        }
        _npcs[npc.Id] = npc;
    }

    private void AddSource(int itemId, ItemSource source)
    {
        if (!_sources.TryGetValue(itemId, out var list))
            _sources[itemId] = list = new List<ItemSource>();

        // Match on id when both sides have one, otherwise on name. Atlas identifies bosses by name
        // and the website by id, so without the name fallback the same boss would appear twice.
        var existing = list.FirstOrDefault(s => s.Key == source.Key)
                       ?? list.FirstOrDefault(s => s.NameKey == source.NameKey
                                                   && !string.IsNullOrWhiteSpace(source.Name));

        if (existing is not null)
        {
            // Adopt an id from whichever side has one, so the UI can still link to the database.
            if (existing.SourceId == 0 && source.SourceId != 0) existing.SourceId = source.SourceId;

            // Atlas metadata wins where the website has nothing to say.
            existing.Instance ??= source.Instance;
            existing.Order ??= source.Order;
            existing.ContainerId ??= source.ContainerId;
        }
        if (existing is not null)
        {
            // Prefer the richer record: zone, drop chance and classification are not present on
            // every listing. Classification in particular is only reported by item-page listviews,
            // so a boss seeded from spawn data would otherwise never be flagged as one.
            if (existing.ZoneId == 0) existing.ZoneId = source.ZoneId;
            existing.Percent ??= source.Percent;
            existing.Cost ??= source.Cost;
            existing.Classification = Math.Max(existing.Classification, source.Classification);
            existing.Phase = Math.Min(existing.Phase, source.Phase);
            if (string.IsNullOrEmpty(existing.Name)) existing.Name = source.Name;
            return;
        }
        list.Add(source);
    }

    private static double? Positive(double? value) => value is > 0 ? value : null;

    private string ZoneName(int id) => _zoneNames.GetValueOrDefault(id, $"Zone {id}");

    /// <summary>
    /// Reads the NPC's name from the page. There are two h1 elements: the site header renders
    /// "Flamegor - NPCs" and the content heading renders just "Flamegor", so take the last one.
    /// </summary>
    private static string? ExtractHeading(string html)
    {
        string? heading = null;
        var search = 0;

        while (true)
        {
            var start = html.IndexOf("<h1>", search, StringComparison.Ordinal);
            if (start < 0) break;
            start += 4;

            var end = html.IndexOf("</h1>", start, StringComparison.Ordinal);
            if (end < 0) break;

            heading = System.Net.WebUtility.HtmlDecode(html[start..end]).Trim();
            search = end;
        }

        return heading;
    }
}

/// <summary>Diagnostics for the run, written to data/meta.json.</summary>
public sealed class Report
{
    public int NpcsCrawled { get; set; }
    public int ItemsDetailed { get; set; }
    public int PrunedItems { get; set; }
    public int SkippedAsWorldDrop { get; set; }
    public List<string> UnmatchedLines { get; } = new();
    public List<string> UnresolvedZoneNames { get; } = new();
    public List<string> UnresolvedNpcNames { get; } = new();
    public List<string> UnresolvedItemNames { get; } = new();

    /// <summary>Zone ids that appeared in the data with no name in any source. Add them to
    /// zoneIdOverrides in config/phases.json to give them a label.</summary>
    public HashSet<int> UnnamedZoneIds { get; } = new();

    public List<string> AtlasWarnings { get; } = new();
    public Dictionary<string, int> AtlasInstances { get; } = new();
    public Dictionary<string, int> AtlasCatalogues { get; } = new();
    public Dictionary<string, int> AtlasSets { get; } = new();
    public int AtlasSetItems { get; set; }
    public int AtlasContainerItems { get; set; }
    public int QuestItems { get; set; }
    public int ItemsWithUnknownPhase { get; set; }

    /// <summary>Quest rewards no zone or set could place, taken as available now.</summary>
    public int QuestRewardsPhasedAtLaunch { get; set; }
    public int CraftSourcesRephased { get; set; }

    /// <summary>Recipe spells Spells.lua does not say the product of.</summary>
    public int UnresolvedRecipes { get; set; }

    /// <summary>Recipe spell to item mappings read from Spells.lua.</summary>
    public int RecipeProducts { get; set; }

    /// <summary>Instance -> count of items Atlas lists that the database has no entry for.</summary>
    public Dictionary<string, int> AtlasItemsMissingFromDatabase { get; } = new();
    public int AtlasRecipesSkipped { get; set; }
    public int AtlasItems { get; set; }
    public Dictionary<string, int> SeededZones { get; } = new();
}

/// <summary>The parts of config/phases.json the scraper needs, plus the derived zone-unit seed.</summary>
public sealed class Config
{
    public List<PhaseConfig> Phases { get; init; } = new();
    public Dictionary<string, List<string>> CraftedItems { get; init; } = new();
    public List<string> QuestItems { get; init; } = new();

    /// <summary>Set-name prefix -> phase, longest prefix wins. From config's atlasSetPhases.</summary>
    public Dictionary<string, int> SetPhases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Faction name -> the phase its reputation becomes available in.</summary>
    public Dictionary<string, int> FactionPhases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Looks a faction up ignoring spacing and case. Atlas's keys do not always split into words
    /// cleanly - "WardensofTime" comes out as "Wardensof Time" - and a config entry silently failing
    /// to match is exactly how raid reputation ends up looking like launch content.
    /// </summary>
    public int FactionPhaseFor(string faction)
    {
        var needle = Squash(faction);
        foreach (var (name, phase) in FactionPhases)
            if (Squash(name) == needle) return phase;
        return 0;
    }

    private static string Squash(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The phase a set implies, or null when the set spans phases or is unknown.</summary>
    public int? SetPhaseFor(string setName)
    {
        var match = SetPhases.Keys
            .Where(prefix => setName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault();

        return match is null ? null : SetPhases[match];
    }
    public Dictionary<int, HashSet<int>> ZoneUnits { get; init; } = new();
    public Dictionary<int, string> ZoneIdOverrides { get; init; } = new();

    /// <summary>Set when an Atlas-CFM addon folder was supplied; null disables the import.</summary>
    public AtlasImporter? Atlas { get; set; }

    /// <summary>Atlas instance key -> phase. Instances not listed default to launch.</summary>
    public Dictionary<string, int> AtlasInstancePhases { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Config Load(string phasesJson, Dictionary<int, HashSet<int>> zoneUnits)
    {
        using var doc = JsonDocument.Parse(phasesJson);
        var root = doc.RootElement;

        var phases = new List<PhaseConfig>();
        foreach (var p in root.GetProperty("phases").EnumerateArray())
        {
            phases.Add(new PhaseConfig
            {
                Id = p.GetProperty("id").GetInt32(),
                Key = p.GetProperty("key").GetString() ?? "",
                Name = p.GetProperty("name").GetString() ?? "",
                Date = p.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                Status = p.GetProperty("status").GetString() ?? "",
                Blurb = p.TryGetProperty("blurb", out var b) ? b.GetString() : null,
                Headline = p.TryGetProperty("headline", out var h) ? h.GetString() : null,
                Zones = StringList(p, "zones"),
                Npcs = StringList(p, "npcs")
            });
        }

        var crafted = new Dictionary<string, List<string>>();
        if (root.TryGetProperty("craftedItems", out var craftedElement))
            foreach (var entry in craftedElement.EnumerateObject())
                crafted[entry.Name] = entry.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToList();

        var overrides = new Dictionary<int, string>();
        if (root.TryGetProperty("zoneIdOverrides", out var overrideElement))
            foreach (var entry in overrideElement.EnumerateObject())
                if (int.TryParse(entry.Name, out var zoneId))
                    overrides[zoneId] = entry.Value.GetString() ?? "";

        var questItems = root.TryGetProperty("questItems", out var questElement) && questElement.ValueKind == JsonValueKind.Array
            ? questElement.EnumerateArray().Select(v => v.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : new List<string>();

        var config = new Config
        {
            Phases = phases,
            CraftedItems = crafted,
            QuestItems = questItems,
            ZoneUnits = zoneUnits,
            ZoneIdOverrides = overrides
        };

        if (root.TryGetProperty("factionPhases", out var factionPhases) && factionPhases.ValueKind == JsonValueKind.Object)
            foreach (var entry in factionPhases.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Number)
                    config.FactionPhases[entry.Name] = entry.Value.GetInt32();

        if (root.TryGetProperty("atlasSetPhases", out var setPhases) && setPhases.ValueKind == JsonValueKind.Object)
            foreach (var entry in setPhases.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Number)
                    config.SetPhases[entry.Name] = entry.Value.GetInt32();

        foreach (var p in root.GetProperty("phases").EnumerateArray())
        {
            if (!p.TryGetProperty("atlasInstances", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            var id = p.GetProperty("id").GetInt32();
            foreach (var entry in list.EnumerateArray())
                if (entry.GetString() is { Length: > 0 } key)
                    config.AtlasInstancePhases[key] = id;
        }

        return config;
    }

    private static List<string> StringList(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(v => v.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : new List<string>();
}
