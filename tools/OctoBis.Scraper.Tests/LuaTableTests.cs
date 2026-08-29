using OctoBis.Scraper;
using Xunit;

namespace OctoBis.Scraper.Tests;

public class LuaTableTests
{
    [Fact]
    public void NumericBracketKeysAreKeysNotArrayPositions()
    {
        // Spells.lua is written entirely this way. Read as unkeyed elements, the spell id is lost -
        // and the spell id is the half that says which recipe the entry belongs to. Without it the
        // crafting tables cannot be resolved to the items they produce.
        const string lua = """
            AtlasCFM.SpellDB = {
                enchants = {
                    [23067] = { item = 9312 },
                    [23068] = { item = 9313 },
                },
            }
            """;

        var table = LuaTable.ParseFile(lua).Values.OfType<LuaTable.Table>().First();
        var enchants = (LuaTable.Table)table.Field("enchants")!;

        Assert.Equal(2, enchants.Fields.Count);
        Assert.Empty(enchants.Array);
        Assert.Equal(9312, ((LuaTable.Table)enchants.Field("23067")!).Int("item"));
        Assert.Equal(9313, ((LuaTable.Table)enchants.Field("23068")!).Int("item"));
    }

    [Fact]
    public void UnkeyedEntriesStillLandInTheArray()
    {
        const string lua = """
            local t = {
                { id = 16928 },
                { id = 16929 },
            }
            """;

        var table = LuaTable.ParseFile(lua).Values.OfType<LuaTable.Table>().First();

        Assert.Equal(2, table.Array.Count);
        Assert.Empty(table.Fields);
    }

    [Fact]
    public void StringBracketKeysStillWork()
    {
        const string lua = """
            local t = { ["Engineering"] = { skill = 300 } }
            """;

        var table = LuaTable.ParseFile(lua).Values.OfType<LuaTable.Table>().First();

        Assert.Equal(300, ((LuaTable.Table)table.Field("Engineering")!).Int("skill"));
    }
}
