using OctoBis.Scraper;
using Xunit;

namespace OctoBis.Scraper.Tests;

/// <summary>
/// Tooltip fragments here are trimmed copies of real pages. The assertions pin down the behaviours
/// that were actually wrong at some point during development: slot resolution, bonus weapon damage
/// folding into dps, penalties written as "+-25", and set blocks staying out of the stat table.
/// </summary>
public class ItemPageParserTests
{
    private const string NemesisGloves = """
        <td><table><tr><td><b class="q4">Nemesis Gloves</b><br />Binds when picked up<table width="100%"><tr><td>Hands</td><th>Cloth</th></tr></table>72 Armor<br />+15 Intellect<br />+20 Stamina<br />+10 Shadow Resistance<br />Durability 35 / 35<br />Classes: Warlock<br />Requires Level 60<br /></td></tr></table><table><tr><td><span class="q2">Equip: <a href="?spell=21347" class="q2">Restores 4 health per 5 sec.</a></span><br /><span class="q2">Equip: <a href="?spell=14799" class="q2">Increases damage and healing done by magical spells and effects by up to 20.</a></span><br /><span class="q2">Equip: <a href="?spell=23727" class="q2">Improves your chance to hit with spells by 1%.</a></span><br /><span class="q"><a href="?itemset=212" class="q">Nemesis Raiment</a> (0/8)</span><div class="q0 indent"><span><a href="?item=16933">Nemesis Belt</a></span><br /><span><a href="?item=16931">Nemesis Robes</a></span><br /></div><span class="q0"><span>(3) Set: <a href="?spell=14047">Increases damage and healing done by magical spells and effects by up to 23.</a></span><br /></span></td></tr></table>
        """;

    [Fact]
    public void ParsesArmourStatsClassRestrictionAndSet()
    {
        var item = ItemPageParser.Parse(NemesisGloves, 16928);

        Assert.NotNull(item);
        Assert.Equal("Nemesis Gloves", item!.Name);
        Assert.Equal(4, item.Quality);
        Assert.Equal(10, item.Slot);            // Hands
        Assert.Equal("cloth", item.SubClassName);
        Assert.Equal(60, item.ReqLevel);
        Assert.Equal(new[] { "warlock" }, item.Classes);
        Assert.Equal(212, item.SetId);
        Assert.Equal("Nemesis Raiment", item.SetName);

        Assert.Equal(72, item.Stats["armor"]);
        Assert.Equal(15, item.Stats["int"]);
        Assert.Equal(20, item.Stats["sta"]);
        Assert.Equal(10, item.Stats["resShadow"]);
        Assert.Equal(20, item.Stats["spellPower"]);
        Assert.Equal(1, item.Stats["spellHit"]);
    }

    [Fact]
    public void SetPieceListAndSetBonusesDoNotLeakIntoUnmatchedLines()
    {
        var unmatched = new List<string>();
        ItemPageParser.Parse(NemesisGloves, 16928, unmatched);

        Assert.DoesNotContain(unmatched, line => line.Contains("Nemesis Robes"));
        Assert.DoesNotContain(unmatched, line => line.Contains("Set:"));
        Assert.DoesNotContain(unmatched, line => line.Contains("Nemesis Raiment"));
    }

    [Fact]
    public void FoldsBonusElementalDamageIntoWeaponDps()
    {
        // Thunderfury: 53.9 dps on the tooltip, plus 16-30 nature at speed 1.90 -> 53.9 + 23/1.9.
        const string html = """
            <td><table><tr><td><b class="q5">Thunderfury, Blessed Blade of the Windseeker</b><br />Binds when picked up<br />Unique<table width="100%"><tr><td>One-hand</td><th>Sword</th></tr></table><table width="100%"><tr><td>44 - 115  Damage</td><th>Speed 1.90</th></tr></table>+16 - 30 Nature Damage<br />(53.9 damage per second)<br />+5 Agility<br />+8 Stamina<br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 19019)!;

        Assert.Equal(13, item.Slot);                  // One-hand
        Assert.Equal("sword", item.SubClassName);
        Assert.Equal(1.9, item.Stats["weaponSpeed"]);
        Assert.Equal(66.01, item.Stats["weaponDps"], 2);
        Assert.Equal(5, item.Stats["agi"]);
    }

    [Fact]
    public void DerivesDpsFromDamageRangeWhenTheTooltipOmitsIt()
    {
        const string html = """
            <td><table><tr><td><b class="q3">Test Blade</b><table width="100%"><tr><td>Two-hand</td><th>Axe</th></tr></table><table width="100%"><tr><td>100 - 200  Damage</td><th>Speed 3.00</th></tr></table>+10 Strength<br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 1)!;

        Assert.Equal(17, item.Slot);                  // Two-hand
        Assert.Equal(50, item.Stats["weaponDps"]);    // (100+200)/2 / 3.00
    }

    [Fact]
    public void ReadsPenaltiesWrittenWithABothSignsPrefix()
    {
        // Corrupted Ashbringer really does render its stamina penalty as "+-25 Stamina".
        const string html = """
            <td><table><tr><td><b class="q4">Corrupted Ashbringer</b><table width="100%"><tr><td>Two-hand</td><th>Sword</th></tr></table>+-25 Stamina<br />+10 Strength<br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 22691)!;

        Assert.Equal(-25, item.Stats["sta"]);
        Assert.Equal(10, item.Stats["str"]);
    }

    [Fact]
    public void RecognisesSchoolSpecificSpellDamageSeparatelyFromGenericSpellPower()
    {
        const string html = """
            <td><table><tr><td><b class="q4">Test Robe</b><table width="100%"><tr><td>Chest</td><th>Cloth</th></tr></table><span class="q2">Equip: <a href="?spell=1">Increases damage done to Fire by magical spells and effects by up to 30.</a></span><br /><span class="q2">Equip: <a href="?spell=2">Increases damage and healing done by magical spells and effects by up to 10.</a></span><br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 2)!;

        Assert.Equal(30, item.Stats["spellDmgFire"]);
        Assert.Equal(10, item.Stats["spellPower"]);
    }

    [Fact]
    public void KeepsUnquantifiableProcTextAsANoteRatherThanDiscardingIt()
    {
        const string html = """
            <td><table><tr><td><b class="q5">Sulfuras, Hand of Ragnaros</b><table width="100%"><tr><td>Two-hand</td><th>Mace</th></tr></table>+12 Strength<br /></td></tr></table><table><tr><td><span class="q2">Chance on hit: <a href="?spell=21162" class="q2">Hurls a fiery ball that causes 273 to 334 Fire damage.</a></span><br /></td></tr></table>
            """;

        var unmatched = new List<string>();
        var item = ItemPageParser.Parse(html, 17182, unmatched)!;

        Assert.Contains(item.Notes, note => note.Contains("Hurls a fiery ball"));
        Assert.Contains(unmatched, line => line.Contains("Hurls a fiery ball"));
    }

    [Fact]
    public void ReturnsNullWhenThePageHasNoItemTooltip()
    {
        Assert.Null(ItemPageParser.Parse("<html><body>Not an item page</body></html>", 1));
    }
}
