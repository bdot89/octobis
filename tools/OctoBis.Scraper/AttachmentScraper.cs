using System.Globalization;
using System.Text.RegularExpressions;

namespace OctoBis.Scraper;

/// <summary>
/// Everything you permanently attach to a piece of gear: enchants, belt buckles and jewelcrafting
/// gems.
///
/// This reads the database directly rather than the Atlas addon's crafting catalogue. Atlas only
/// lists what an enchanter can craft, which misses quest and reputation enchants entirely and
/// records jewelcrafting by spell id rather than item id. The database has all of it - 200-odd
/// enchant spells against Atlas's 153 - and, more usefully, states the actual effect in words that
/// can be turned into stats instead of hand-curated.
/// </summary>
public sealed partial class AttachmentScraper
{
    private readonly AowowClient _client;

    public List<string> Warnings { get; } = new();
    public List<string> Unparsed { get; } = new();

    public AttachmentScraper(AowowClient client) => _client = client;

    public sealed class Attachment
    {
        public int Id { get; init; }
        /// <summary>enchant, buckle or gem - what kind of thing this is.</summary>
        public string Kind { get; init; } = "enchant";
        public string Name { get; set; } = "";
        public string[] Slots { get; set; } = Array.Empty<string>();
        public Dictionary<string, double> Stats { get; } = new();
        public string? Effect { get; set; }
        public bool IsProc { get; set; }
        public bool TwoHandOnly { get; set; }
        public bool ShieldOnly { get; set; }
    }

    /// <summary>Enchant name prefixes mapped to the paperdoll slots they go on.</summary>
    private static readonly (string Prefix, string[] Slots, bool TwoHand, bool Shield)[] EnchantSlots =
    {
        ("Enchant Bracer", new[] { "wrist" }, false, false),
        ("Enchant Boots", new[] { "feet" }, false, false),
        ("Enchant Chest", new[] { "chest" }, false, false),
        ("Enchant Cloak", new[] { "back" }, false, false),
        ("Enchant Gloves", new[] { "hands" }, false, false),
        ("Enchant Shield", new[] { "offhand" }, false, true),
        ("Enchant 2H Weapon", new[] { "mainhand" }, true, false),
        ("Enchant Weapon", new[] { "mainhand", "offhand" }, false, false)
    };

    public async Task<List<Attachment>> LoadAsync()
    {
        var found = new Dictionary<int, Attachment>();

        await CollectEnchantsAsync(found);
        await CollectItemsAsync(found, "Belt Buckle", "buckle", new[] { "waist" });
        await CollectItemsAsync(found, "Gemstone", "gem", new[] { "neck", "finger1", "finger2" });

        // Head and leg enchants are reputation and quest items rather than enchanting recipes, so
        // they only turn up by name.
        foreach (var term in new[] { "Arcanum", "Signet of", "Presence of Sight", "Syncretist", "Falcon's Call" })
            await CollectItemsAsync(found, term, "enchant", new[] { "head", "legs" });

        return found.Values
            .Where(a => a.Slots.Length > 0)
            .OrderBy(a => a.Slots[0])
            .ThenBy(a => a.Name)
            .ToList();
    }

    private async Task CollectEnchantsAsync(Dictionary<int, Attachment> found)
    {
        var html = await _client.GetSearchPageAsync("Enchant");
        var view = ListviewParser.Find(html, "spells");
        if (view is null)
        {
            Warnings.Add("No spell results for \"Enchant\"");
            return;
        }

        foreach (var row in view.Rows)
        {
            var id = JsLiteral.Int(row, "id");
            if (id is null) continue;

            var raw = StripSpellPrefix(JsLiteral.Str(row, "name") ?? "");

            // Skip the recipe items, the developers' leftovers and duplicated test entries.
            if (raw.StartsWith("Formula:", StringComparison.OrdinalIgnoreCase)) continue;
            if (raw.StartsWith("zzOLD", StringComparison.OrdinalIgnoreCase)) continue;
            if (raw.StartsWith("Copy of", StringComparison.OrdinalIgnoreCase)) continue;

            var match = EnchantSlots.FirstOrDefault(e => raw.StartsWith(e.Prefix + " - ", StringComparison.OrdinalIgnoreCase));
            if (match.Prefix is null) continue;

            var attachment = new Attachment
            {
                Id = id.Value,
                Kind = "enchant",
                Name = raw[(match.Prefix.Length + 3)..].Trim(),
                Slots = match.Slots,
                TwoHandOnly = match.TwoHand,
                ShieldOnly = match.Shield
            };

            var page = await _client.GetAsync($"?spell={id}");
            ApplyEffect(attachment, ExtractSpellEffect(page));
            found[id.Value] = attachment;
        }
    }

    private async Task CollectItemsAsync(Dictionary<int, Attachment> found, string term, string kind, string[] slots)
    {
        var html = await _client.GetSearchPageAsync(term);
        var view = ListviewParser.Find(html, "items");
        if (view is null) return;

        foreach (var row in view.Rows)
        {
            var id = JsLiteral.Int(row, "id");
            if (id is null || found.ContainsKey(id.Value)) continue;

            var name = ListviewParser.StripNamePrefix(JsLiteral.Str(row, "name") ?? "");
            if (name.StartsWith("Plans:", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Design:", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Formula:", StringComparison.OrdinalIgnoreCase)) continue;

            var page = await _client.GetItemPageAsync(id.Value);
            if (string.IsNullOrEmpty(page)) continue;

            var effect = ExtractItemEffect(page);
            // Only things that actually attach to gear; the search catches plenty that do not.
            if (effect is null || !AttachesToGear(effect)) continue;

            var attachment = new Attachment { Id = id.Value, Kind = kind, Name = name, Slots = slots };
            ApplyEffect(attachment, effect);

            // The wording says which slots it is for, and it beats the search term's guess.
            attachment.Slots = SlotsFromEffect(effect) ?? slots;
            found[id.Value] = attachment;
        }
    }

    private static bool AttachesToGear(string effect) =>
        effect.Contains("permanently enchant", StringComparison.OrdinalIgnoreCase)
        || effect.Contains("attaches a buckle", StringComparison.OrdinalIgnoreCase)
        || effect.Contains("permanently add", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the slots out of the effect wording, e.g. "a ring or amulet".</summary>
    private static string[]? SlotsFromEffect(string effect)
    {
        var text = effect.ToLowerInvariant();
        if (text.Contains("ring or amulet") || text.Contains("amulet or ring"))
            return new[] { "neck", "finger1", "finger2" };
        if (text.Contains("to your belt") || text.Contains("a belt")) return new[] { "waist" };
        if (text.Contains("head or leg") || text.Contains("leg or head")) return new[] { "head", "legs" };
        if (text.Contains("your leg")) return new[] { "legs" };
        if (text.Contains("your head")) return new[] { "head" };
        return null;
    }

    private static string StripSpellPrefix(string raw) =>
        raw.StartsWith('@') ? raw[1..] : ListviewParser.StripNamePrefix(raw);

    private static string? ExtractSpellEffect(string html)
    {
        var match = SpellEffectRegex().Match(html);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Value).Trim() : null;
    }

    private static string? ExtractItemEffect(string html)
    {
        var match = ItemUseRegex().Match(html);
        if (!match.Success) return null;

        var text = Regex.Replace(match.Groups[1].Value, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    /// <summary>
    /// Turns an effect sentence into stats. Enchant wording is its own dialect - "increase agility
    /// by 15" rather than the "+15 Agility" an item tooltip uses - so it gets its own patterns.
    /// </summary>
    private void ApplyEffect(Attachment attachment, string? effect)
    {
        attachment.Effect = effect;
        if (effect is null) return;

        var result = ParseEffect(effect);

        if (result.IsProc)
        {
            attachment.IsProc = true;
            Unparsed.Add($"{attachment.Name} (proc, not scored): {effect}");
            return;
        }

        foreach (var (key, value) in result.Stats) attachment.Stats[key] = value;
        if (!result.Understood) Unparsed.Add($"{attachment.Name}: {effect}");
    }

    /// <summary>The outcome of reading one effect sentence.</summary>
    /// <param name="Stats">Stats the sentence grants; empty when it grants none this site models.</param>
    /// <param name="IsProc">The effect is timed or conditional, so deliberately not scored.</param>
    /// <param name="Understood">A pattern recognised the sentence, even if it produced no stat.</param>
    public readonly record struct EffectResult(
        Dictionary<string, double> Stats, bool IsProc, bool Understood);

    /// <summary>
    /// Reads one enchant description. Public so the wordings can be tested directly: vanilla says
    /// the same thing a dozen ways - "give 7 Agility", "adds 8 agility", "grant +4 to all stats" -
    /// and any phrasing without a pattern silently yields an enchant worth nothing.
    /// </summary>
    public static EffectResult ParseEffect(string effect)
    {
        var stats = new Dictionary<string, double>();

        // A proc is not a stat. Crusader reads "increase Strength by 100 for 15 sec", and taking
        // that at face value would rank it as the best enchant in the game by a wide margin.
        // Conditional and timed effects are recorded but deliberately left unscored, the same way
        // proc effects on items are.
        if (ProcRegex().IsMatch(effect)) return new EffectResult(stats, true, true);

        var matched = false;

        foreach (var (pattern, key) in Patterns)
        {
            foreach (Match match in pattern.Matches(effect))
            {
                if (!double.TryParse(match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    continue;

                matched = true;

                if (key == "resAll")
                {
                    foreach (var school in new[] { "resFire", "resFrost", "resNature", "resShadow", "resArcane" })
                        stats[school] = stats.GetValueOrDefault(school) + value;
                    continue;
                }

                if (key == "statWord")
                {
                    var resolved = StatWords.GetValueOrDefault(match.Groups["stat"].Value.ToLowerInvariant());
                    if (resolved is not null) stats[resolved] = stats.GetValueOrDefault(resolved) + value;
                    // Health and mana are real effects but not stats this site models, so a match
                    // that resolves to nothing still counts as understood rather than unparsed.
                    continue;
                }

                if (key == "allStats")
                {
                    foreach (var stat in new[] { "str", "agi", "sta", "int", "spi" })
                        stats[stat] = stats.GetValueOrDefault(stat) + value;
                    continue;
                }

                // "+20 shadow damage" is not spell power - it only helps a caster of that school,
                // which is exactly how the item parser stores it, so enchants use the same keys.
                if (key == "spellSchool")
                {
                    var school = match.Groups["school"].Value.ToLowerInvariant();
                    var resolved = "spellDmg" + char.ToUpperInvariant(school[0]) + school[1..];
                    stats[resolved] = stats.GetValueOrDefault(resolved) + value;
                    continue;
                }

                if (key == "school")
                {
                    var school = match.Groups["school"].Value.ToLowerInvariant();
                    var resolved = school switch
                    {
                        "fire" => "resFire", "frost" => "resFrost", "nature" => "resNature",
                        "shadow" => "resShadow", "arcane" => "resArcane", _ => null
                    };
                    if (resolved is not null) stats[resolved] = stats.GetValueOrDefault(resolved) + value;
                    continue;
                }

                stats[key] = stats.GetValueOrDefault(key) + value;
            }
        }

        return new EffectResult(stats, false, matched);
    }

    /// <summary>
    /// Stat names as the older enchant descriptions spell them. Health and mana map to nothing on
    /// purpose: they are flat pools rather than stats, and this site has no base values to add them
    /// to, so counting them would be misleading.
    /// </summary>
    private static readonly Dictionary<string, string?> StatWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stamina"] = "sta", ["agility"] = "agi", ["strength"] = "str",
        ["intellect"] = "int", ["spirit"] = "spi", ["defense"] = "defense",
        ["armor"] = "armor", ["health"] = null, ["mana"] = null,
        ["attack power"] = "ap", ["ranged attack power"] = "rap",
        ["spell damage"] = "spellPower", ["spell power"] = "spellPower",
        ["damage and healing"] = "spellPower", ["healing"] = "healPower",
        ["healing power"] = "healPower", ["mana every 5 sec"] = "mp5",
        ["mana per 5 sec"] = "mp5", ["dodge"] = "dodge", ["block value"] = "blockValue",
        ["fire resistance"] = "resFire", ["frost resistance"] = "resFrost",
        ["nature resistance"] = "resNature", ["shadow resistance"] = "resShadow",
        ["arcane resistance"] = "resArcane", ["all resistances"] = "resAll",
        ["resistances"] = "resAll", ["defense rating"] = "defense",
        ["hit"] = "hit", ["critical strike"] = "crit",
        ["haste"] = "haste", ["parry"] = "parry", ["block"] = "block",
        ["ranged attack power"] = "rap", ["armor penetration"] = "armorPen",
        ["spell penetration"] = "spellPen", ["vampirism"] = "leech",
        // Flat pools, like health and mana: nothing to add them to.
        ["hit points"] = null, ["chance to hit"] = "hit"
    };

    private static readonly (Regex Pattern, string Key)[] Patterns =
    {
        // The commonest phrasing by far: "to give +5 Stamina", "to grant +15 Agility".
        (new(@"\+(?<v>\d+)\s+(?<stat>[A-Za-z][A-Za-z ]{1,24}?)\s*(?:\.|,|$| and )", RegexOptions.IgnoreCase), "statWord"),
        // Older phrasings: "increases the wearer's Stamina by 1", "add 3 to intellect",
        // "the defense skill of the wearer is increased by 1".
        (new(@"(?:increase|increases)(?: the)? (?:wearer's|bearer's|wearer\\u0027s|bearer\\u0027s) (?<stat>\w+) by (?<v>\d+)", RegexOptions.IgnoreCase), "statWord"),
        (new(@"(?:increase|increases) the (?<stat>\w+) of the (?:wearer|bearer) by (?<v>\d+)", RegexOptions.IgnoreCase), "statWord"),
        (new(@"(?<stat>\w+) skill of the (?:wearer|bearer) is increased by (?<v>\d+)", RegexOptions.IgnoreCase), "statWord"),
        (new(@"(?:they |so they |so it )?(?:increase|increases) the (?:wearer|bearer)'?s? (?<stat>\w+) by (?<v>\d+)", RegexOptions.IgnoreCase), "statWord"),
        (new(@"add (?<v>\d+) to (?<stat>\w+)", RegexOptions.IgnoreCase), "statWord"),
        (new(@"resistance to all schools of magic by (?<v>\d+)", RegexOptions.IgnoreCase), "resAll"),
        (new(@"resistance to (?<school>fire|frost|nature|shadow|arcane) by (?<v>\d+)", RegexOptions.IgnoreCase), "school"),
        (new(@"damage and healing done by magical spells and effects by up to (?<v>\d+)", RegexOptions.IgnoreCase), "spellPower"),
        (new(@"healing done by (?:magical )?spells(?: and effects)? by up to (?<v>\d+)", RegexOptions.IgnoreCase), "healPower"),
        (new(@"(?:increase|increases) (?:your )?attack power by (?<v>\d+)", RegexOptions.IgnoreCase), "ap"),
        (new(@"ranged attack power by (?<v>\d+)", RegexOptions.IgnoreCase), "rap"),
        (new(@"(?:increase|increases) (?:your )?agility by (?<v>\d+)", RegexOptions.IgnoreCase), "agi"),
        (new(@"(?:increase|increases) (?:your )?strength by (?<v>\d+)", RegexOptions.IgnoreCase), "str"),
        (new(@"(?:increase|increases) (?:your )?stamina by (?<v>\d+)", RegexOptions.IgnoreCase), "sta"),
        (new(@"(?:increase|increases) (?:your )?intellect by (?<v>\d+)", RegexOptions.IgnoreCase), "int"),
        (new(@"(?:increase|increases) (?:your )?spirit by (?<v>\d+)", RegexOptions.IgnoreCase), "spi"),
        (new(@"(?:increase|increases) (?:your )?defense(?: rating)? by (?<v>\d+)", RegexOptions.IgnoreCase), "defense"),
        (new(@"(?:increase|increases) (?:your |its )?armor by (?<v>\d+)", RegexOptions.IgnoreCase), "armor"),
        // Both guards earn their place: without \b this matched the "25" inside "125 armor",
        // and without the lookbehind it claimed a value the "adds N <stat>" pattern already read.
        (new(@"(?<!adds )\b(?<v>\d+) (?:additional )?armor", RegexOptions.IgnoreCase), "armor"),
        (new(@"all (?:your )?(?:magical )?resistances by (?<v>\d+)", RegexOptions.IgnoreCase), "resAll"),
        (new(@"magical resistances by (?<v>\d+)", RegexOptions.IgnoreCase), "resAll"),
        (new(@"(?<school>fire|frost|nature|shadow|arcane) resistance by (?<v>\d+)", RegexOptions.IgnoreCase), "school"),
        (new(@"chance to hit with spells by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "spellHit"),
        (new(@"chance to hit by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "hit"),
        (new(@"critical strike[^.]{0,30}by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "crit"),
        (new(@"(?:restore|restores|regenerate) (?<v>\d+) mana per 5", RegexOptions.IgnoreCase), "mp5"),
        (new(@"(?:increase|increases) (?:your )?dodge[^.]{0,20}by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "dodge"),
        (new(@"block value[^.]{0,20}by (?<v>\d+)", RegexOptions.IgnoreCase), "blockValue"),
        (new(@"(?:increase|increases) (?:your )?healing (?:power )?by (?:up to )?(?<v>\d+)", RegexOptions.IgnoreCase), "healPower"),
        (new(@"spell damage[^.]{0,20}by (?:up to )?(?<v>\d+)", RegexOptions.IgnoreCase), "spellPower"),

        // ---- Wordings the first pass missed -----------------------------------------------
        //
        // Everything below was found by listing the enchants that came back with no stats at all
        // and reading their own descriptions. Vanilla words the same effect a dozen ways -
        // "give 7 Agility", "adds 8 agility", "grant +4 to all stats" - and each phrasing that has
        // no pattern silently produces an enchant worth nothing.

        // "give 7 Agility", "give 15 fire resistance", "give 3 Stamina". No "by", so none of the
        // "... by N" patterns above can reach these.
        (new(@"(?:give|gives) (?<v>\d+) (?<stat>[A-Za-z][A-Za-z ]{1,22}?)(?:\.|,|$)", RegexOptions.IgnoreCase), "statWord"),
        (new(@"(?:give|gives|grant|grants) (?<v>\d+) to all resistances", RegexOptions.IgnoreCase), "resAll"),

        // Head and leg enchants, and the Zandalar shoulder signets: "Permanently adds N <stat>".
        (new(@"adds\s+\+?(?<v>\d+)\s+(?<stat>[A-Za-z][A-Za-z ]{1,22}?)(?=\s*(?:,|\.|$)|\s+and\s|\s+to\s+a\s)", RegexOptions.IgnoreCase), "statWord"),
        // Continuations of the same list: "adds 24 Ranged Attack Power, 10 Stamina, and 1% ...".
        // Only the first entry follows the word "adds", so the rest need their own pattern.
        (new(@",\s*(?<v>\d+)\s+(?<stat>[A-Za-z][A-Za-z ]{1,22}?)(?=\s*(?:,|\.|$)|\s+and\s|\s+to\s+a\s)", RegexOptions.IgnoreCase), "statWord"),
                (new(@"adds\s+\+?(?<v>[\d.]+)% (?<stat>dodge|haste|parry|block)", RegexOptions.IgnoreCase), "statWord"),
        // "Permanently adds +8 to your Healing and Damage from spells"
        (new(@"adds\s+\+?(?<v>\d+) to your healing and damage from spells", RegexOptions.IgnoreCase), "spellPower"),
        // "Permanently adds 18 to all healing and damage spells"
        (new(@"adds\s+\+?(?<v>\d+) to all healing and damage spells", RegexOptions.IgnoreCase), "spellPower"),
        // Zandalar Signet of Mojo says "effects up to 18" - no "by", so the pattern above misses it.
        (new(@"damage and healing done by magical spells and effects up to (?<v>\d+)", RegexOptions.IgnoreCase), "spellPower"),
        (new(@"healing done by spells and effects up to (?<v>\d+)", RegexOptions.IgnoreCase), "healPower"),

        // School-specific spell damage: the Power glove enchants, the caster gemstones, and the
        // two-handed weapon enchants.
        (new(@"(?<school>fire|frost|nature|shadow|arcane|holy) damage by (?:up to )?(?<v>\d+)", RegexOptions.IgnoreCase), "spellSchool"),
        (new(@"up to (?<v>\d+) additional (?<school>fire|frost|nature|shadow|arcane|holy) damage when casting", RegexOptions.IgnoreCase), "spellSchool"),

        (new(@"(?:increase|increases) spell power by (?<v>\d+)", RegexOptions.IgnoreCase), "spellPower"),
        (new(@"(?:increase|increases) (?:the caster's healing spells|the effects of your healing spells) by (?:up to )?(?<v>\d+)", RegexOptions.IgnoreCase), "healPower"),

        // "+4 to all stats", "increase All stats by 3".
        (new(@"(?:grant|grants|give|gives) \+?(?<v>\d+) to all stats", RegexOptions.IgnoreCase), "allStats"),
        (new(@"(?:increase|increases) all stats by (?<v>\d+)", RegexOptions.IgnoreCase), "allStats"),

        (new(@"(?:increase|increases) (?:your )?defense skill by (?<v>\d+)", RegexOptions.IgnoreCase), "defense"),
        (new(@"(?:increase|increases) (?:your )?armor penetration by (?<v>\d+)", RegexOptions.IgnoreCase), "armorPen"),
        (new(@"(?:increase|increases) (?:your )?vampirism by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "leech"),
        (new(@"(?:increase|increases) block chance by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "block"),
        (new(@"(?:give|gives|grant|grants) \+?(?<v>[\d.]+)% chance to block", RegexOptions.IgnoreCase), "block"),
        (new(@"(?:give|gives|grant|grants) a (?<v>[\d.]+)% chance to dodge", RegexOptions.IgnoreCase), "dodge"),
        (new(@"(?:increase|increases) (?:your )?arcane magic resistance by (?<v>\d+)", RegexOptions.IgnoreCase), "resArcane"),
        (new(@"(?:increase|increases) spell penetration by (?<v>\d+)", RegexOptions.IgnoreCase), "spellPen"),
        // "decreases the magical resistances of your spell targets by 10" - reducing the target's
        // resistance is spell penetration, not resistance of your own.
        (new(@"magical resistances of your spell targets by (?<v>\d+)", RegexOptions.IgnoreCase), "spellPen"),
        (new(@"(?:increase|increases) mana regeneration by (?<v>\d+) every 5", RegexOptions.IgnoreCase), "mp5"),
        (new(@"(?:restore|restores|regenerate) (?<v>\d+) mana every 5", RegexOptions.IgnoreCase), "mp5"),
        (new(@"\+?(?<v>[\d.]+)% attack speed", RegexOptions.IgnoreCase), "haste"),

        // Spell hit and melee hit read almost identically and must not be conflated: an enchant
        // giving spell hit does nothing for a warrior's cap.
        (new(@"(?<v>[\d.]+)% chance to hit with spells", RegexOptions.IgnoreCase), "spellHit"),
        (new(@"(?<v>[\d.]+)% chance to hit(?! with spells)", RegexOptions.IgnoreCase), "hit"),

        // "do 3 additional points of damage" on a weapon. Captured with the same key items use for
        // flat weapon damage, which is carried but not scored - it depends on weapon speed, and an
        // enchant does not know what it will be applied to.
        (new(@"do \+?(?<v>\d+) (?:additional points? of )?damage(?!\s+(?:to|against))", RegexOptions.IgnoreCase), "flatMeleeDamage")
    };

    /// <summary>
    /// Proc wording. "chance to" is deliberately narrowed: permanent hit and crit read as
    /// "chance to hit" and "chance to get a critical strike", and matching those as procs throws
    /// away real stats.
    /// </summary>
    [GeneratedRegex(@"for \d+ sec|sometimes|occasionally|often|when struck|on hit|temporarily|chance to (?!hit\b|get a critical|dodge|parry|block|resist|crit)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex ProcRegex();

    [GeneratedRegex(@"Permanently[^<]{10,220}")] private static partial Regex SpellEffectRegex();
    [GeneratedRegex(@"Use:\s*(.{0,300}?)</span>", RegexOptions.Singleline)] private static partial Regex ItemUseRegex();
}
