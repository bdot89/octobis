using OctoBis.Scraper;
using Xunit;

namespace OctoBis.Scraper.Tests;

/// <summary>
/// The listview payloads are JavaScript object literals, not JSON, and the awkward shapes below are
/// all taken verbatim from real OctoWoW pages. They are the reason the reader is hand-written.
/// </summary>
public class ListviewParserTests
{
    [Fact]
    public void ReadsDropTableWithEscapedApostropheAndNegativePercent()
    {
        const string html = """
            new Listview({template:'item',id:'drop',name: LANG.tab_drops,tabs:tabsRelated,
            extraCols:[Listview.extraCols.percent,Listview.funcBox.createSimpleCol('group', 'group', '10%', 'group')],
            hiddenCols:['source'],sort: ['-percent','name'],data: [
            {name: '5Narain\'s Scrying Goggles',description: '',level: 1,classs: 12,subclass: 0,percent: -1,id: 20951},
            {name: '1Essence of the Firelord',description: '',level: 1,classs: 7,subclass: 0,percent: -100,id: 19017},
            {name: '4Essence of Fire',description: '',level: 55,classs: 5,subclass: 0,stack:[1,9],percent: 40,id: 7078}]});
            """;

        var view = ListviewParser.Find(html, "drop");

        Assert.NotNull(view);
        Assert.Equal("item", view!.Template);
        Assert.Equal(3, view.Rows.Count);

        Assert.Equal("Narain's Scrying Goggles",
                     ListviewParser.StripNamePrefix(JsLiteral.Str(view.Rows[0], "name")!));

        Assert.Equal(-100, JsLiteral.Num(view.Rows[1], "percent"));
        Assert.Equal(7078, JsLiteral.Int(view.Rows[2], "id"));
    }

    [Fact]
    public void ReadsSparseArraysWithoutLosingLaterFields()
    {
        // `react: [,]` is a two-hole sparse array. A naive reader either throws here or swallows the
        // rest of the row, taking the id with it.
        const string html = """
            new Listview({template:'npc',id:'dropped-by',data:[
            {name: 'Flamegor',minlevel: 63,maxlevel: 63,type: 2,classification: 3,location: [2677],react: [,],percent: 6.6666666666667,id: 11981}]});
            """;

        var row = ListviewParser.Find(html, "dropped-by")!.Rows.Single();

        Assert.Equal("Flamegor", JsLiteral.Str(row, "name"));
        Assert.Equal(2677, JsLiteral.FirstInt(row, "location"));
        Assert.Equal(3, JsLiteral.Int(row, "classification"));
        Assert.Equal(11981, JsLiteral.Int(row, "id"));
        Assert.Equal(6.67, Math.Round(JsLiteral.Num(row, "percent")!.Value, 2));
    }

    [Fact]
    public void ReadsNegativeNumbersInArrays()
    {
        const string html = "new Listview({template:'npc',id:'x',data:[{react: [-1,-1],stock: -1,id: 5}]});";

        var row = ListviewParser.Find(html, "x")!.Rows.Single();

        Assert.Equal(-1, JsLiteral.FirstInt(row, "react"));
        Assert.Equal(-1, JsLiteral.Num(row, "stock"));
    }

    [Fact]
    public void FindsEveryListviewOnAPage()
    {
        const string html = """
            new Listview({template:'item',id:'drop',data:[{id: 1}]});
            new Listview({template:'item',id:'skinning',data:[{id: 2}]});
            new Listview({template:'spell',id:'abilities',data:[{name: '@Shadow Flame',id: 22539}]});
            """;

        var views = ListviewParser.ParseAll(html);

        Assert.Equal(3, views.Count);
        Assert.Equal(new[] { "drop", "skinning", "abilities" }, views.Select(v => v.Id));
    }

    [Fact]
    public void SurvivesAnUnparseableBlockAndKeepsGoing()
    {
        const string html = """
            new Listview({template:'item',id:'broken',data:[{id:
            new Listview({template:'item',id:'good',data:[{name: '4Nemesis Gloves',id: 16928}]});
            """;

        var good = ListviewParser.ParseAll(html).FirstOrDefault(v => v.Id == "good");

        Assert.NotNull(good);
        Assert.Equal(16928, JsLiteral.Int(good!.Rows.Single(), "id"));
    }

    [Fact]
    public void NamesWithoutAMarkerDigitAreLeftAlone()
    {
        Assert.Equal("Flamegor", ListviewParser.StripNamePrefix("Flamegor"));
    }

    [Fact]
    public void TheMarkerDigitIsNotTheItemQuality()
    {
        // Guards the bug this cost the most time to find: Nemesis Gloves is an epic, and the
        // listview prefixes it with 2. Anything that reads quality from a listview is wrong.
        const string html = "new Listview({template:'item',id:'items',data: [{name: '2Nemesis Gloves',level: 76,classs: 4,subclass: 1,id: 16928}]});";

        var row = ListviewParser.Find(html, "items")!.Rows.Single();

        Assert.Equal("Nemesis Gloves", ListviewParser.StripNamePrefix(JsLiteral.Str(row, "name")!));
        Assert.DoesNotContain("quality", row.Keys);
    }
    [Fact]
    public void ReadsNumbersTheDatabaseWroteAsQuotedStrings()
    {
        // Drop listviews write `id: 11981`; quest listviews write `id: '41409'`. Accepting only the
        // unquoted form discarded every quest reward in the database.
        const string js = """
            new Listview({template:'quest',id:'reward-of',data:[{id: '41409',name: 'Cenarion Shoulders',level: '60',reqlevel:60,itemchoices:[[47331,1],[47339,1]]}]});
            """;

        var view = ListviewParser.ParseAll(js).Single();
        var row = view.Rows.Single();

        Assert.Equal(41409, JsLiteral.Int(row, "id"));
        Assert.Equal(60, JsLiteral.Int(row, "level"));
        Assert.Equal(60, JsLiteral.Int(row, "reqlevel"));   // unquoted still works
        Assert.Equal("Cenarion Shoulders", JsLiteral.Str(row, "name"));
    }

    [Fact]
    public void QuotedNumbersDoNotTurnEveryStringIntoANumber()
    {
        const string js = "new Listview({template:'npc',id:'dropped-by',data:[{id:11981,name:'Flamegor',percent:6.67}]});";

        var row = ListviewParser.ParseAll(js).Single().Rows.Single();

        Assert.Null(JsLiteral.Int(row, "name"));
        Assert.Equal("Flamegor", JsLiteral.Str(row, "name"));
        Assert.Equal(6.67, JsLiteral.Num(row, "percent"));
    }
}
