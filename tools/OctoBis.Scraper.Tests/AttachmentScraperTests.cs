using OctoBis.Scraper;
using Xunit;

namespace OctoBis.Scraper.Tests;

/// <summary>
/// Enchant descriptions, verbatim from the OctoWoW database.
///
/// Vanilla words the same effect a dozen ways - "give 7 Agility", "adds 8 agility", "grant +4 to
/// all stats", "increase Spell Power by 3" - and a phrasing with no pattern behind it produces an
/// enchant that silently contributes nothing. That is exactly how 111 of 265 attachments ended up
/// scoring zero, so every wording that has ever appeared gets a case here.
/// </summary>
public class AttachmentScraperTests
{
    private static Dictionary<string, double> Stats(string effect)
        => AttachmentScraper.ParseEffect(effect).Stats;

    [Theory]
    // "give N <stat>" - no "by", which is why the original "... by N" patterns all missed it.
    [InlineData("Permanently enchant a cloak to give 7 Agility.", "agi", 7)]
    [InlineData("Permanently enchant a shield to give 3 Stamina.", "sta", 3)]
    [InlineData("Permanently enchant a shield to give 5 Spirit.", "spi", 5)]
    [InlineData("Permanently enchant a cloak to give 15 fire resistance.", "resFire", 15)]
    // "Permanently adds N <stat>" - head, leg and shoulder enchants.
    [InlineData("Permanently adds 8 agility to a leg or head slot item.", "agi", 8)]
    [InlineData("Permanently adds 30 attack power to a shoulder slot item.", "ap", 30)]
    [InlineData("Permanently adds 14 Stamina to a shoulder slot item.", "sta", 14)]
    [InlineData("Permanently adds 20 fire resistance to a leg or head slot item.", "resFire", 20)]
    [InlineData("Permanently adds 1% dodge to a leg or head slot item.", "dodge", 1)]
    [InlineData("Permanently adds 1% haste to a leg or head slot item.", "haste", 1)]
    // Spell power, in each of its wordings.
    [InlineData("Permanently adds +8 to your Healing and Damage from spells to a leg or head slot item.", "spellPower", 8)]
    [InlineData("Permanently enchant a ring or amulet to increase Spell Power by 3.", "spellPower", 3)]
    // Other stats that had no pattern at all.
    [InlineData("Permanently enchant a pair of bracers to increase defense skill by 5.", "defense", 5)]
    [InlineData("Attaches a buckle to your belt that increases your armor penetration by 25.", "armorPen", 25)]
    [InlineData("Permanently enchant a shield to increase its armor by 30.", "armor", 30)]
    [InlineData("Permanently enchant a pair of boots to increase vampirism by 1%.", "leech", 1)]
    [InlineData("Permanently enchant a ring or amulet to increase Block chance by 1%.", "block", 1)]
    [InlineData("Permanently enchant a shield to give +2% chance to block.", "block", 2)]
    [InlineData("Permanently enchant a cloak to increase arcane magic resistance by 15.", "resArcane", 15)]
    [InlineData("Permanently enchant a ring or amulet to increase Spell penetration by 6.", "spellPen", 6)]
    [InlineData("Permanently enchant a ring or amulet to increase Mana regeneration by 2 every 5 seconds.", "mp5", 2)]
    [InlineData("Permanently enchants bracers to restore 4 mana every 5 seconds.", "mp5", 4)]
    [InlineData("Permanently enchant gloves to grant a +1% attack speed bonus.", "haste", 1)]
    [InlineData("Permanently enchant gloves to increase the caster's healing spells by up to 30.", "healPower", 30)]
    [InlineData("Permanently enchants bracers to increase the effects of your healing spells by 24.", "healPower", 24)]
    public void ReadsAStatOutOfEachWording(string effect, string key, double expected)
    {
        Assert.Equal(expected, Stats(effect).GetValueOrDefault(key));
    }

    [Theory]
    // School-specific spell damage belongs to its own school, not to generic spell power - a
    // Shadow priest gets nothing from Fire Power.
    [InlineData("Permanently enchant gloves to increase shadow damage by up to 20.", "spellDmgShadow", 20)]
    [InlineData("Permanently enchant gloves to increase fire damage by up to 20.", "spellDmgFire", 20)]
    [InlineData("Permanently enchant gloves to increase holy damage by up to 20.", "spellDmgHoly", 20)]
    [InlineData("Permanently enchant a Two-Handed Melee Weapon to increase Nature damage by 40.", "spellDmgNature", 40)]
    [InlineData("Permanently enchant a ring or amulet to increase Arcane Damage by 9.", "spellDmgArcane", 9)]
    [InlineData("Permanently enchant a weapon to grant up to 7 additional frost damage when casting frost spells.", "spellDmgFrost", 7)]
    public void SchoolDamageKeepsItsSchool(string effect, string key, double expected)
    {
        var stats = Stats(effect);
        Assert.Equal(expected, stats.GetValueOrDefault(key));
        Assert.False(stats.ContainsKey("spellPower"));
    }

    [Fact]
    public void ZandalarSignetOfMojoIsSpellPower()
    {
        // Reads "effects up to 18", not "effects by up to 18". One missing word, and the enchant
        // was worth nothing.
        var stats = Stats("Permanently adds to a shoulder slot item increased damage and healing done by magical spells and effects up to 18.");

        Assert.Equal(18, stats["spellPower"]);
        Assert.Single(stats);
    }

    [Fact]
    public void PresenceOfSightGivesBothSpellPowerAndSpellHit()
    {
        var stats = Stats("Permanently adds 18 to all healing and damage spells and 1% chance to hit with spells to a leg or head slot item. Does not stack with other enchantments for the selected equipment slot.");

        Assert.Equal(18, stats["spellPower"]);
        Assert.Equal(1, stats["spellHit"]);
        // Spell hit must not land in the melee hit bucket; it does nothing for a warrior's cap.
        Assert.False(stats.ContainsKey("hit"));
    }

    [Fact]
    public void FalconsCallReadsEveryStatInItsCommaList()
    {
        var stats = Stats("Permanently adds 24 Ranged Attack Power, 10 Stamina, and 1% Chance to Hit to a leg or head slot item.");

        Assert.Equal(24, stats["rap"]);
        Assert.Equal(10, stats["sta"]);
        Assert.Equal(1, stats["hit"]);
        Assert.False(stats.ContainsKey("spellHit"));
    }

    [Theory]
    [InlineData("Permanently enchant a piece of chest armor to grant +4 to all stats.", 4)]
    [InlineData("Permanently enchant a ring or amulet to increase All stats by 3.", 3)]
    public void AllStatsExpandsToTheFive(string effect, double expected)
    {
        var stats = Stats(effect);
        foreach (var key in new[] { "str", "agi", "sta", "int", "spi" })
            Assert.Equal(expected, stats[key]);
    }

    [Theory]
    [InlineData("Permanently enchant a cloak to give 5 to all resistances.", 5)]
    [InlineData("Permanently enchant a cloak to give 3 to all resistances.", 3)]
    public void AllResistancesExpandsToTheFiveSchools(string effect, double expected)
    {
        var stats = Stats(effect);
        foreach (var key in new[] { "resFire", "resFrost", "resNature", "resShadow", "resArcane" })
            Assert.Equal(expected, stats[key]);
    }

    [Fact]
    public void SpellPenetrationIsNotResistance()
    {
        // "decreases the magical resistances of your spell targets" reduces the enemy's resistance.
        // Reading it as resistance of your own would be an outright inversion.
        var stats = Stats("Permanently enchant a bracer to decreases the magical resistances of your spell targets by 10.");

        Assert.Equal(10, stats["spellPen"]);
        Assert.False(stats.ContainsKey("resFire"));
    }

    [Theory]
    [InlineData("Permanently enchant a melee weapon so that often when attacking in melee it heals for 75 to 126 and increases Strength by 100 for 15 sec.")]
    [InlineData("Permanently enchant a melee weapon to often strike for 40 additional fire damage.")]
    [InlineData("Permanently enchant a melee weapon to often steal life from the enemy and give it to the wielder.")]
    public void ProcsStayUnscored(string effect)
    {
        var result = AttachmentScraper.ParseEffect(effect);

        Assert.True(result.IsProc);
        Assert.Empty(result.Stats);
    }

    [Theory]
    // Flat pools and profession skills are understood but deliberately model nothing: there are no
    // base health, mana or skill values here to add them to.
    [InlineData("Permanently enchant a piece of chest armor to grant +100 health.")]
    [InlineData("Permanently enchant a piece of chest armor to give +50 mana.")]
    [InlineData("Permanently adds 100 hit points to a leg or head slot item.")]
    [InlineData("Permanently adds 150 mana to a leg or head slot item.")]
    public void PoolsAreRecognisedButContributeNothing(string effect)
    {
        var result = AttachmentScraper.ParseEffect(effect);

        Assert.True(result.Understood);   // recognised, so not reported as a parser gap
        Assert.Empty(result.Stats);
    }

    [Theory]
    // Damage conditional on the target's type is not a stat you always have.
    [InlineData("Permanently enchant a Melee Weapon to do 6 additional points of damage to Beasts.")]
    [InlineData("Permanently enchant a Melee Weapon to do 6 additional damage against Elementals.")]
    public void ConditionalWeaponDamageIsNotCounted(string effect)
    {
        Assert.False(Stats(effect).ContainsKey("flatMeleeDamage"));
    }

    [Fact]
    public void UnconditionalWeaponDamageIsCarried()
    {
        Assert.Equal(5, Stats("Permanently enchant a Melee Weapon to do 5 additional points of damage.")["flatMeleeDamage"]);
        Assert.Equal(9, Stats("Permanently enchant a two-handed melee weapon to do +9 damage.")["flatMeleeDamage"]);
    }

    [Theory]
    // Several patterns can match one sentence, and each match adds to the total - so an overlap
    // silently doubles a stat. These are the sentences most at risk of it.
    [InlineData("Permanently enchant a cloak to give 30 additional armor.", "armor", 30)]
    // Armour has three patterns behind it and every one of these hit two of them at once.
    [InlineData("Permanently enchant a cloak to increase armor by 20.", "armor", 20)]
    [InlineData("Permanently enchant a shield to increase its armor by 30.", "armor", 30)]
    [InlineData("Permanently enchant a ring or amulet to increase Armor by 40.", "armor", 40)]
    [InlineData("Permanently adds 125 armor to a leg or head slot item.", "armor", 125)]
    [InlineData("Permanently enchant a cloak to give a 1% chance to dodge.", "dodge", 1)]
    [InlineData("Permanently enchant a Melee Weapon to do 1 additional point of damage.", "flatMeleeDamage", 1)]
    [InlineData("Permanently enchant a cloak to give 7 Agility.", "agi", 7)]
    [InlineData("Permanently enchant gloves to increase shadow damage by up to 20.", "spellDmgShadow", 20)]
    [InlineData("Permanently enchant a bracer to decreases the magical resistances of your spell targets by 6.", "spellPen", 6)]
    public void OverlappingPatternsDoNotDoubleCount(string effect, string key, double expected)
    {
        Assert.Equal(expected, Stats(effect)[key]);
    }
}
