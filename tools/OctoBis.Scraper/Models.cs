using System.Text.Json.Serialization;

namespace OctoBis.Scraper;

/// <summary>An equippable item as the site consumes it.</summary>
public sealed class Item
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Quality { get; set; }
    public int ItemLevel { get; set; }
    public int ReqLevel { get; set; }

    /// <summary>WoW inventory type, as reported by the database listviews (1 = head, 2 = neck, ...).</summary>
    public int Slot { get; set; }

    /// <summary>Database item class (2 = weapon, 4 = armor).</summary>
    public int ItemClass { get; set; }

    public int SubClass { get; set; }

    /// <summary>Resolved name of the subclass: "plate", "dagger", "shield", "idol" and so on.</summary>
    public string? SubClassName { get; set; }

    public string? Icon { get; set; }

    /// <summary>Classes the item is restricted to, empty when unrestricted.</summary>
    public List<string> Classes { get; set; } = new();

    public Dictionary<string, double> Stats { get; set; } = new();

    public int? SetId { get; set; }
    public string? SetName { get; set; }

    /// <summary>Effect lines the stat parser did not recognise, kept verbatim so nothing is lost.</summary>
    public List<string> Notes { get; set; } = new();

    // ---- Tooltip presentation ----------------------------------------------------------------
    //
    // Scoring needs numbers; a tooltip needs the sentence the number came from. "Equip: Increases
    // damage and healing done by magical spells and effects by up to 20" becomes spellPower=20 for
    // the ranker, and a player reading the tooltip expects to see the sentence, not "+20 SP". These
    // fields carry the parts of the tooltip that have no stat to become.

    /// <summary>"pickup", "equip", "use" or "quest". Null when the item does not bind.</summary>
    public string? Binding { get; set; }

    public bool Unique { get; set; }

    /// <summary>Maximum durability. Null for items that have none, such as rings and trinkets.</summary>
    public int? Durability { get; set; }

    /// <summary>Bonus weapon damage exactly as written, e.g. "+16 - 30 Nature Damage".</summary>
    public string? BonusDamage { get; set; }

    /// <summary>Equip / Use / Chance on hit lines in tooltip order, wording preserved.</summary>
    public List<ItemEffect> Effects { get; set; } = new();

    /// <summary>Earliest phase in which any source of this item is available.</summary>
    public int MinPhase { get; set; } = int.MaxValue;

    /// <summary>
    /// True for random-suffix gear ("Hero's Buckler of the Whale"). The base item has no fixed
    /// stat line, so it cannot be ranked meaningfully and is dropped from the output.
    /// </summary>
    [JsonIgnore] public bool HasRandomSuffix { get; set; }

    [JsonIgnore] public bool DetailFetched { get; set; }
}

/// <summary>One Equip / Use / Chance-on-hit line, kept as the tooltip words it.</summary>
public sealed class ItemEffect
{
    /// <summary>"equip", "use" or "proc".</summary>
    public string Kind { get; set; } = "";

    public string Text { get; set; } = "";
}

/// <summary>
/// A tier or dungeon set, stored once rather than repeated on every piece.
///
/// The piece list and bonus thresholds are identical for all eight members of a set, so writing
/// them per item would multiply the same text by eight for no gain.
/// </summary>
public sealed class ItemSetInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Piece ids in tooltip order, so the tooltip can list them and mark what you wear.</summary>
    public List<int> Pieces { get; set; } = new();

    /// <summary>Pieces in the complete set, as the tooltip states it ("(0/8)").</summary>
    public int Total { get; set; }

    public List<SetBonus> Bonuses { get; set; } = new();
}

/// <summary>A set bonus and the number of pieces that switches it on.</summary>
public sealed class SetBonus
{
    public int Pieces { get; set; }
    public string Text { get; set; } = "";
}

public enum SourceKind { Drop, Vendor, Quest, Craft, Object, Reputation }

/// <summary>One way of obtaining an item.</summary>
public sealed class ItemSource
{
    public SourceKind Kind { get; set; }

    /// <summary>NPC / object / quest id, depending on Kind.</summary>
    public int SourceId { get; set; }

    public string Name { get; set; } = "";
    public int ZoneId { get; set; }
    public string? ZoneName { get; set; }

    /// <summary>Drop chance as a percentage. Null for vendors, quests and crafts.</summary>
    public double? Percent { get; set; }

    /// <summary>Vendor cost in copper, when known.</summary>
    public long? Cost { get; set; }

    /// <summary>Boss classification reported by the database (3 = boss, 4 = rare).</summary>
    public int Classification { get; set; }

    public int Phase { get; set; } = int.MaxValue;

    /// <summary>Instance this source belongs to, when it came from the Atlas data.</summary>
    public string? Instance { get; set; }

    /// <summary>Encounter order within its instance, for sorting a checklist into pull order.</summary>
    public int? Order { get; set; }

    /// <summary>Token or container this item is exchanged from, when applicable.</summary>
    public int? ContainerId { get; set; }

    /// <summary>
    /// Identity for merging. Atlas knows bosses by name and the website by id, so a source without
    /// an id falls back to its name - that is what lets "Firemaw" from both sources become one row
    /// rather than two.
    /// </summary>
    public string Key => SourceId != 0
        ? $"{Kind}:{SourceId}"
        : $"{Kind}:{Name.Trim().ToLowerInvariant()}";

    public string NameKey => $"{Kind}:{Name.Trim().ToLowerInvariant()}";
}

/// <summary>A phase as declared in config/phases.json.</summary>
public sealed class PhaseConfig
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Date { get; set; }
    public string Status { get; set; } = "";
    public string? Blurb { get; set; }
    public string? Headline { get; set; }
    public List<string> Zones { get; set; } = new();
    public List<string> Npcs { get; set; } = new();
}

/// <summary>An NPC discovered during the crawl.</summary>
public sealed class Npc
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int ZoneId { get; set; }
    public int Classification { get; set; }
    public bool Crawled { get; set; }

    /// <summary>
    /// Phase this NPC was seeded under. Several raid bosses report no zone at all on their drop
    /// listings - Ragnaros, Nefarian, Sapphiron and Kel'Thuzad all come back with location 0 - so
    /// without this their loot would fall through to the launch phase and appear available today.
    /// </summary>
    public int Phase { get; set; } = int.MaxValue;
}
