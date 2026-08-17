using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace OctoBis.Scraper;

/// <summary>
/// Reads an item's stats out of its tooltip.
///
/// The tooltip is plain HTML rather than structured data, so this works by normalising it into
/// lines and matching each line against a table of patterns. Lines that match nothing are kept in
/// <see cref="Item.Notes"/> and counted in the run report - a rising unmatched count is the signal
/// that the server has introduced wording this parser has not seen.
/// </summary>
public static partial class ItemPageParser
{
    /// <summary>Field separator injected where a table cell ends, so cell pairs stay distinguishable.</summary>
    private const char Cell = '\u0001';

    public static Item? Parse(string html, int id, ICollection<string>? unmatchedSink = null)
    {
        var nameMatch = ItemNameRegex().Match(html);
        if (!nameMatch.Success) return null;

        var item = new Item
        {
            Id = id,
            Quality = int.Parse(nameMatch.Groups[1].Value),
            Name = WebUtility.HtmlDecode(nameMatch.Groups[2].Value).Trim(),
            DetailFetched = true,
            HasRandomSuffix = html.Contains("Random Bonuses", StringComparison.Ordinal)
        };

        if (IconRegex().Match(html) is { Success: true } icon)
        {
            var name = icon.Groups[1].Value;
            if (!name.Equals("INV_Misc_QuestionMark", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Temp", StringComparison.OrdinalIgnoreCase))
            {
                item.Icon = name.ToLowerInvariant();
            }
        }

        if (ItemSetRegex().Match(html) is { Success: true } set)
        {
            item.SetId = int.Parse(set.Groups[1].Value);
            item.SetName = WebUtility.HtmlDecode(set.Groups[2].Value).Trim();
        }

        var tooltip = ExtractTooltip(html, nameMatch.Index + nameMatch.Length);
        foreach (var line in Normalise(tooltip))
            ApplyLine(line, item, unmatchedSink);

        DeriveWeaponDps(item);
        return item;
    }

    /// <summary>
    /// Takes the tooltip region: everything after the item name, up to whichever comes first of the
    /// item-set block, an inline script, or a related-content listview.
    ///
    /// The item-set block is cut off deliberately - the set id, name, piece list and set bonuses are
    /// captured separately by regex, and letting them through here would flood the unmatched-line
    /// report with every piece name of every tier set.
    /// </summary>
    private static string ExtractTooltip(string html, int start)
    {
        var end = html.Length;
        // "Random Bonuses" precedes a stack of random-suffix variants ("of the Whale", "of the
        // Bear", ...). Reading past it sums every variant's stats into one item, which is how a
        // level 62 shield ended up claiming 1890 armour.
        foreach (var terminator in new[] { "Random Bonuses", "?itemset=", "new Listview", "<script", "id=\"infobox", "<h2", "lv_comments" })
        {
            var at = html.IndexOf(terminator, start, StringComparison.Ordinal);
            if (at >= 0 && at < end) end = at;
        }
        return html[start..end];
    }

    private static IEnumerable<string> Normalise(string fragment)
    {
        var text = fragment;
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        // Opening block tags must break the line too, not just closing ones. Without this the text
        // before a table runs straight into its first cell - "Binds when picked up" + "Hands" -
        // and the slot cell stops being recognisable.
        text = Regex.Replace(text, @"<(tr|div|p|table)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(tr|div|p|table)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(td|th)>", Cell.ToString(), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        // Cutting the fragment at the item-set marker can leave a half-written opening tag behind.
        text = Regex.Replace(text, @"<[a-zA-Z/][^>]*$", "");
        text = WebUtility.HtmlDecode(text);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Replace("\u00A0", " ").Trim();
            if (line.Length == 0 || line == Cell.ToString()) continue;
            yield return line;
        }
    }

    private static void ApplyLine(string line, Item item, ICollection<string>? unmatched)
    {
        // A line carrying cell separators came from a two-column tooltip row: either
        // "Chest | Cloth" (slot and armour type) or "223 - 372 Damage | Speed 3.70".
        if (line.Contains(Cell))
        {
            var cells = line.Split(Cell, StringSplitOptions.RemoveEmptyEntries)
                            .Select(c => c.Trim())
                            .Where(c => c.Length > 0)
                            .ToArray();

            foreach (var cell in cells)
            {
                if (DamageRangeRegex().Match(cell) is { Success: true } dmg)
                {
                    item.Stats["weaponMinDmg"] = double.Parse(dmg.Groups[1].Value, CultureInfo.InvariantCulture);
                    item.Stats["weaponMaxDmg"] = double.Parse(dmg.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                else if (SpeedRegex().Match(cell) is { Success: true } spd)
                {
                    item.Stats["weaponSpeed"] = double.Parse(spd.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                else if (SlotNames.TryGetValue(cell, out var invType))
                {
                    if (item.Slot == 0) item.Slot = invType;
                }
                else if (TypeNames.Contains(cell))
                {
                    item.SubClassName ??= cell.ToLowerInvariant();
                }
            }
            return;
        }

        if (DpsRegex().Match(line) is { Success: true } dps)
        {
            item.Stats["weaponDpsBase"] = double.Parse(dps.Groups[1].Value, CultureInfo.InvariantCulture);
            return;
        }

        if (ClassesRegex().Match(line) is { Success: true } classes)
        {
            foreach (var name in classes.Groups[1].Value.Split(','))
                item.Classes.Add(name.Trim().ToLowerInvariant());
            return;
        }

        if (RequiresLevelRegex().Match(line) is { Success: true } req)
        {
            item.ReqLevel = int.Parse(req.Groups[1].Value);
            return;
        }

        // Bonus elemental damage on a weapon, e.g. "+16 - 30 Nature Damage".
        if (BonusDamageRegex().Match(line) is { Success: true } bonus)
        {
            var min = double.Parse(bonus.Groups[1].Value, CultureInfo.InvariantCulture);
            var max = double.Parse(bonus.Groups[2].Value, CultureInfo.InvariantCulture);
            Add(item, "weaponBonusDmg", (min + max) / 2);
            return;
        }

        if (ArmorRegex().Match(line) is { Success: true } armor)
        {
            Add(item, "armor", double.Parse(armor.Groups[1].Value, CultureInfo.InvariantCulture));
            return;
        }

        // Plain stat lines: "+15 Intellect", "+10 Shadow Resistance", "+-25 Stamina".
        if (PlainStatRegex().Match(line) is { Success: true } plain &&
            StatNames.TryGetValue(plain.Groups[2].Value.Trim(), out var statKey))
        {
            var raw = plain.Groups[1].Value.TrimStart('+');
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                Add(item, statKey, value);
                return;
            }
        }

        if (EffectRegex().Match(line) is { Success: true } effect)
        {
            var kind = effect.Groups[1].Value;
            var text = effect.Groups[2].Value.Trim();

            // Only "Equip:" grants a stat you actually have. "Use:" and "Chance on hit:" are
            // temporary, and reading them as permanent is badly wrong rather than slightly wrong:
            // Manual Crowd Pummeler's 30-second attack speed charge parsed as a flat +50% haste,
            // which made a level 29 mace outscore every raid weapon in the game.
            var permanent = kind.Equals("Equip", StringComparison.OrdinalIgnoreCase)
                            && !TimedOrConditionalRegex().IsMatch(text);

            if (permanent && ApplyEffect(text, item)) return;

            item.Notes.Add(line);
            unmatched?.Add(line);
            return;
        }

        // Everything remaining is flavour text, binding, durability, set listings and so on.
        if (IsIgnorable(line)) return;

        item.Notes.Add(line);
        unmatched?.Add(line);
    }

    /// <summary>Maps an Equip/Use/Chance-on-hit sentence onto a stat. Returns false if unrecognised.</summary>
    private static bool ApplyEffect(string text, Item item)
    {
        foreach (var (pattern, key, scale) in EffectPatterns)
        {
            var m = pattern.Match(text);
            if (!m.Success) continue;

            var value = double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture) * scale;

            // "spells and attacks" crit counts for both melee and casters, so it lands in both keys
            // rather than forcing every spec's weight table to know about a combined stat.
            if (key == "critAll")
            {
                Add(item, "crit", value);
                Add(item, "spellCrit", value);
                return true;
            }

            var resolved = key;
            if (key == "spellDmgSchool")
            {
                var school = m.Groups["school"].Value.Trim().ToLowerInvariant();
                if (!Schools.Contains(school)) return false;
                resolved = "spellDmg" + char.ToUpperInvariant(school[0]) + school[1..];
            }

            Add(item, resolved, value);
            return true;
        }
        return false;
    }

    /// <summary>Folds weapon damage and speed into a single dps figure the scorer can use.</summary>
    private static void DeriveWeaponDps(Item item)
    {
        if (!item.Stats.TryGetValue("weaponSpeed", out var speed) || speed <= 0) return;

        if (!item.Stats.TryGetValue("weaponDpsBase", out var dps))
        {
            if (!item.Stats.TryGetValue("weaponMinDmg", out var min) ||
                !item.Stats.TryGetValue("weaponMaxDmg", out var max)) return;
            dps = (min + max) / 2 / speed;
        }

        // Bonus elemental damage is applied per swing, so it converts to dps by the same speed.
        if (item.Stats.TryGetValue("weaponBonusDmg", out var bonus))
            dps += bonus / speed;

        item.Stats["weaponDps"] = Math.Round(dps, 2);
        item.Stats.Remove("weaponDpsBase");
    }

    private static void Add(Item item, string key, double value)
        => item.Stats[key] = item.Stats.GetValueOrDefault(key) + value;

    private static bool IsIgnorable(string line)
        => IgnorablePrefixes.Any(p => line.StartsWith(p, StringComparison.OrdinalIgnoreCase))
           || line.Contains(") Set:", StringComparison.Ordinal)
           || DurabilityRegex().IsMatch(line);

    // ---- Pattern tables ----------------------------------------------------------------------

    private static readonly HashSet<string> Schools = new(StringComparer.OrdinalIgnoreCase)
        { "arcane", "fire", "frost", "holy", "nature", "shadow" };

    /// <summary>Tooltip slot wording mapped to the inventory-type ids used everywhere else.</summary>
    private static readonly Dictionary<string, int> SlotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Head"] = 1,
        ["Neck"] = 2,
        ["Shoulder"] = 3,
        ["Shirt"] = 4,
        ["Chest"] = 5,
        ["Waist"] = 6,
        ["Legs"] = 7,
        ["Feet"] = 8,
        ["Wrist"] = 9,
        ["Hands"] = 10,
        ["Finger"] = 11,
        ["Trinket"] = 12,
        ["One-hand"] = 13,
        ["Shield"] = 14,
        ["Ranged"] = 15,
        ["Back"] = 16,
        ["Two-hand"] = 17,
        ["Bag"] = 18,
        ["Tabard"] = 19,
        ["Robe"] = 20,
        ["Main Hand"] = 21,
        ["Off Hand"] = 22,
        ["Held In Off-hand"] = 23,
        ["Projectile"] = 24,
        ["Thrown"] = 25,
        ["Relic"] = 28
    };

    private static readonly HashSet<string> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cloth", "Leather", "Mail", "Plate", "Shield", "Libram", "Idol", "Totem", "Miscellaneous",
        "Axe", "Mace", "Sword", "Dagger", "Polearm", "Staff", "Fist Weapon", "Bow", "Gun",
        "Crossbow", "Wand", "Thrown", "Spear"
    };

    private static readonly Dictionary<string, string> StatNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Strength"] = "str",
        ["Agility"] = "agi",
        ["Stamina"] = "sta",
        ["Intellect"] = "int",
        ["Spirit"] = "spi",
        ["Defense"] = "defense",
        ["Dodge"] = "dodge",
        ["Parry"] = "parry",
        ["Block"] = "block",
        ["Fire Resistance"] = "resFire",
        ["Frost Resistance"] = "resFrost",
        ["Nature Resistance"] = "resNature",
        ["Shadow Resistance"] = "resShadow",
        ["Arcane Resistance"] = "resArcane",
        ["All Resistances"] = "resAll",
        ["Attack Power"] = "ap",
        ["Ranged Attack Power"] = "rap"
    };

    private static readonly string[] IgnorablePrefixes =
    {
        "Binds when", "Unique", "Durability", "Requires ", "Classes:", "Races:", "Quest Item",
        "Conjured Item", "Soulbound", "Item Level", "Cooking", "Fishing", "Charges", "Sell Price"
    };

    /// <summary>Effect sentence patterns, most specific first. 'v' captures the magnitude.</summary>
    private static readonly (Regex Pattern, string Key, double Scale)[] EffectPatterns =
    {
        (new(@"Increases damage done to (?<school>\w+) by magical spells and effects by up to (?<v>\d+)", RegexOptions.IgnoreCase), "spellDmgSchool", 1),
        (new(@"Increases damage done by (?<school>\w+) spells and effects by up to (?<v>\d+)", RegexOptions.IgnoreCase), "spellDmgSchool", 1),
        (new(@"Increases healing done by (?:magical )?spells and effects by up to (?<v>\d+)", RegexOptions.IgnoreCase), "healPower", 1),
        (new(@"Increases damage and healing done by magical spells and effects by up to (?<v>\d+)", RegexOptions.IgnoreCase), "spellPower", 1),
        // Must precede the single-school crit patterns, which would otherwise match it first.
        (new(@"Improves your chance to get a critical strike with spells and attacks by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "critAll", 1),
        (new(@"Improves your chance to get a critical strike with spells by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "spellCrit", 1),
        (new(@"Improves your chance to hit with spells by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "spellHit", 1),
        (new(@"Improves your chance to get a critical strike by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "crit", 1),
        (new(@"Improves your chance to hit by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "hit", 1),
        (new(@"Increases your chance to dodge an attack by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "dodge", 1),
        (new(@"Increases your chance to parry an attack by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "parry", 1),
        (new(@"Increases your chance to block attacks with a shield by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "block", 1),
        (new(@"Increases the block value of your shield by (?<v>\d+)", RegexOptions.IgnoreCase), "blockValue", 1),
        (new(@"Increases your ranged attack power by (?<v>\d+)", RegexOptions.IgnoreCase), "rap", 1),
        (new(@"Increases ranged attack power by (?<v>\d+)", RegexOptions.IgnoreCase), "rap", 1),
        (new(@"Increases attack power by (?<v>\d+)", RegexOptions.IgnoreCase), "ap", 1),
        (new(@"\+(?<v>\d+) Attack Power", RegexOptions.IgnoreCase), "ap", 1),
        (new(@"Restores (?<v>\d+) mana per 5 sec", RegexOptions.IgnoreCase), "mp5", 1),
        // Health regeneration is recognised so it stops showing up as an unmatched line; no spec
        // weights it, so it is carried but never scored.
        (new(@"Restores (?<v>\d+) health per 5 sec", RegexOptions.IgnoreCase), "hp5", 1),
        (new(@"Increases (?:your )?spell penetration by (?<v>\d+)", RegexOptions.IgnoreCase), "spellPen", 1),
        (new(@"Decreases the magical resistances of your spell targets by (?<v>\d+)", RegexOptions.IgnoreCase), "spellPen", 1),
        (new(@"Increased Defense \+(?<v>\d+)", RegexOptions.IgnoreCase), "defense", 1),
        (new(@"Increases defense by (?<v>\d+)", RegexOptions.IgnoreCase), "defense", 1),
        (new(@"Increases your attack speed by (?<v>[\d.]+)%", RegexOptions.IgnoreCase), "haste", 1),
        (new(@"Allows (?<v>\d+)% of your Mana regeneration to continue while casting", RegexOptions.IgnoreCase), "mp5WhileCasting", 1),
        (new(@"Your attacks ignore (?<v>\d+) of the target's armor", RegexOptions.IgnoreCase), "armorPen", 1),
        (new(@"(?<v>\d+)% of damage dealt is returned as healing", RegexOptions.IgnoreCase), "leech", 1),
        (new(@"Adds (?<v>\d+) fire damage to your melee attacks", RegexOptions.IgnoreCase), "flatMeleeDamage", 1)
    };

    [GeneratedRegex(@"<b class=""q(\d)"">([^<]+)</b>")] private static partial Regex ItemNameRegex();
    [GeneratedRegex(@"Icon\.create\('([^']+)'")] private static partial Regex IconRegex();
    [GeneratedRegex(@"\?itemset=(\d+)""[^>]*>([^<]+)</a>")] private static partial Regex ItemSetRegex();
    [GeneratedRegex(@"^([\d.]+)\s*-\s*([\d.]+)\s+Damage$", RegexOptions.IgnoreCase)] private static partial Regex DamageRangeRegex();
    [GeneratedRegex(@"^Speed\s+([\d.]+)$", RegexOptions.IgnoreCase)] private static partial Regex SpeedRegex();
    [GeneratedRegex(@"^\(([\d.]+) damage per second\)$", RegexOptions.IgnoreCase)] private static partial Regex DpsRegex();
    [GeneratedRegex(@"^\+([\d.]+)\s*-\s*([\d.]+)\s+\w+\s+Damage$", RegexOptions.IgnoreCase)] private static partial Regex BonusDamageRegex();
    [GeneratedRegex(@"^([\d.]+) Armor$", RegexOptions.IgnoreCase)] private static partial Regex ArmorRegex();
    // Penalties are written "+-25 Stamina" rather than "-25 Stamina", so the sign has to be optional
    // after the plus as well as instead of it.
    [GeneratedRegex(@"^([+-]?-?[\d.]+) ([A-Za-z ]+)$")] private static partial Regex PlainStatRegex();
    [GeneratedRegex(@"^(Equip|Use|Chance on hit):\s*(.+)$", RegexOptions.IgnoreCase)] private static partial Regex EffectRegex();

    /// <summary>
    /// Wording that marks an effect as temporary or conditional rather than always on.
    ///
    /// "chance to" cannot be a proc marker on its own. Half the permanent percentage stats in
    /// vanilla are phrased that way - chance to hit, to crit, to dodge, parry, block - so matching
    /// it blindly strips real stats off most of the database. Only the combat-log verbs that follow
    /// it ("chance to deal", "chance to strike") indicate an actual proc.
    /// </summary>
    [GeneratedRegex(@"for \d+ sec|sometimes|occasionally|when struck|on hit|next \d+|temporarily|chance to (?!hit\b|get a critical|dodge|parry|block|resist|crit)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex TimedOrConditionalRegex();
    [GeneratedRegex(@"^Classes:\s*(.+)$", RegexOptions.IgnoreCase)] private static partial Regex ClassesRegex();
    [GeneratedRegex(@"^Requires Level (\d+)$", RegexOptions.IgnoreCase)] private static partial Regex RequiresLevelRegex();
    [GeneratedRegex(@"^Durability \d+ / \d+$", RegexOptions.IgnoreCase)] private static partial Regex DurabilityRegex();
}
