using static OctoBis.Scraper.LuaTable;

namespace OctoBis.Scraper;

/// <summary>
/// Reads the loot database shipped with the Atlas-CFM addon.
///
/// This is a far better source of "which boss drops what" than crawling the website: it is curated
/// for this server family, it carries drop rates, it names and orders the encounters, and it keeps
/// each instance separate - including the custom ones the database's zone data cannot distinguish.
/// What it does not carry is item stats, so the website is still needed for those.
///
/// Entries are tagged with the servers they apply to. OctoWoW runs client 1.18.1, which Atlas
/// identifies as the "Turtle WoW" profile, and that profile inherits content marked for 1.17.2.
/// </summary>
public sealed class AtlasImporter
{
    private const string ProfileTurtle = "Turtle WoW";
    private const string ProfileTurtle1 = "Turtle WoW 1.17.2";

    private readonly string _dataRoot;
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Warnings => _warnings;

    public AtlasImporter(string addonRoot)
    {
        _dataRoot = Path.Combine(addonRoot, "CFMLoot", "Data");
    }

    public sealed class AtlasLoot
    {
        public int ItemId { get; init; }
        public double? DropRate { get; init; }
        /// <summary>Tokens or chain items linked to this drop. See ReadContainers.</summary>
        public List<int> ContainerIds { get; init; } = new();

        /// <summary>The token itself, for display; a chain may list more than one.</summary>
        public int? ContainerId => ContainerIds.Count > 0 ? ContainerIds[0] : null;
        /// <summary>Name from the source comment. Advisory only - the database is authoritative.</summary>
        public string? Name { get; init; }

        /// <summary>Recipes and plans make up a large share of the entries and are never gear.</summary>
        public bool LooksLikeRecipe =>
            Name is not null &&
            RecipePrefixes.Any(p => Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        private static readonly string[] RecipePrefixes =
            { "Formula:", "Plans:", "Pattern:", "Recipe:", "Schematic:", "Design:", "Manual:" };
    }

    public sealed class AtlasBoss
    {
        public string Name { get; init; } = "";
        /// <summary>Encounter order within the instance, assigned after non-boss entries are dropped.</summary>
        public int Order { get; set; }
        public List<AtlasLoot> Loot { get; } = new();
    }

    public sealed class AtlasInstance
    {
        /// <summary>The Lua key, e.g. "LowerKarazhan". Stable, and what config/phases.json refers to.</summary>
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Location { get; init; }
        public string? Acronym { get; init; }
        public int? MaxPlayers { get; init; }
        public bool Attunement { get; init; }
        public List<AtlasBoss> Bosses { get; } = new();

        public int LootCount => Bosses.Sum(b => b.Loot.Count);
    }

    /// <summary>One entry from a Tables/ catalogue: crafted gear, or a reputation reward.</summary>
    public sealed class CatalogueEntry
    {
        public int ItemId { get; init; }
        public string Category { get; init; } = "";
        /// <summary>The nearest preceding section header, e.g. "Honored" or "Cloth".</summary>
        public string? Section { get; init; }
        /// <summary>Profession skill needed, from the first element of a `skill` list.</summary>
        public int? Skill { get; init; }
        public string? Name { get; init; }
    }

    /// <summary>
    /// Reads one of the Tables/ files. They share a shape: a single local table whose keys are
    /// category names and whose values are ordered entry lists, where a <c>{ name = ... }</c> entry
    /// is a section header applying to everything that follows it rather than an item.
    /// </summary>
    public List<CatalogueEntry> LoadCatalogue(string fileName)
    {
        var path = Path.Combine(_dataRoot, "Tables", fileName);
        var entries = new List<CatalogueEntry>();

        if (!File.Exists(path))
        {
            _warnings.Add($"Atlas table missing: {path}");
            return entries;
        }

        Dictionary<string, object?> assignments;
        try
        {
            assignments = ParseFile(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _warnings.Add($"{fileName}: {ex.Message}");
            return entries;
        }

        // The file assigns one local table; take the largest, which is the catalogue itself.
        var catalogue = assignments.Values.OfType<Table>()
            .OrderByDescending(t => t.Fields.Count)
            .FirstOrDefault();

        if (catalogue is null)
        {
            _warnings.Add($"{fileName}: no catalogue table found");
            return entries;
        }

        foreach (var (category, value) in catalogue.Fields)
        {
            if (value is not Table list) continue;

            string? section = null;
            foreach (var item in list.Array)
            {
                if (item is not Table row) continue;
                if (!IsVisible(row.Field("servers"))) continue;

                if (row.Int("id") is not { } itemId)
                {
                    // A header carries a name and no id; it labels everything after it.
                    if (row.Text("name") is { Length: > 0 } header) section = header;
                    continue;
                }

                entries.Add(new CatalogueEntry
                {
                    ItemId = itemId,
                    Category = category,
                    Section = section,
                    Skill = (row.Field("skill") as Table)?.Array.OfType<double>().Select(d => (int)d).FirstOrDefault(),
                    Name = row.TrailingComment
                });
            }
        }

        return entries;
    }

    /// <summary>
    /// Enchanting categories mapped onto the paperdoll slots they apply to.
    ///
    /// Head and leg enchants are absent here on purpose: in vanilla those are not enchanting at
    /// all, they are reputation-bought items, so they come from config rather than this catalogue.
    /// </summary>
    private static readonly Dictionary<string, string[]> EnchantSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EnchantingCloak"] = new[] { "back" },
        ["EnchantingChest"] = new[] { "chest" },
        ["EnchantingBracer"] = new[] { "wrist" },
        ["EnchantingGlove"] = new[] { "hands" },
        ["EnchantingBoots"] = new[] { "feet" },
        ["EnchantingWeapon"] = new[] { "mainhand", "offhand" },
        ["Enchanting2HWeapon"] = new[] { "mainhand" },
        ["EnchantingShield"] = new[] { "offhand" }
    };

    public sealed class Enchant
    {
        public int SpellId { get; init; }
        public string Name { get; init; } = "";
        public string[] Slots { get; init; } = Array.Empty<string>();
        public bool TwoHandOnly { get; init; }
        public bool ShieldOnly { get; init; }
    }

    /// <summary>Every enchant the Turtle profile can apply, grouped by the slot it goes on.</summary>
    public List<Enchant> LoadEnchants()
    {
        var enchants = new List<Enchant>();

        foreach (var entry in LoadCatalogue("Crafting.lua"))
        {
            if (!EnchantSlots.TryGetValue(entry.Category, out var slots)) continue;

            // The comment names the enchant; without it there is nothing to show a user.
            var name = entry.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            // Strip the "Enchant Gloves - " style prefix so the list reads as effects.
            var dash = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && name.StartsWith("Enchant ", StringComparison.OrdinalIgnoreCase))
                name = name[(dash + 3)..].Trim();

            // Comments often carry the patch an enchant arrived in ("Agility 1.18.1"); that is a
            // note to the addon's maintainers, not part of the name.
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+\d+\.\d+(\.\d+)?$", "").Trim();

            enchants.Add(new Enchant
            {
                SpellId = entry.ItemId,
                Name = name,
                Slots = slots,
                TwoHandOnly = entry.Category.Equals("Enchanting2HWeapon", StringComparison.OrdinalIgnoreCase),
                ShieldOnly = entry.Category.Equals("EnchantingShield", StringComparison.OrdinalIgnoreCase)
            });
        }

        return enchants;
    }

    /// <summary>
    /// Every item id named in Sets.lua, grouped by the set it belongs to.
    ///
    /// This matters more than the file's name suggests. OctoWoW ships several variants of each tier
    /// set - Priest tier 1 alone is Vestments, Regalia and Attire of Prophecy - and none of the
    /// variants beyond the first are reachable by crawling boss loot, because the addon is where
    /// they are enumerated. It also carries artifacts, legendaries and the world blue/epic lists.
    ///
    /// No phase is assigned here on purpose: these are imported as candidates, and each item's own
    /// page supplies its real sources and therefore its real phase.
    /// </summary>
    public Dictionary<string, List<int>> LoadSets()
    {
        var sets = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var entry in LoadCatalogue("Sets.lua"))
        {
            if (!sets.TryGetValue(entry.Category, out var ids))
                sets[entry.Category] = ids = new List<int>();

            if (!ids.Contains(entry.ItemId)) ids.Add(entry.ItemId);
        }

        return sets;
    }

    public List<AtlasInstance> LoadInstances()
    {
        var instances = new List<AtlasInstance>();

        foreach (var directory in new[] { "Instances", "World" })
        {
            var path = Path.Combine(_dataRoot, directory);
            if (!Directory.Exists(path))
            {
                _warnings.Add($"Atlas data directory missing: {path}");
                continue;
            }

            foreach (var file in Directory.GetFiles(path, "*.lua").OrderBy(f => f))
            {
                foreach (var instance in ReadInstanceFile(file))
                    instances.Add(instance);
            }
        }

        return instances;
    }

    private IEnumerable<AtlasInstance> ReadInstanceFile(string file)
    {
        Dictionary<string, object?> assignments;
        try
        {
            assignments = ParseFile(File.ReadAllText(file));
        }
        catch (Exception ex)
        {
            _warnings.Add($"{Path.GetFileName(file)}: {ex.Message}");
            yield break;
        }

        foreach (var (target, value) in assignments)
        {
            if (!target.StartsWith("AtlasCFM.InstanceData.", StringComparison.Ordinal)) continue;
            if (value is not Table table) continue;

            var key = target["AtlasCFM.InstanceData.".Length..];

            // An instance can itself be restricted to particular servers.
            if (!IsVisible(table.Field("Servers"))) continue;

            var instance = new AtlasInstance
            {
                Key = key,
                Name = table.Text("Name") ?? key,
                Location = table.Text("Location"),
                Acronym = table.Text("Acronym"),
                MaxPlayers = table.Int("MaxPlayers"),
                Attunement = table.Field("Attunement") is true
            };

            if (table.TableField("Bosses") is { } bosses)
            {
                var seen = 0;
                foreach (var entry in bosses.Array)
                {
                    if (entry is not Table bossTable) continue;
                    if (!IsVisible(bossTable.Field("servers") ?? bossTable.Field("Servers"))) continue;

                    // Entries without loot are map annotations - entrances, connections, and the
                    // like - lettered rather than numbered in the addon's own display.
                    var boss = ReadBoss(bossTable, ++seen);
                    if (boss.Loot.Count == 0) continue;

                    boss.Order = instance.Bosses.Count + 1;
                    instance.Bosses.Add(boss);
                }
            }

            if (instance.Bosses.Count > 0) yield return instance;
        }
    }

    private AtlasBoss ReadBoss(Table bossTable, int order)
    {
        var boss = new AtlasBoss
        {
            Name = bossTable.Text("name") ?? bossTable.Text("id") ?? $"Encounter {order}",
            Order = order
        };

        // A boss-level default drop rate applies to any loot entry that does not state its own.
        var defaultRate = bossTable.TableField("defaults")?.Number("dropRate");

        if (bossTable.TableField("loot") is not { } loot) return boss;

        foreach (var entry in loot.Array)
        {
            // Empty tables are layout separators in the addon's UI.
            if (entry is not Table lootTable || lootTable.Fields.Count == 0) continue;
            if (lootTable.Int("id") is not { } itemId) continue;
            if (!IsVisible(lootTable.Field("servers"))) continue;

            boss.Loot.Add(new AtlasLoot
            {
                ItemId = itemId,
                DropRate = lootTable.Number("dropRate") ?? defaultRate,
                ContainerIds = ReadContainers(lootTable.Field("container")),
                Name = lootTable.TrailingComment
            });
        }

        return boss;
    }

    /// <summary>
    /// container is either a bare id list or a list of {id, servers} tables - the latter when a
    /// token differs between servers.
    ///
    /// Every visible id is returned, not just the first: a chain can branch, and The Eye of Divinity
    /// listing <c>{ 18608, 18609 }</c> is exactly that - Benediction and Anathema, two ends of the
    /// same quest. Taking only the first silently lost one of them.
    /// </summary>
    private List<int> ReadContainers(object? container)
    {
        var ids = new List<int>();
        if (container is not Table table) return ids;

        foreach (var entry in table.Array)
        {
            switch (entry)
            {
                case double id:
                    ids.Add((int)id);
                    break;
                case Table inner when IsVisible(inner.Field("servers")):
                    if (inner.Array.FirstOrDefault() is double innerId) ids.Add((int)innerId);
                    else if (inner.Int("id") is { } namedId) ids.Add(namedId);
                    break;
            }
        }

        return ids;
    }

    /// <summary>
    /// Mirrors Atlas's own visibility rule for the Turtle profile: a "!Server" entry denies and
    /// wins outright; otherwise, if any allow entries exist, at least one must match. No list at
    /// all means visible everywhere.
    /// </summary>
    internal static bool IsVisible(object? servers)
    {
        if (servers is not Table table) return true;

        var hasAllowList = false;
        var allowed = false;

        foreach (var entry in table.Array)
        {
            var name = ServerName(entry);
            if (name is null) continue;

            var deny = name.StartsWith('!');
            if (deny) name = name[1..];

            var strict = name.StartsWith('=');
            if (strict) name = name[1..];

            var matches = name == ProfileTurtle
                          // 1.18.1 inherits content marked for 1.17.2 unless the entry is strict.
                          || (!strict && name == ProfileTurtle1);

            if (!matches)
            {
                if (!deny) hasAllowList = true;
                continue;
            }

            if (deny) return false;
            hasAllowList = true;
            allowed = true;
        }

        return !hasAllowList || allowed;
    }

    private static readonly string[] Professions =
    {
        "Alchemy", "Enchanting", "Smithing", "Armorsmith", "Weaponsmith", "Axesmith", "Hammersmith",
        "Swordsmith", "Leather", "Dragonscale", "Elemental", "Tribal", "Tailoring", "Engineering",
        "Gnomish", "Goblin", "Mining", "Smelting", "Skinning", "Herbalism", "Cooking", "FirstAid",
        "Survival", "Gardening", "Jewelcrafting", "Poisons", "Fishing", "Lockpicking", "Cloth", "Disguise"
    };

    /// <summary>
    /// Whether a crafting category can produce equippable gear.
    ///
    /// The crafting catalogue is mostly consumables - potions, enchants, food, poisons, bolts of
    /// cloth. Importing those means fetching a page for each only to discard it once the tooltip
    /// shows it has no slot, so they are filtered out by category instead.
    /// </summary>
    public static bool CraftsGear(string category)
    {
        foreach (var profession in NonGearProfessions)
            if (category.StartsWith(profession, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var keyword in NonGearKeywords)
            if (category.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private static readonly string[] NonGearProfessions =
    {
        "Alchemy", "Enchanting", "Cooking", "FirstAid", "Fishing", "Herbalism", "Mining",
        "Smelting", "Skinning", "Poisons", "Lockpicking", "Disguise", "Gardening", "Survival", "Cloth"
    };

    private static readonly string[] NonGearKeywords =
    {
        "Bags", "Misc", "Explosives", "Parts", "Shirt", "Gemstones", "Gemology", "Goldsmithing",
        "Transmutes", "Flasks", "Pots", "Buckles", "Disenchant", "Woodcutting"
    };

    /// <summary>Maps a crafting category key such as "SmithingChest" onto its profession.</summary>
    public static string ProfessionOf(string category)
    {
        var match = Professions.FirstOrDefault(p => category.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        return match switch
        {
            null => Prettify(category),
            "Smithing" or "Armorsmith" or "Weaponsmith" or "Axesmith" or "Hammersmith" or "Swordsmith" => "Blacksmithing",
            "Leather" or "Dragonscale" or "Elemental" or "Tribal" => "Leatherworking",
            "Gnomish" or "Goblin" => "Engineering",
            "FirstAid" => "First Aid",
            "Smelting" => "Mining",
            _ => match
        };
    }

    /// <summary>Turns a run-together key such as "ThoriumBrotherhood" into "Thorium Brotherhood".</summary>
    public static string Prettify(string key)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            if (i > 0 && char.IsUpper(key[i]) && !char.IsUpper(key[i - 1])) sb.Append(' ');
            sb.Append(key[i]);
        }
        return sb.ToString();
    }

    /// <summary>Turns AtlasCFM.Server.X back into the string constant it stands for.</summary>
    private static string? ServerName(object? entry)
    {
        var text = entry switch
        {
            string s => s,
            LuaExpression e => e.Text,
            _ => null
        };
        if (text is null) return null;

        const string prefix = "AtlasCFM.Server.";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return text;

        var constant = text[prefix.Length..].Trim();

        var negate = false;
        if (constant.StartsWith("NOT_", StringComparison.Ordinal))
        {
            negate = true;
            constant = constant[4..];
        }

        var strict = false;
        if (constant.StartsWith("STRICT_", StringComparison.Ordinal))
        {
            strict = true;
            constant = constant[7..];
        }

        var name = constant switch
        {
            "TURTLE" => ProfileTurtle,
            "TURTLE1" => ProfileTurtle1,
            "VANILLA_PLUS" => "Vanilla Plus",
            "CLASSIC" => "Classic",
            _ => null
        };
        if (name is null) return null;

        return (negate ? "!" : "") + (strict ? "=" : "") + name;
    }
}
