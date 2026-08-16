namespace OctoBis.Scraper;

/// <summary>
/// Pulls the <c>new Listview({ ... })</c> payloads out of a database page.
///
/// Every related-content block on a page - an NPC's drop table, an item's "dropped by" list, a
/// vendor's stock - is rendered as one of these, so this is the single entry point for structured
/// data from the site.
/// </summary>
public static class ListviewParser
{
    public sealed record Listview(string Id, string Template, List<Dictionary<string, object?>> Rows);

    private const string Marker = "new Listview({";

    public static List<Listview> ParseAll(string html)
    {
        var results = new List<Listview>();
        var search = 0;

        while (true)
        {
            var start = html.IndexOf(Marker, search, StringComparison.Ordinal);
            if (start < 0) break;

            var cursor = start + Marker.Length - 1; // point at the '{' so ParseObject sees it
            Dictionary<string, object?> obj;
            try
            {
                obj = JsLiteral.ParseObject(html, ref cursor);
            }
            catch (Exception)
            {
                // A malformed block should cost us that block, not the rest of the page.
                search = start + Marker.Length;
                continue;
            }

            var rows = new List<Dictionary<string, object?>>();
            foreach (var entry in JsLiteral.Arr(obj, "data") ?? new List<object?>())
                if (entry is Dictionary<string, object?> row)
                    rows.Add(row);

            results.Add(new Listview(
                JsLiteral.Str(obj, "id") ?? "",
                JsLiteral.Str(obj, "template") ?? "",
                rows));

            // Always resume just past this marker rather than at the cursor. A malformed block runs
            // the reader to the end of the document, and resuming at the cursor would swallow every
            // healthy listview after it. Listviews are never nested, so nothing is parsed twice.
            search = start + Marker.Length;
        }

        return results;
    }

    public static Listview? Find(string html, string id)
        => ParseAll(html).FirstOrDefault(lv => lv.Id == id);

    public static Listview? Find(List<Listview> views, string id)
        => views.FirstOrDefault(lv => lv.Id == id);

    /// <summary>
    /// Item rows carry a leading marker digit on the name ("2Nemesis Gloves"). It is tempting to
    /// read that digit as the item quality - it is not. Nemesis Gloves is an epic and prefixed 2,
    /// while Hero's Buckler is genuinely uncommon; the digit tracks something else entirely.
    /// Quality is only trustworthy from the item page's own tooltip, so this just strips the marker.
    /// </summary>
    public static string StripNamePrefix(string raw)
        => raw.Length > 0 && char.IsDigit(raw[0]) ? raw[1..] : raw;
}
