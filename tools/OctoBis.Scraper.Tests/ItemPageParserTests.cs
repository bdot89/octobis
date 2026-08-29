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
    public void TemporaryUseEffectsAreNotScoredAsPermanentStats()
    {
        // Manual Crowd Pummeler's 30-second charge parsed as a flat +50% attack speed, which made
        // a level 29 mace outscore every raid weapon in the game.
        const string html = """
            <td><table><tr><td><b class="q3">Manual Crowd Pummeler</b><table width="100%"><tr><td>Two-hand</td><th>Mace</th></tr></table>+16 Strength<br /></td></tr></table><table><tr><td><span class="q2">Use: <a href="?spell=13494">Increases your attack speed by 50% for 30 sec.</a></span><br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 9449)!;

        Assert.False(item.Stats.ContainsKey("haste"));
        Assert.Equal(16, item.Stats["str"]);
        Assert.Contains(item.Notes, note => note.Contains("attack speed by 50%"));
    }

    [Fact]
    public void PermanentPercentageStatsSurviveTheProcFilter()
    {
        // Guards the over-correction: vanilla phrases permanent hit, crit, dodge, parry and block
        // as "chance to ...", so a blanket "chance to" proc filter strips real stats everywhere.
        const string html = """
            <td><table><tr><td><b class="q4">Test Trinket</b><table width="100%"><tr><td>Trinket</td><th>Miscellaneous</th></tr></table></td></tr></table><table><tr><td><span class="q2">Equip: <a href="?spell=1">Improves your chance to hit by 2%.</a></span><br /><span class="q2">Equip: <a href="?spell=2">Improves your chance to get a critical strike by 1%.</a></span><br /><span class="q2">Equip: <a href="?spell=3">Increases your chance to dodge an attack by 1%.</a></span><br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 1)!;

        Assert.Equal(2, item.Stats["hit"]);
        Assert.Equal(1, item.Stats["crit"]);
        Assert.Equal(1, item.Stats["dodge"]);
    }

    [Fact]
    public void CapturesTheTooltipLinesThatCarryNoStat()
    {
        var item = ItemPageParser.Parse(NemesisGloves, 16928)!;

        Assert.Equal("pickup", item.Binding);
        Assert.Equal(35, item.Durability);
        Assert.False(item.Unique);
    }

    [Fact]
    public void KeepsEveryEffectSentenceEvenWhenItAlsoBecameAStat()
    {
        // The three Equip lines all parse into stats. A tooltip still has to show the sentences,
        // so they are kept alongside the numbers rather than replaced by them.
        var item = ItemPageParser.Parse(NemesisGloves, 16928)!;

        Assert.Equal(3, item.Effects.Count);
        Assert.All(item.Effects, e => Assert.Equal("equip", e.Kind));
        Assert.Contains(item.Effects, e => e.Text.StartsWith("Increases damage and healing"));
        Assert.Equal(20, item.Stats["spellPower"]);   // and the stat survived too
    }

    [Fact]
    public void DistinguishesUseAndProcEffectsFromEquipEffects()
    {
        const string html = """
            <td><table><tr><td><b class="q3">Manual Crowd Pummeler</b><br />Binds when equipped<br />Unique<table width="100%"><tr><td>Two-hand</td><th>Mace</th></tr></table></td></tr></table><table><tr><td><span class="q2">Use: <a href="?spell=13494">Increases your attack speed by 50% for 30 sec.</a></span><br /><span class="q2">Chance on hit: <a href="?spell=1">Blasts the target.</a></span><br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 9449)!;

        Assert.Equal("equip", item.Binding);
        Assert.True(item.Unique);
        Assert.Equal(new[] { "use", "proc" }, item.Effects.Select(e => e.Kind));
        Assert.False(item.Stats.ContainsKey("haste"));   // still not scored
    }

    [Fact]
    public void KeepsBonusWeaponDamageWordingAsWellAsTheAveragedNumber()
    {
        const string html = """
            <td><table><tr><td><b class="q5">Thunderfury</b><table width="100%"><tr><td>One-hand</td><th>Sword</th></tr></table><table width="100%"><tr><td>44 - 115  Damage</td><th>Speed 1.90</th></tr></table>+16 - 30 Nature Damage<br />(53.9 damage per second)<br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 19019)!;

        Assert.Equal("+16 - 30 Nature Damage", item.BonusDamage);
        Assert.Equal(23, item.Stats["weaponBonusDmg"]);
    }

    [Fact]
    public void ReadsTheSetBlockPiecesAndBonuses()
    {
        var set = ItemPageParser.ParseSet(NemesisGloves);

        Assert.NotNull(set);
        Assert.Equal(212, set!.Id);
        Assert.Equal("Nemesis Raiment", set.Name);
        Assert.Equal(8, set.Total);                            // from the "(0/8)" counter
        Assert.Equal(new[] { 16933, 16931 }, set.Pieces);
        Assert.Single(set.Bonuses);
        Assert.Equal(3, set.Bonuses[0].Pieces);
        Assert.StartsWith("Increases damage and healing", set.Bonuses[0].Text);
    }

    [Fact]
    public void SetBlockStillDoesNotReachTheStatTable()
    {
        // The set bonus is +23 spell power; the glove's own Equip line is +20. Reading the block as
        // stats would silently inflate every tier piece by its own set bonuses.
        var item = ItemPageParser.Parse(NemesisGloves, 16928)!;

        Assert.Equal(20, item.Stats["spellPower"]);
    }

    [Fact]
    public void FormConditionalAttackPowerIsNotPlainAttackPower()
    {
        // Atiesh states "+420 Attack Power in Cat, Bear, and Dire Bear forms only". Read as plain
        // attack power it made a caster staff the best main hand in the game for a hunter.
        const string html = """
            <td><table><tr><td><b class="q5">Atiesh, Greatstaff of the Guardian</b><table width="100%"><tr><td>Two-hand</td><th>Staff</th></tr></table>+28 Stamina<br /></td></tr></table><table><tr><td><span class="q2">Equip: <a href="?spell=1">+420 Attack Power in Cat, Bear, and Dire Bear forms only.</a></span><br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 22632)!;

        Assert.False(item.Stats.ContainsKey("ap"));
        Assert.Equal(420, item.Stats["apFeral"]);
    }

    [Fact]
    public void UnconditionalAttackPowerIsStillPlainAttackPower()
    {
        const string html = """
            <td><table><tr><td><b class="q4">Test Cloak</b><table width="100%"><tr><td>Back</td><th>Cloth</th></tr></table></td></tr></table><table><tr><td><span class="q2">Equip: <a href="?spell=1">+16 Attack Power.</a></span><br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 1)!;

        Assert.Equal(16, item.Stats["ap"]);
        Assert.False(item.Stats.ContainsKey("apFeral"));
    }

    [Fact]
    public void ShieldsAreIdentifiedAsShields()
    {
        // The row reads "Off Hand | Shield". "Shield" is in both the slot table and the type table,
        // and matching the slot table first threw the type away - leaving no item in the database
        // identifiable as a shield, so the class check could not gate them.
        const string html = """
            <td><table><tr><td><b class="q4">Scaleshield of Obsidian Flight</b><table width="100%"><tr><td>Off Hand</td><th>Shield</th></tr></table>2853 Armor<br />+24 Stamina<br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 33155)!;

        Assert.Equal("shield", item.SubClassName);
        Assert.Equal(22, item.Slot);            // the database really does say Off Hand
        Assert.Equal(2853, item.Stats["armor"]);
    }

    [Fact]
    public void TheSlotColumnStillWinsWhenTheSlotIsUnknown()
    {
        const string html = """
            <td><table><tr><td><b class="q3">Test Blade</b><table width="100%"><tr><td>Two-hand</td><th>Sword</th></tr></table>+10 Strength<br /></td></tr></table>
            """;

        var item = ItemPageParser.Parse(html, 1)!;

        Assert.Equal(17, item.Slot);
        Assert.Equal("sword", item.SubClassName);
    }

    [Fact]
    public void ReturnsNullWhenThePageHasNoItemTooltip()
    {
        Assert.Null(ItemPageParser.Parse("<html><body>Not an item page</body></html>", 1));
    }
}
